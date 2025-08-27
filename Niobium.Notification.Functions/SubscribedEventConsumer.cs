using Azure.Messaging.ServiceBus;
using Niobium.Messaging;
using Niobium.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Niobium.Notification.Functions
{
    public class SubscribedEventConsumer(
        IExternalEventAdaptor<Subscription, SubscribedEvent> adaptor,
        ILogger<SubscribedEventConsumer> logger)
    {
        [Function(nameof(SubscribedEventConsumer))]
        public async Task Run(
            [ServiceBusTrigger("subscribedevent", AutoCompleteMessages = true, Connection = nameof(ServiceBusOptions))]
            ServiceBusReceivedMessage message,
            CancellationToken cancellationToken)
        {
            if (!message.TryParse(out SubscribedEvent? evt, out var rawBody))
            {
                logger.LogError($"Failed to parse message {message.MessageId}: {rawBody}");
                return;
            }

            await adaptor.OnEvent(evt, cancellationToken);
        }
    }
}
