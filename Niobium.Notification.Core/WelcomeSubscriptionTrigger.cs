using Cod;
using Cod.Messaging;

namespace Niobium.Notification
{
    internal class WelcomeSubscriptionTrigger(IMessagingBroker<Subscription> queue) : DomainEventHandler<SubscriptionDomain, EntityChangedEvent<Subscription>>
    {
        public async override Task HandleCoreAsync(EntityChangedEvent<Subscription> e, CancellationToken cancellationToken)
        {
            await queue.EnqueueAsync(new MessagingEntry<Subscription>
            {
                ID = e.NewEntity.GetFullID(),
                Value = e.NewEntity,
            }, cancellationToken: cancellationToken);
        }
    }
}
