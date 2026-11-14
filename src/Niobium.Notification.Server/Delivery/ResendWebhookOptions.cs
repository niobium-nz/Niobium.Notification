using System.Text.Json;

namespace Niobium.Notification.Server.Delivery;

public sealed class ResendWebhookOptions
{
    // One Key Vault value holds all accepted endpoint keys, including a rotation
    // overlap or multiple provider accounts. Values never enter Bicep parameters.
    public string SigningSecretsJson { get; set; } = String.Empty;

    internal string[] ReadSigningSecrets()
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(SigningSecretsJson)
                ?? throw new InvalidOperationException("Provider webhook signing keys are required.");
        }
        catch (Exception error) when (error is JsonException or ArgumentNullException)
        {
            throw new InvalidOperationException("Valid provider webhook signing key configuration is required.");
        }
    }
}
