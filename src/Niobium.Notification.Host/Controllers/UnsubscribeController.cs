using Microsoft.AspNetCore.Mvc;

namespace Niobium.Notification.Host.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class UnsubscribeController(IDomainRepository<SubscriptionDomain, Subscription> repo) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> Action(
            [FromQuery(Name = "email")] string email,
            [FromQuery(Name = "tenant")] Guid tenant,
            [FromQuery(Name = "channel")] string? channel,
            CancellationToken cancellationToken)
        {
            if (String.IsNullOrWhiteSpace(email) || tenant == Guid.Empty)
            {
                return new BadRequestResult();
            }

            if (String.IsNullOrWhiteSpace(channel))
            {
                channel = Constants.DefaultChannel;
            }

            SubscriptionDomain domain = await repo.GetAsync(
                Subscription.BuildPartitionKey(tenant, channel),
                Subscription.BuildRowKey(email),
                cancellationToken: cancellationToken);

            await domain.UnsubscribeAsync(cancellationToken);
            return new OkObjectResult("You've been successfully unsubscribed from this mailing list.");
        }
    }
}
