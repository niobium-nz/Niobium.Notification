using Cod;

namespace Niobium.EmailNotification
{
    public class SubscriptionDomain(Lazy<IRepository<Subscription>> repository, IEnumerable<IDomainEventHandler<IDomain<Subscription>>> eventHandlers)
        : GenericDomain<Subscription>(repository, eventHandlers)
    {
        public async Task SubscribeAsync(string tenant, string campaign, string email, string firstName, string? lastName, string? source, CancellationToken? cancellationToken)
        {
            Initialize(new Subscription
            {
                Belonging = Subscription.BuildBelonging(tenant, campaign),
                Email = email,
                FirstName = firstName,
                LastName = lastName,
                Source = source,
                Subscribed = DateTimeOffset.UtcNow,
                Unsubscribed = null,
            });

            await SaveAsync(true, cancellationToken: cancellationToken ?? CancellationToken.None);
        }
    }
}
