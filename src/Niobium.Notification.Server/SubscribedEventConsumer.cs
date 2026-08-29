using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Niobium.Messaging;
using Niobium.Messaging.ServiceBus;
using Niobium.Platform.ServiceBus;

namespace Niobium.Notification.Server
{
    public class SubscribedEventConsumer(
        IExternalEventAdaptor<Subscription, SubscribedEvent> adaptor,
        ILogger<SubscribedEventConsumer> logger)
    {
        [Function(nameof(SubscribedEventConsumer))]
        public async Task Run(
            [ServiceBusTrigger("subscribedevent")]
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
