using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Niobium.Database.StorageTable;
using Niobium.File;
using Niobium.Notification.Delivery;

namespace Niobium.Notification.Server.Delivery
{
    public sealed class TableNotificationSubmissionStore(IAzureTableClientFactory factory, IFileService files) : INotificationSubmissionStore
    {
        public const string TableName = "NotificationSubmission";
        public const string PayloadContainer = "notification-submissions";

        private async Task<TableClient> TableAsync(CancellationToken cancellationToken)
            => (await factory.CreateClientAsync([DatabasePermissions.Query, DatabasePermissions.Add, DatabasePermissions.Update], TableName, cancellationToken: cancellationToken)).GetTableClient(TableName);

        public async Task<NotificationSubmission?> ReadAsync(string partition, string id, CancellationToken cancellationToken)
        {
            TableClient table = await this.TableAsync(cancellationToken);
            try
            {
                NullableResponse<TableEntity> response = await table.GetEntityIfExistsAsync<TableEntity>(partition, id, cancellationToken: cancellationToken);
                return response.HasValue ? FromEntity(response.Value!) : null;
            }
            catch (RequestFailedException error) when (error.Status == 404)
            {
                return null;
            }
        }

        public async Task<bool> CreateAsync(NotificationSubmission submission, FrozenNotification? payload, CancellationToken cancellationToken)
        {
            TableClient table = await this.TableAsync(cancellationToken);
            await table.CreateIfNotExistsAsync(cancellationToken);
            if (payload != null)
            {
                using MemoryStream stream = new(JsonSerializer.SerializeToUtf8Bytes(payload));
                await files.PutAsync(PayloadContainer, submission.PayloadName, stream, replaceIfExist: false, cancellationToken: cancellationToken);
            }
            try
            {
                await table.AddEntityAsync(ToEntity(submission), cancellationToken);
                return true;
            }
            catch (RequestFailedException error) when (error.Status == 409)
            {
                // Immutable candidate blobs can be orphaned by a losing insert. Never delete
                // one after an ambiguous write; retention must reconcile committed references.
                return false;
            }
        }

        public async Task<NotificationSubmission?> UpdateAsync(NotificationSubmission submission, CancellationToken cancellationToken)
        {
            if (String.IsNullOrWhiteSpace(submission.ETag) || submission.ETag == "*")
            {
                throw new InvalidOperationException("A conditional notification version is required.");
            }
            TableClient table = await this.TableAsync(cancellationToken);
            try
            {
                Response response = await table.UpdateEntityAsync(ToEntity(submission), new ETag(submission.ETag), TableUpdateMode.Replace, cancellationToken);
                return submission with
                {
                    ETag = response.Headers.ETag?.ToString()
                        ?? throw new InvalidOperationException("Notification write returned no version."),
                };
            }
            catch (RequestFailedException error) when (error.Status == 412)
            {
                return null;
            }
        }

        public async Task<FrozenNotification> ReadPayloadAsync(string name, CancellationToken cancellationToken)
        {
            using Stream stream = await files.GetAsync(PayloadContainer, name, createIfNotExist: false, cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("Frozen notification content is unavailable.");
            return await JsonSerializer.DeserializeAsync<FrozenNotification>(stream, cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("Frozen notification content is invalid.");
        }

        private static TableEntity ToEntity(NotificationSubmission value) => new(value.PartitionKey, value.RowKey)
        {
            [nameof(value.Version)] = value.Version,
            [nameof(value.Fingerprint)] = value.Fingerprint,
            [nameof(value.PayloadName)] = value.PayloadName,
            [nameof(value.State)] = value.State,
            [nameof(value.FirstSubmission)] = value.FirstSubmission,
            [nameof(value.NextAttempt)] = value.NextAttempt,
            [nameof(value.Attempts)] = value.Attempts,
            [nameof(value.ProviderID)] = value.ProviderID,
            [nameof(value.Outcome)] = value.Outcome,
        };

        private static NotificationSubmission FromEntity(TableEntity entity) => new()
        {
            PartitionKey = entity.PartitionKey,
            RowKey = entity.RowKey,
            ETag = entity.ETag.ToString(),
            Version = entity.GetInt32(nameof(NotificationSubmission.Version)) ?? 0,
            Fingerprint = entity.GetString(nameof(NotificationSubmission.Fingerprint)),
            PayloadName = entity.GetString(nameof(NotificationSubmission.PayloadName)),
            State = entity.GetString(nameof(NotificationSubmission.State)),
            FirstSubmission = entity.GetDateTimeOffset(nameof(NotificationSubmission.FirstSubmission)),
            NextAttempt = entity.GetDateTimeOffset(nameof(NotificationSubmission.NextAttempt)),
            Attempts = entity.GetInt32(nameof(NotificationSubmission.Attempts)) ?? 0,
            ProviderID = entity.GetString(nameof(NotificationSubmission.ProviderID)),
            Outcome = entity.GetString(nameof(NotificationSubmission.Outcome)),
        };
    }
}
