using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Niobium.Messaging.ServiceBus;
using Niobium.Platform.ServiceBus;

namespace Niobium.Notification.Functions
{
    public class NotifyCommandConsumer(
        NotificationFlow flow,
        ILogger<NotifyCommandConsumer> logger)
    {
        [Function(nameof(NotifyCommandConsumer))]
        public async Task Run(
            [ServiceBusTrigger("notifycommand", AutoCompleteMessages = true, Connection = nameof(ServiceBusTriggerOptions))]
            ServiceBusReceivedMessage message,
            CancellationToken cancellationToken)
        {
            if (!message.TryParse(out NotifyCommand? evt, out var rawBody))
            {
                logger.LogError($"Failed to parse message {message.MessageId}: {rawBody}");
                return;
            }

            _ = evt.TryValidate(out var validationState);
            if (!validationState.IsValid)
            {
                logger.LogError($"Validation failed for order evt: {JsonMarshaller.Marshall(validationState.ToDictionary())}");
                return;
            }

            await flow.RunAsync(evt, cancellationToken);
        }
    }
}
