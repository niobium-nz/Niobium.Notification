using System.Runtime.InteropServices;

namespace Niobium.Notification
{
    public class Template
    {
        [EntityKey(EntityKeyKind.PartitionKey)]
        public required Guid Tenant { get; set; }

        [EntityKey(EntityKeyKind.RowKey)]
        public required string Channel { get; set; }

        [EntityKey(EntityKeyKind.Timestamp)]
        public DateTimeOffset? Timestamp { get; set; }

        [EntityKey(EntityKeyKind.ETag)]
        public string? ETag { get; set; }

        public string? FromName { get; set; }

        public required string From { get; set; }

        public required string Subject { get; set; }

        public string? FallbackTo { get; set; }

        public required string Blob { get; set; }

        public static string BuildParitionKey(Guid tenant) => tenant.ToString();

        public static string BuildRowKey(string channel) => channel.Trim();
    }
}
