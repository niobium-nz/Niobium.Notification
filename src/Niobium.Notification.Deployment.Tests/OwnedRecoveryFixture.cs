using Azure.Data.Tables;
using Azure.Messaging.ServiceBus;
using Niobium.Notification.Delivery;

namespace Niobium.Notification.Deployment.Tests;

// Raw SDK fault setup belongs only to this test executable. It cannot change an
// arbitrary row: identity comes from the model and each write uses the observed ETag.
internal sealed class OwnedRecoveryFixture(NotifyCommand command, string sender)
{
    internal NotificationSubmission Identity { get; } = NotificationSubmission.Create(command);

    internal void ValidateTemplate(TableEntity template)
    {
        Require(template.PartitionKey == command.Tenant.ToString() && template.RowKey == command.Channel
            && template.GetString("VerificationRun") == command.Tenant.ToString()
            && template.GetString("FallbackTo") == command.Destination
            && template.GetString("From") == sender, "live_template_ownership_required");
    }

    internal void ValidateSubmission(TableEntity row)
    {
        Require(row.PartitionKey == Identity.Tenant && row.RowKey == Identity.ID
            && row.GetInt32(nameof(NotificationSubmission.Version)) == NotificationSubmission.CurrentVersion
            && row.GetString(nameof(NotificationSubmission.Fingerprint)) == Identity.Fingerprint,
            "live_submission_identity_changed");
    }

    internal async Task RestoreLostOutcomeAsync(TableClient table, TableEntity accepted, TableEntity template,
        ServiceBusReceivedMessage wake, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ValidateTemplate(template);
        ValidateSubmission(accepted);
        Require(accepted.GetInt32(nameof(NotificationSubmission.StateCode)) == (int)SubmissionState.Accepted
            && accepted.GetInt32(nameof(NotificationSubmission.OutcomeCode)) == (int)SubmissionOutcome.Accepted
            && accepted.GetInt32(nameof(NotificationSubmission.Attempts)) == 1
            && accepted.GetDateTimeOffset(nameof(NotificationSubmission.FirstSubmission)) != null
            && !String.IsNullOrWhiteSpace(accepted.GetString(nameof(NotificationSubmission.ProviderID)))
            && !String.IsNullOrWhiteSpace(accepted.GetString(nameof(NotificationSubmission.PayloadName)))
            && !String.IsNullOrWhiteSpace(accepted.ETag.ToString()) && accepted.ETag.ToString() != "*",
            "live_acceptance_evidence_required");
        Require(wake.MessageId == command.ID && wake.State == ServiceBusMessageState.Scheduled,
            "live_scheduled_recovery_missing");
        Require(wake.ScheduledEnqueueTime > now.AddSeconds(10), "live_recovery_window_too_short");

        TableEntity uncertain = new(accepted);
        uncertain[nameof(NotificationSubmission.StateCode)] = (int)SubmissionState.Sending;
        uncertain[nameof(NotificationSubmission.OutcomeCode)] = (int)SubmissionOutcome.SubmissionUnknown;
        uncertain[nameof(NotificationSubmission.NextAttempt)] = wake.ScheduledEnqueueTime;
        uncertain[nameof(NotificationSubmission.WriteID)] = Guid.NewGuid().ToString("N");
        uncertain.Remove(nameof(NotificationSubmission.ProviderID));
        // Exactly one conditional attempt. A conflict or unknown response propagates;
        // never retry or force-save a fault after ownership may have changed.
        await table.UpdateEntityAsync(uncertain, accepted.ETag, TableUpdateMode.Replace, cancellationToken);
        TableEntity restored = (await table.GetEntityAsync<TableEntity>(Identity.Tenant, Identity.ID,
            cancellationToken: cancellationToken)).Value;
        ValidateSubmission(restored);
        Require(restored.GetString(nameof(NotificationSubmission.WriteID)) == uncertain.GetString(nameof(NotificationSubmission.WriteID))
            && restored.GetInt32(nameof(NotificationSubmission.StateCode)) == (int)SubmissionState.Sending
            && restored.GetInt32(nameof(NotificationSubmission.OutcomeCode)) == (int)SubmissionOutcome.SubmissionUnknown
            && restored.GetString(nameof(NotificationSubmission.ProviderID)) == null
            && restored.GetString(nameof(NotificationSubmission.PayloadName)) == accepted.GetString(nameof(NotificationSubmission.PayloadName))
            && restored.GetDateTimeOffset(nameof(NotificationSubmission.FirstSubmission)) == accepted.GetDateTimeOffset(nameof(NotificationSubmission.FirstSubmission))
            && restored.GetInt32(nameof(NotificationSubmission.Attempts)) == accepted.GetInt32(nameof(NotificationSubmission.Attempts))
            && restored.GetDateTimeOffset(nameof(NotificationSubmission.NextAttempt)) == wake.ScheduledEnqueueTime,
            "live_uncertain_snapshot_not_verified");
    }

    internal async Task<ServiceBusReceivedMessage> ScheduledWakeAsync(ServiceBusReceiver reader, CancellationToken cancellationToken)
    {
        long next = 0;
        for (int page = 0; page < 100; page++)
        {
            IReadOnlyList<ServiceBusReceivedMessage> messages = await reader.PeekMessagesAsync(100, next, cancellationToken);
            foreach (ServiceBusReceivedMessage message in messages)
            {
                if (message.MessageId == command.ID && message.State == ServiceBusMessageState.Scheduled) return message;
            }
            if (messages.Count == 0) break;
            next = messages[^1].SequenceNumber + 1;
        }
        throw new VerificationFailure("live_scheduled_recovery_missing");
    }

    private static void Require(bool valid, string code) { if (!valid) throw new VerificationFailure(code); }
}

internal sealed class VerificationFailure(string code) : Exception(code);
