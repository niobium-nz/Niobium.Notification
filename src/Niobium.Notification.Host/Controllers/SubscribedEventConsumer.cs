using Dapr;
using Microsoft.AspNetCore.Mvc;
using Niobium.Messaging;

namespace Niobium.Notification.Host.Controllers
{
    [ApiController]
    [Route(DaprComponents.MessageRoute)]
    public class SubscribedEventConsumer(IExternalEventAdaptor<Subscription, SubscribedEvent> adaptor) : ControllerBase
    {
        [Topic(DaprComponents.ServiceBusPubSub, QueueNames.SubscribedEvent, enableRawPayload: true)]
        [HttpPost(QueueNames.SubscribedEvent)]
        public async Task<IActionResult> ConsumeAsync(SubscribedEvent message, CancellationToken cancellationToken)
        {
            await adaptor.OnEvent(message, cancellationToken);
            return this.NoContent();
        }
    }
}
