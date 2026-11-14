using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;
using Azure.Data.Tables;
using Azure.Messaging.ServiceBus;
using Azure.Storage;
using Azure.Storage.Blobs;
using Niobium.Notification;
using Niobium.Notification.Delivery;

// This executable is a test fixture, never a Function or a production entry point.
// It observes a real acceptance, then restores only its own conditional snapshot
// to the state left by a lost outcome write. No provider credential is needed.
internal static class Program
{
    private const string Account = "niobiumnotifystdtest";
    private const string BusHost = "niobiumnotify-sb-test.servicebus.windows.net";
    private static readonly List<object> Checks = [];
    private static string? fixtureTenant;
    private static string? operation;
    private static string stage = "configuration";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 1) return 2;
        string? failure = null;
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(8));
            await VerifyAsync(timeout.Token);
        }
        catch (VerificationFailure error) { failure = error.Message; }
        catch (RequestFailedException error) when (error.Status is 401 or 403) { failure = "live_storage_access_denied"; }
        catch (RequestFailedException error) when (error.Status == 404) { failure = "live_storage_resource_missing"; }
        catch (RequestFailedException error) when (error.Status == 412) { failure = "live_storage_condition_failed"; }
        catch (UnauthorizedAccessException) { failure = "live_queue_access_denied"; }
        catch (ServiceBusException error) when (error.Reason == ServiceBusFailureReason.QuotaExceeded) { failure = "live_queue_quota_exceeded"; }
        catch (ServiceBusException error) when (error.Reason == ServiceBusFailureReason.MessagingEntityNotFound) { failure = "live_queue_resource_missing"; }
        catch (OperationCanceledException) { failure = "live_recovery_deadline_exceeded"; }
        catch { failure = "live_recovery_failed_details_suppressed"; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
        await System.IO.File.WriteAllTextAsync(args[0], JsonSerializer.Serialize(new
        {
            commit = Environment.GetEnvironmentVariable("TEST_EXPECTED_COMMIT"),
            checks = Checks, success = failure == null, error = failure,
            fixtureTenant, operation, failureStage = failure == null ? null : stage,
            // Durable evidence must survive late wakes and repeated envelopes.
            // Template fixtures are cleaned by the parent harness; inbox evidence is retained.
            durableEvidenceRetained = operation != null,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failure == null ? 0 : 1;
    }

    private static async Task VerifyAsync(CancellationToken cancellationToken)
    {
        Require(Env("AZURE_ENV_NAME") == "test" && Env("TEST_ENVIRONMENT") == "test"
            && Env("TEST_STORAGE_ACCOUNT") == Account, "live_test_environment_required");
        Require(Regex.IsMatch(Env("TEST_EXPECTED_COMMIT"), "\\A[0-9a-f]{40}\\z"), "live_test_commit_required");
        Uri origin = new(Env("TEST_API_URL"));
        Require(origin.Scheme == "https" && origin.IsDefaultPort && origin.UserInfo == ""
            && origin.AbsolutePath == "/" && origin.Query == "" && origin.Fragment == ""
            && origin.Host.StartsWith("niobiumnotify-func-test.", StringComparison.Ordinal)
            && origin.Host.EndsWith(".azurewebsites.net", StringComparison.Ordinal), "live_test_host_required");
        string connection = Env("TEST_SERVICEBUS_CONNECTION");
        ServiceBusConnectionStringProperties busSettings = ServiceBusConnectionStringProperties.Parse(connection);
        Require(busSettings.FullyQualifiedNamespace == BusHost && String.IsNullOrEmpty(busSettings.EntityPath), "live_test_queue_required");
        string recipient = Env("TEST_NOTIFICATION_RECIPIENT");
        Require(new MailAddress(recipient).Address == recipient && recipient.Length <= 50, "live_test_recipient_required");
        Guid tenant = Guid.Parse(Env("TEST_NOTIFICATION_TENANT"));
        Require(tenant != Guid.Empty, "live_owned_tenant_required");
        fixtureTenant = tenant.ToString();
        operation = "deployment-recovery-" + Guid.NewGuid().ToString("N");
        NotifyCommand command = new()
        {
            ID = operation, Tenant = tenant, Channel = "ContactUs", Destination = recipient,
            Parameters = new() { ["MESSAGE"] = "Notification durable recovery verification in the test environment." },
        };
        NotificationSubmission identity = NotificationSubmission.Create(command);
        TableClientOptions tableOptions = new();
        tableOptions.Retry.MaxRetries = 0;
        TableServiceClient tables = new(new Uri($"https://{Account}.table.core.windows.net"),
            new TableSharedKeyCredential(Account, Env("TEST_STORAGE_KEY")), tableOptions);
        TableClient inbox = tables.GetTableClient(nameof(NotificationSubmission));
        stage = "fixture_ownership";
        TableEntity template = (await tables.GetTableClient(nameof(Template)).GetEntityAsync<TableEntity>(
            tenant.ToString(), "ContactUs", cancellationToken: cancellationToken)).Value;
        Require(template.GetString("VerificationRun") == tenant.ToString()
            && template.GetString("FallbackTo") == recipient
            && template.GetString("From") == Env("TEST_NOTIFICATION_SENDER"), "live_template_ownership_required");
        NullableResponse<TableEntity> existing = await inbox.GetEntityIfExistsAsync<TableEntity>(identity.Tenant, identity.ID, cancellationToken: cancellationToken);
        Require(!existing.HasValue, "live_operation_must_be_new");
        await using ServiceBusClient bus = new(connection, new ServiceBusClientOptions
        {
            TransportType = ServiceBusTransportType.AmqpWebSockets,
            RetryOptions = new() { MaxRetries = 0 },
        });
        await using ServiceBusSender sender = bus.CreateSender("notifycommand");
        await using ServiceBusReceiver reader = bus.CreateReceiver("notifycommand");
        // No receive, complete, abandon, purge or cancellation on the shared queue.
        stage = "queue_send";
        await sender.SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(command))
        {
            MessageId = operation, ContentType = "application/json",
        }, cancellationToken);
        stage = "initial_acceptance";
        TableEntity accepted = await AcceptedAsync(inbox, identity, cancellationToken);
        Require(accepted.GetInt32(nameof(NotificationSubmission.Attempts)) == 1, "live_first_attempt_required");
        string providerID = accepted.GetString(nameof(NotificationSubmission.ProviderID))!;
        string payloadName = accepted.GetString(nameof(NotificationSubmission.PayloadName))!;
        Require(!String.IsNullOrWhiteSpace(providerID) && !String.IsNullOrWhiteSpace(payloadName), "live_acceptance_evidence_required");
        Passed("live_queue_provider_acceptance");

        BlobClient payload = new BlobServiceClient(new Uri($"https://{Account}.blob.core.windows.net"),
            new StorageSharedKeyCredential(Account, Env("TEST_STORAGE_KEY")))
            .GetBlobContainerClient(NotificationSubmission.PayloadContainer).GetBlobClient(payloadName);
        stage = "frozen_payload";
        byte[] originalDigest = SHA256.HashData((await payload.DownloadContentAsync(cancellationToken)).Value.Content.ToArray());
        stage = "scheduled_wake";
        ServiceBusReceivedMessage wake = await ScheduledWakeAsync(reader, operation, cancellationToken);
        Require(wake.ScheduledEnqueueTime > DateTimeOffset.UtcNow.AddSeconds(10), "live_recovery_window_too_short");
        Passed("live_scheduled_recovery_observed");

        // Fault setup is restricted to the unique owned fixture and guarded by the
        // accepted ETag. Preserve the model-owned keys, fingerprint, frozen reference,
        // first-submission boundary and attempt count; only lose the outcome write.
        stage = "owned_fault_setup";
        TableEntity uncertain = new(accepted);
        uncertain[nameof(NotificationSubmission.StateCode)] = (int)SubmissionState.Sending;
        uncertain[nameof(NotificationSubmission.OutcomeCode)] = (int)SubmissionOutcome.SubmissionUnknown;
        uncertain[nameof(NotificationSubmission.NextAttempt)] = wake.ScheduledEnqueueTime;
        uncertain[nameof(NotificationSubmission.WriteID)] = Guid.NewGuid().ToString("N");
        uncertain.Remove(nameof(NotificationSubmission.ProviderID));
        await inbox.UpdateEntityAsync(uncertain, accepted.ETag, TableUpdateMode.Replace, cancellationToken);
        TableEntity restored = (await inbox.GetEntityAsync<TableEntity>(identity.Tenant, identity.ID, cancellationToken: cancellationToken)).Value;
        Require(restored.GetString(nameof(NotificationSubmission.WriteID)) == uncertain.GetString(nameof(NotificationSubmission.WriteID))
            && restored.GetInt32(nameof(NotificationSubmission.StateCode)) == (int)SubmissionState.Sending
            && restored.GetString(nameof(NotificationSubmission.ProviderID)) == null, "live_uncertain_snapshot_not_verified");
        Passed("live_owned_lost_outcome_snapshot");

        // The runner sends nothing further. A distinct deployed invocation must
        // recover through the previously scheduled broker wake and the actual provider.
        stage = "scheduled_recovery";
        TableEntity recovered = await AcceptedAsync(inbox, identity, cancellationToken);
        Require(recovered.GetInt32(nameof(NotificationSubmission.Attempts)) >= 2
            && recovered.GetString(nameof(NotificationSubmission.ProviderID)) == providerID
            && recovered.GetDateTimeOffset(nameof(NotificationSubmission.FirstSubmission)) == accepted.GetDateTimeOffset(nameof(NotificationSubmission.FirstSubmission))
            && recovered.GetString(nameof(NotificationSubmission.PayloadName)) == payloadName
            && recovered.GetString(nameof(NotificationSubmission.Fingerprint)) == accepted.GetString(nameof(NotificationSubmission.Fingerprint)),
            "live_recovery_identity_changed");
        byte[] recoveredDigest = SHA256.HashData((await payload.DownloadContentAsync(cancellationToken)).Value.Content.ToArray());
        Require(CryptographicOperations.FixedTimeEquals(originalDigest, recoveredDigest), "live_frozen_payload_changed");
        Passed("live_same_provider_submission_recovered");
        Passed("live_frozen_payload_and_window_preserved");
    }

    private static async Task<TableEntity> AcceptedAsync(TableClient table, NotificationSubmission identity, CancellationToken cancellationToken)
    {
        while (true)
        {
            NullableResponse<TableEntity> response = await table.GetEntityIfExistsAsync<TableEntity>(identity.Tenant, identity.ID, cancellationToken: cancellationToken);
            if (response.HasValue)
            {
                TableEntity row = response.Value!;
                Require(row.GetInt32(nameof(NotificationSubmission.Version)) == NotificationSubmission.CurrentVersion
                    && row.GetString(nameof(NotificationSubmission.Fingerprint)) == identity.Fingerprint, "live_submission_identity_changed");
                int? state = row.GetInt32(nameof(NotificationSubmission.StateCode));
                Require(state != (int)SubmissionState.Failed && state != (int)SubmissionState.Held, "live_submission_not_recoverable");
                if (state == (int)SubmissionState.Accepted) return row;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private static async Task<ServiceBusReceivedMessage> ScheduledWakeAsync(ServiceBusReceiver reader, string id, CancellationToken cancellationToken)
    {
        long next = 0;
        for (int page = 0; page < 100; page++)
        {
            IReadOnlyList<ServiceBusReceivedMessage> messages = await reader.PeekMessagesAsync(100, next, cancellationToken);
            foreach (ServiceBusReceivedMessage message in messages)
            {
                if (message.MessageId == id && message.State == ServiceBusMessageState.Scheduled) return message;
            }
            if (messages.Count == 0) break;
            next = messages[^1].SequenceNumber + 1;
        }
        throw new VerificationFailure("live_scheduled_recovery_missing");
    }

    private static string Env(string key) => Environment.GetEnvironmentVariable(key) ?? String.Empty;
    private static void Passed(string check) => Checks.Add(new { check, passed = true });
    private static void Require(bool valid, string code) { if (!valid) throw new VerificationFailure(code); }
    private sealed class VerificationFailure(string code) : Exception(code);
}
