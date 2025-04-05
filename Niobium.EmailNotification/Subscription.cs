using Cod;

namespace Niobium.EmailNotification
{
    public class Subscription
    {
        private const char TENANT_SPLITOR = '@';
        private const char SOURCE_SPLITOR = '|';

        [EntityKey(EntityKeyKind.PartitionKey)]
        public required string Tenant { get; set; }

        [EntityKey(EntityKeyKind.RowKey)]
        public required string ID { get; set; }

        [EntityKey(EntityKeyKind.Timestamp)]
        public DateTimeOffset? Timestamp { get; set; }

        [EntityKey(EntityKeyKind.ETag)]
        public string? ETag { get; set; }

        public required string FirstName { get; set; }

        public string? LastName { get; set; }

        public required string Email { get; set; }

        public bool Disabled { get; set; }

        public string GetSource()
        {
            return ID.Split(SOURCE_SPLITOR, 2, StringSplitOptions.RemoveEmptyEntries)[0];
        }

        public void SetSource(string source)
        {
            ID = BuildID(source);
        }

        public string GetFullID()
        {
            return $"{ID}{TENANT_SPLITOR}{Tenant}";
        }

        public static string BuildID(string source)
        {
            return $"{source.Trim().ToUpperInvariant()}{SOURCE_SPLITOR}{Guid.NewGuid()}";
        }
    }
}
