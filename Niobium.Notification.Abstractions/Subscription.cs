using Cod;

namespace Niobium.Notification
{
    public class Subscription
    {
        private const char SPLITOR = '|';

        [EntityKey(EntityKeyKind.PartitionKey)]
        public required string Belonging { get; set; }

        [EntityKey(EntityKeyKind.RowKey)]
        public required string Email { get; set; }

        [EntityKey(EntityKeyKind.Timestamp)]
        public DateTimeOffset? Timestamp { get; set; }

        [EntityKey(EntityKeyKind.ETag)]
        public string? ETag { get; set; }

        public required string FirstName { get; set; }

        public string? LastName { get; set; }

        public string? Source { get; set; }

        public required DateTimeOffset Subscribed { get; set; }

        public DateTimeOffset? Unsubscribed { get; set; }

        public string? IP { get; set; }

        public string GetTenant()
        {
            return Belonging.Split(SPLITOR, 2, StringSplitOptions.RemoveEmptyEntries)[0];
        }

        public string GetCampaign()
        {
            return Belonging.Split(SPLITOR, 2, StringSplitOptions.RemoveEmptyEntries)[1];
        }

        public string GetFullID()
        {
            return $"{Belonging}{SPLITOR}{Email}";
        }

        public static string BuildPartitionKey(string tenant, string campaign) => BuildBelonging(tenant, campaign);
        public static string BuildRowKey(string email) => email.Trim().ToLowerInvariant();

        public static string BuildBelonging(string tenant, string campaign)
        {
            return $"{tenant.Trim().ToLowerInvariant()}{SPLITOR}{campaign.Trim()}";
        }
    }
}
