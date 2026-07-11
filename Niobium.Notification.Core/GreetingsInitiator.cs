using System.Text.Json;

namespace Niobium.Notification
{
    internal class GreetingsInitiator(NotificationFlow flow)
        : DomainEventHandler<IDomain<Subscription>, SubscribedEvent>
    {
        protected override DomainEventAudience EventSource => DomainEventAudience.External;

        public override async Task HandleCoreAsync(SubscribedEvent e, CancellationToken cancellationToken) => await flow.RunAsync(new NotifyCommand
        {
            ID = e.Subscription.GetFullID(),
            Tenant = e.Subscription.GetTenant(),
            Channel = e.Subscription.GetChannel(),
            Destination = e.Subscription.Email,
            DestinationDisplayName = String.IsNullOrWhiteSpace(e.Subscription.LastName) ?
                e.Subscription.FirstName :
                $"{e.Subscription.FirstName} {e.Subscription.LastName}",
            Parameters = new Dictionary<string, object>
                {
                    { "FIRST_NAME", String.IsNullOrWhiteSpace(e.Subscription.FirstName) ? String.Empty : e.Subscription.FirstName.ToUpperInvariant() },
                    { "LAST_NAME", String.IsNullOrWhiteSpace(e.Subscription.LastName) ? String.Empty : e.Subscription.LastName.ToUpperInvariant() },
                }
        }, cancellationToken);
    }
}
