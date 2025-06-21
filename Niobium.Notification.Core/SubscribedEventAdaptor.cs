using Cod;
using Cod.Messaging;

namespace Niobium.Notification
{
    internal class SubscribedEventAdaptor(IMessagingBroker<SubscribedEvent> queue) : DomainEventHandler<SubscriptionDomain, EntityChangedEvent<Subscription>>
    {
        public async override Task HandleCoreAsync(EntityChangedEvent<Subscription> e, CancellationToken cancellationToken)
        {
            await queue.EnqueueAsync(new MessagingEntry<SubscribedEvent>
            {
                ID = e.NewEntity.GetFullID(),
                Value = new SubscribedEvent { Subscription = e.NewEntity },
            }, cancellationToken: cancellationToken);
        }
    }
}
