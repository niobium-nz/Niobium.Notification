using System.Text.Json;
using Niobium.Notification.Delivery;

namespace Niobium.Notification.Server.Delivery;

internal static class ResendWebhookReader
{
    internal static CaptureNotificationReceiptCommand? Read(ReadOnlyMemory<byte> verifiedBody, string eventID, DateTimeOffset receivedAt)
    {
        using JsonDocument document = JsonDocument.Parse(verifiedBody, new JsonDocumentOptions { MaxDepth = 16 });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out JsonElement type)
            || type.ValueKind != JsonValueKind.String) throw new FormatException("Invalid provider receipt.");
        NotificationDeliveryKind? kind = type.GetString() switch
        {
            "email.sent" => NotificationDeliveryKind.Submitted,
            "email.delivered" => NotificationDeliveryKind.Delivered,
            "email.delivery_delayed" => NotificationDeliveryKind.Delayed,
            "email.bounced" => NotificationDeliveryKind.Bounced,
            "email.failed" => NotificationDeliveryKind.Failed,
            "email.suppressed" => NotificationDeliveryKind.Suppressed,
            "email.complained" => NotificationDeliveryKind.Complained,
            _ => null,
        };
        if (kind == null) return null;
        if (!root.TryGetProperty("created_at", out JsonElement created) || created.ValueKind != JsonValueKind.String
            || !created.TryGetDateTimeOffset(out DateTimeOffset occurredAt)
            || !root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("email_id", out JsonElement email) || email.ValueKind != JsonValueKind.String
            || String.IsNullOrWhiteSpace(email.GetString()) || email.GetString()!.Length > 128)
            throw new FormatException("Invalid provider receipt.");
        NotificationCorrelation? correlation = null;
        if (data.TryGetProperty("tags", out JsonElement tags) && tags.ValueKind == JsonValueKind.Object)
        {
            string? tenant = Tag(tags, "niobium_tenant");
            string? submission = Tag(tags, "niobium_submission");
            string? proof = Tag(tags, "niobium_proof");
            correlation = NotificationCorrelation.FromTags(tenant, submission, proof);
        }
        return CaptureNotificationReceiptCommand.Capture("resend", eventID, email.GetString()!, kind.Value,
            occurredAt, receivedAt, verifiedBody.Span, correlation);
    }

    private static string? Tag(JsonElement tags, string name)
        => tags.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
