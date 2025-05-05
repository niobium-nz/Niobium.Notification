using Cod;
using Cod.Messaging;

namespace Niobium.EmailNotification
{
    internal class WelcomeSubscriptionTrigger(IMessagingBroker<Subscription> queue) : DomainEventHandler<SubscriptionDomain, EntityChangedEventArgs<Subscription>>
    {
        public async override Task HandleAsync(EntityChangedEventArgs<Subscription> e, CancellationToken cancellationToken)
        {
            await queue.EnqueueAsync(new MessagingEntry<Subscription>
            {
                ID = e.NewEntity.GetFullID(),
                Value = e.NewEntity,
            });
        }
    }
}
