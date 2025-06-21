using Azure.Messaging.ServiceBus;
using Cod;
using Cod.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace Niobium.Notification.Functions
{
    public class SubscribeCommandConsumer(
        Func<SubscriptionDomain> domainFactory,
        ILogger<SubscribeCommandConsumer> logger)
    {
        [Function(nameof(SubscribeCommandConsumer))]
        public async Task Run(
            [ServiceBusTrigger("subscribecommand", AutoCompleteMessages = true, Connection = nameof(ServiceBusOptions))]
            ServiceBusReceivedMessage message,
            CancellationToken cancellationToken)
        {
            if (!message.TryParse(out SubscribeCommand? evt, out var rawBody))
            {
                logger.LogError($"Failed to parse message {message.MessageId}: {rawBody}");
                return;
            }

            if (string.IsNullOrWhiteSpace(evt.Tenant))
            {
                logger.LogError($"Failed to process message {message.MessageId} due to invalid tenant: {rawBody}");
                return;
            }

            evt.TryValidate(out var validationState);
            if (!validationState.IsValid)
            {
                logger.LogError($"Validation failed for order evt: {JsonSerializer.Serialize(validationState.ToDictionary())}");
                return;
            }

            await domainFactory().SubscribeAsync(evt.Tenant!, evt.Campaign, evt.Email, evt.FirstName, evt.LastName, evt.Source, cancellationToken: cancellationToken);
        }
    }
}
