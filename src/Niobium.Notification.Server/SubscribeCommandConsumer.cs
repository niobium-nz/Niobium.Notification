using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Niobium.Messaging.ServiceBus;
using Niobium.Platform.ServiceBus;

namespace Niobium.Notification.Server
{
    public class SubscribeCommandConsumer(
        Func<SubscriptionDomain> domainFactory,
        ILogger<SubscribeCommandConsumer> logger)
    {
        [Function(nameof(SubscribeCommandConsumer))]
        public async Task Run(
            [ServiceBusTrigger("subscribecommand")]
            ServiceBusReceivedMessage message,
            CancellationToken cancellationToken)
        {
            if (!message.TryParse(out SubscribeCommand? evt, out var rawBody))
            {
                logger.LogError($"Failed to parse message {message.MessageId}: {rawBody}");
                return;
            }

            if (evt.Tenant == Guid.Empty)
            {
                logger.LogError($"Failed to process message {message.MessageId} due to invalid tenant: {rawBody}");
                return;
            }

            evt.TryValidate(out var validationState);
            if (!validationState.IsValid)
            {
                logger.LogError($"Validation failed for order evt: {JsonMarshaller.Marshall(validationState.ToDictionary())}");
                return;
            }

            await domainFactory().SubscribeAsync(evt.Tenant!, evt.Campaign, evt.Email, evt.FirstName, evt.LastName, evt.Track, cancellationToken: cancellationToken);
        }
    }
}
