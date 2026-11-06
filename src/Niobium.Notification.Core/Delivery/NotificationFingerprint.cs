using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Niobium.Notification.Delivery
{
    public static class NotificationFingerprint
    {
        public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

        public static string Of(NotifyCommand command)
        {
            JsonElement content = JsonSerializer.SerializeToElement(new
            {
                command.Tenant,
                command.Channel,
                command.Destination,
                command.DestinationDisplayName,
                command.Parameters,
            });
            using MemoryStream stream = new();
            using (Utf8JsonWriter writer = new(stream))
            {
                Write(writer, content);
            }
            return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
        }

        private static void Write(Utf8JsonWriter writer, JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }
                writer.WriteEndObject();
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    Write(writer, item);
                }
                writer.WriteEndArray();
            }
            else
            {
                element.WriteTo(writer);
            }
        }
    }
}
