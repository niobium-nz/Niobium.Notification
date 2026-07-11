using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace Niobium.Notification.Functions
{
    public class Unsubscribe(IDomainRepository<SubscriptionDomain, Subscription> repo)
    {
        [Function(nameof(Unsubscribe))]
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequest req,
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

            var domain = await repo.GetAsync(
                Subscription.BuildPartitionKey(tenant, channel),
                Subscription.BuildRowKey(email),
                cancellationToken: cancellationToken);

            await domain.UnsubscribeAsync(cancellationToken);
            return new OkObjectResult("You've been successfully unsubscribed from this mailing list.");
        }
    }
}
