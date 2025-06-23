using Cod;
using Cod.Messaging;

namespace Niobium.Notification
{
    internal class SubscribedEventAdaptor(IMessagingBroker<SubscribedEvent> queue) : DomainEventHandler<SubscriptionDomain, EntityChangedEvent<Subscription>>
    {
        public async override Task HandleCoreAsync(EntityChangedEvent<Subscription> e, CancellationToken cancellationToken)
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
