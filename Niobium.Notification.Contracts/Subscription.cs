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

        public string? FirstName { get; set; }

        public string? LastName { get; set; }

        public string? Source { get; set; }

        public required DateTimeOffset Subscribed { get; set; }

        public DateTimeOffset? Unsubscribed { get; set; }

        public string? IP { get; set; }

        public Guid GetTenant() => Guid.Parse(this.Belonging.Split(SPLITOR, 2, StringSplitOptions.RemoveEmptyEntries)[0]);

        public string GetChannel() => this.Belonging.Split(SPLITOR, 2, StringSplitOptions.RemoveEmptyEntries)[1];

        public string GetFullID() => $"{this.Belonging}{SPLITOR}{this.Email}";

        public static string BuildPartitionKey(Guid tenant, string channel) => BuildBelonging(tenant, channel);
        public static string BuildRowKey(string email) => email.Trim().ToLowerInvariant();

        public static string BuildBelonging(Guid tenant, string channel) => $"{tenant}{SPLITOR}{channel.Trim()}";
    }
}
