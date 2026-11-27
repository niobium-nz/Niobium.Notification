using System.Text.Json;
using Azure.Messaging.ServiceBus;

namespace Niobium.Notification.Deployment.Tests;

internal static class RoutedReceiptObservation
{
    internal static async Task<long> CaptureNextSequenceAsync(ServiceBusReceiver reader, CancellationToken cancellationToken)
    {
        // A nonpartitioned queue assigns increasing sequence numbers. An
        // explicit peek returns the first retained message at or above its start.
        // Exponential probes bracket the tail without walking historical pages.
        long? first = await PeekSequenceAsync(reader, 0, cancellationToken);
        if (first == null)
            return 0;

        long last = first.Value;
        long distance = 1;
        long beyond;
        while (true)
        {
            if (last == long.MaxValue)
                throw new VerificationFailure("live_routed_receipt_sequence_invalid");
            beyond = last + Math.Min(distance, long.MaxValue - last);
            long? observed = await PeekSequenceAsync(reader, beyond, cancellationToken);
            if (observed == null)
                break;
            last = observed.Value;
            distance = distance > long.MaxValue / 2 ? long.MaxValue : distance * 2;
        }

        while (beyond - last > 1)
        {
            long middle = last + (beyond - last) / 2;
            long? observed = await PeekSequenceAsync(reader, middle, cancellationToken);
            if (observed == null)
                beyond = middle;
            else
                last = observed.Value;
        }
        return last + 1;
    }

    private static async Task<long?> PeekSequenceAsync(ServiceBusReceiver reader, long start, CancellationToken cancellationToken)
    {
        ServiceBusReceivedMessage? message = await reader.PeekMessageAsync(start, cancellationToken);
        if (message == null)
            return null;
        Require(message.SequenceNumber >= start, "live_routed_receipt_sequence_invalid");
        return message.SequenceNumber;
    }

    internal static async Task ObserveAsync(ServiceBusReceiver reader, long nextSequence, string eventID, NotifyCommand command,
        string submissionID, string providerID, CancellationToken cancellationToken)
    {
        // Inspect metadata on a bounded, test-only queue. Only this run's exact
        // message ID can authorize reading the event body. Peek never settles it.
        JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
        int examined = 0;
        for (int attempt = 0; attempt < 12; attempt++)
        {
            IReadOnlyList<ServiceBusReceivedMessage> messages = await reader.PeekMessagesAsync(100, nextSequence, cancellationToken);
            foreach (ServiceBusReceivedMessage message in messages)
            {
                Require(++examined <= 1000, "live_routed_receipt_scan_limit");
                if (message.MessageId != eventID) continue;
                Require(message.ContentType == "application/json" && message.Body.ToMemory().Length <= 8192,
                    "live_routed_receipt_mismatch");
                NotificationDeliveryEvent? route;
                try { route = JsonSerializer.Deserialize<NotificationDeliveryEvent>(message.Body.ToMemory().Span, json); }
                catch (JsonException) { throw new VerificationFailure("live_routed_receipt_mismatch"); }
                Require(route != null && route.ID == eventID && route.Tenant == command.Tenant
                    && route.NotificationID == command.ID && route.Channel == command.Channel
                    && route.SubmissionID == submissionID && route.ProviderMessageID == providerID
                    && route.Provider == "resend" && route.Kind == NotificationDeliveryKind.Delivered,
                    "live_routed_receipt_mismatch");
                return;
            }
            if (messages.Count != 0)
                nextSequence = messages[^1].SequenceNumber + 1;
            else
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        throw new VerificationFailure("live_routed_receipt_missing");
    }

    private static void Require(bool valid, string code)
    {
        if (!valid) throw new VerificationFailure(code);
    }
}
