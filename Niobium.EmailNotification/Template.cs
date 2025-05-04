using Cod;

namespace Niobium.EmailNotification
{
    public class Template
    {
        [EntityKey(EntityKeyKind.PartitionKey)]
        public required string Tenant { get; set; }

        [EntityKey(EntityKeyKind.RowKey)]
        public required string Campaign { get; set; }

        [EntityKey(EntityKeyKind.Timestamp)]
        public DateTimeOffset? Timestamp { get; set; }

        [EntityKey(EntityKeyKind.ETag)]
        public string? ETag { get; set; }

        public string? FromDisplayName { get; set; }

        public required string FromAddress { get; set; }

        public required string Subject { get; set; }

        public required string Blob { get; set; }

        public static string BuildParitionKey(string tenant)
        {
            return tenant.Trim().ToLowerInvariant();
        }

        public static string BuildRowKey(string campaign)
        {
            return campaign.Trim();
        }
    }
}
