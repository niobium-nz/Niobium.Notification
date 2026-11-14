using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Niobium.Notification.Delivery
{
    /// <summary>Owns the frozen payload format and credential comparison without retaining the credential.</summary>
    public sealed record FrozenNotification(string Json, string SenderDomain, string CredentialFingerprint)
    {
        public static FrozenNotification Create(string json, string senderDomain, string credential)
            => new(json, senderDomain, Hash(credential));

        public bool MatchesCredential(string credential) => this.CredentialFingerprint == Hash(credential);

        public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this);

        public static async Task<FrozenNotification> ReadAsync(Stream content, CancellationToken cancellationToken = default)
            => await JsonSerializer.DeserializeAsync<FrozenNotification>(content, cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("Frozen notification content is invalid.");

        private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
