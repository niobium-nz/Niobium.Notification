namespace Niobium.Notification
{
    public class SubscriptionDomain(Lazy<IRepository<Subscription>> repository, IEnumerable<IDomainEventHandler<IDomain<Subscription>>> eventHandlers)
        : GenericDomain<Subscription>(repository, eventHandlers)
    {
        public async Task SubscribeAsync(Guid tenant, string campaign, string email, string firstName, string? lastName, string? source, string? ip = null, CancellationToken cancellationToken = default)
        {
            _ = this.Initialize(new Subscription
            {
                Belonging = Subscription.BuildBelonging(tenant, campaign),
                Email = email,
                FirstName = firstName,
                LastName = lastName,
                Source = source,
                Subscribed = DateTimeOffset.UtcNow,
                Unsubscribed = null,
                IP = ip,
            });

            await this.SaveAsync(true, cancellationToken: cancellationToken);
        }

        public async Task UnsubscribeAsync(CancellationToken cancellationToken = default)
        {
            var subscription = await this.GetEntityAsync(cancellationToken);
            subscription.Unsubscribed = DateTimeOffset.UtcNow;
            await this.SaveAsync(cancellationToken: cancellationToken);
        }
    }
}
