using Cod;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace Niobium.Notification.Functions
{
    public class Unsubscribe(IRepository<Subscription> repo)
    {
        [Function(nameof(Unsubscribe))]
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequest req,
            [FromQuery(Name = "email")] string email,
            [FromQuery(Name = "tenant")] string tenant,
            [FromQuery(Name = "campaign")] string campaign,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(campaign))
            {
                return new BadRequestResult();
            }

            var subscription = await repo.RetrieveAsync(
                Subscription.BuildPartitionKey(tenant, campaign),
                Subscription.BuildRowKey(email),
                cancellationToken: cancellationToken);

            if (subscription != null)
            {
                subscription.Unsubscribed = DateTimeOffset.UtcNow;
                await repo.UpdateAsync(subscription, cancellationToken: cancellationToken);
            }

            return new OkObjectResult("You've been successfully unsubscribed from this mailing list.");
        }
    }
}
