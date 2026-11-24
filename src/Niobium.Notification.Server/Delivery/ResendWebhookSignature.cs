using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Niobium.Notification.Server.Delivery;

// Provider transport authentication only. Receipt persistence/correlation and
// delivery transitions belong to the receipt/submission domains.
internal sealed class ResendWebhookSignature
{
    internal const int MaximumBodyBytes = 64 * 1024;
    private readonly byte[][] keys;
    private readonly TimeProvider clock;

    internal ResendWebhookSignature(IEnumerable<string> secrets, TimeProvider clock)
    {
        this.clock = clock;
        try
        {
            // Multiple configured keys support a controlled webhook-secret rotation.
            // Never log a configured secret or signature/body on a failed request.
            keys = secrets.Select(secret => secret.StartsWith("whsec_", StringComparison.Ordinal)
                ? Convert.FromBase64String(secret[6..]) : throw new FormatException()).ToArray();
            if (keys.Length is < 1 or > 4 || keys.Any(key => key.Length < 16)) throw new FormatException();
        }
        catch (Exception error) when (error is FormatException or ArgumentException or NullReferenceException)
        {
            throw new InvalidOperationException("Valid provider webhook signing keys are required.");
        }
    }

    internal bool Verify(ReadOnlySpan<byte> body, string? id, string? timestamp, string? signatures)
    {
        if (body.Length is 0 or > MaximumBodyBytes || String.IsNullOrEmpty(id) || id.Length > 128
            || id.Any(value => !Char.IsAsciiLetterOrDigit(value) && value is not ('_' or '-'))
            || String.IsNullOrEmpty(timestamp) || timestamp.Length > 20
            || !Int64.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
            || String.IsNullOrEmpty(signatures) || signatures.Length > 2048)
        {
            return false;
        }
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        if (seconds < now - 300 || seconds > now + 300) return false;

        // Svix/Resend signs the exact received bytes after "id.timestamp.".
        // JSON parsing, reserialization and text normalization must happen later.
        byte[] prefix = Encoding.UTF8.GetBytes(id + "." + timestamp + ".");
        byte[] signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed, 0);
        body.CopyTo(signed.AsSpan(prefix.Length));
        Span<byte> provided = stackalloc byte[32];
        foreach (string candidate in signatures.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!candidate.StartsWith("v1,", StringComparison.Ordinal)) continue;
            bool decoded = Convert.TryFromBase64String(candidate[3..], provided, out int written);
            if (!decoded || written != provided.Length) continue;
            foreach (byte[] key in keys)
            {
                byte[] expected = HMACSHA256.HashData(key, signed);
                bool matches = CryptographicOperations.FixedTimeEquals(expected, provided);
                if (matches) return true;
            }
        }
        return false;
    }
}
