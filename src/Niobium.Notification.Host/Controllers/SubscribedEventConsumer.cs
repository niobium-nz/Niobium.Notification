using Dapr;
using Microsoft.AspNetCore.Mvc;
using Niobium.Messaging;

namespace Niobium.Notification.Host.Controllers
{
    [ApiController]
    [Route(DaprComponents.MessageRoute)]
    public class SubscribedEventConsumer(IExternalEventAdaptor<Subscription, SubscribedEvent> adaptor, ILogger<SubscribedEventConsumer> logger) : ControllerBase
    {
        [Topic(DaprComponents.ServiceBusPubSub, QueueNames.SubscribedEvent, enableRawPayload: true)]
        [HttpPost(QueueNames.SubscribedEvent)]
        public async Task<IActionResult> ConsumeAsync(HttpRequest req, CancellationToken cancellationToken)
        {
            SubscribedEvent? message = await req.ReadFromJsonAsync<SubscribedEvent>(cancellationToken: cancellationToken);
            if (message == null)
            {
                logger.LogError("Failed to parse message.");
                return this.BadRequest();
            }

            await adaptor.OnEvent(message, cancellationToken);
            return this.NoContent();
        }
    }
}
