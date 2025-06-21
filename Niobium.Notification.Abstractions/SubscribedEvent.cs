using Cod.Messaging;

namespace Niobium.Notification
{
    public class SubscribedEvent : DomainEvent
    {
        public required Subscription Subscription { get; init; }
    }
}
