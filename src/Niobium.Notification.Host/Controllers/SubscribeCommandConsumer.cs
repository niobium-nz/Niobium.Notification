using Dapr;
using Microsoft.AspNetCore.Mvc;

namespace Niobium.Notification.Host.Controllers
{
    [ApiController]
    [Route(DaprComponents.MessageRoute)]
    public class SubscribeCommandConsumer(Func<SubscriptionDomain> domainFactory, ILogger<SubscribeCommandConsumer> logger) : ControllerBase
    {
        [Topic(DaprComponents.ServiceBusPubSub, QueueNames.SubscribeCommand, enableRawPayload: true)]
        [HttpPost(QueueNames.SubscribeCommand)]
        public async Task<IActionResult> ConsumeAsync(SubscribeCommand message, CancellationToken cancellationToken)
        {
            message.TryValidate(out ValidationState? validationState);
            if (!validationState.IsValid)
            {
                logger.LogError($"Validation failed for order message: {JsonMarshaller.Marshall(validationState.ToDictionary())}");
                return this.BadRequest(validationState);
            }

            await domainFactory().SubscribeAsync(message.Tenant!, message.Campaign, message.Email, message.FirstName, message.LastName, message.Track, cancellationToken: cancellationToken);
            return this.NoContent();
        }
    }
}
