using Azure.Messaging.ServiceBus;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Niobium.Messaging.ServiceBus;
using Niobium.Notification.Delivery;
using Niobium.Platform.ServiceBus;

namespace Niobium.Notification.Server;

public sealed class CaptureNotificationReceiptConsumer(CaptureNotificationReceiptFlow flow)
{
    [Function(nameof(CaptureNotificationReceiptConsumer))]
    public async Task Run([ServiceBusTrigger("capturenotificationreceiptcommand")] ServiceBusReceivedMessage message,
        CancellationToken cancellationToken)
    {
        CaptureNotificationReceiptCommand? command;
        bool parsed;
        try { parsed = message.TryParse(out command, out _); }
        catch (JsonException) { throw new InvalidOperationException("Invalid provider receipt envelope."); }
        if (!parsed || command == null || String.IsNullOrWhiteSpace(message.MessageId) || command.ID != message.MessageId)
            throw new InvalidOperationException("Invalid provider receipt envelope.");
        await flow.RunAsync(command, cancellationToken);
    }
}
