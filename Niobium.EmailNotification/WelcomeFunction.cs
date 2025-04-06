using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Cod.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Niobium.EmailNotification
{
    public class WelcomeFunction(
        ILogger<WelcomeFunction> logger)
    {
        [Function(nameof(Welcome))]
        public void Welcome(
            [ServiceBusTrigger("subscription", AutoCompleteMessages = true, Connection = nameof(ServiceBusOptions))]
            ServiceBusReceivedMessage message,
            CancellationToken cancellationToken)
        {
            if (!message.TryParse(out Subscription? request, out var rawBody))
            {
                logger.LogWarning($"Invalid request: {rawBody}");
                return;
            }

            logger.LogInformation($"Welcome request: {JsonSerializer.Serialize(request)}");
        }
    }
}
