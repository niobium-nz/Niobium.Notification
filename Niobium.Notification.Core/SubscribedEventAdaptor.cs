using Niobium;
using Niobium.Messaging;

namespace Niobium.Notification
{
    internal class SubscribedEventAdaptor(IMessagingBroker<SubscribedEvent> queue) : DomainEventHandler<SubscriptionDomain, EntityChangedEventArgs<Subscription>>
    {
        public async override Task HandleCoreAsync(EntityChangedEventArgs<Subscription> e, CancellationToken cancellationToken)
        {
            if (e.ChangeType.HasFlag(EntityChangeType.Created))
            {
                await queue.EnqueueAsync(new MessagingEntry<SubscribedEvent>
                {
                    ID = e.Entity.GetFullID(),
                    Value = new SubscribedEvent { Subscription = e.Entity },
                }, cancellationToken: cancellationToken);
            }
            
        }
    }
}
