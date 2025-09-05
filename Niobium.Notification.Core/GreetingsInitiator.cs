namespace Niobium.Notification
{
    internal class GreetingsInitiator(NotificationFlow flow)
        : DomainEventHandler<IDomain<Subscription>, SubscribedEvent>
    {
        protected override DomainEventAudience EventSource => DomainEventAudience.External;

        public override async Task HandleCoreAsync(SubscribedEvent e, CancellationToken cancellationToken) => await flow.RunAsync(new NotificationRequest
        {
            ID = Guid.NewGuid(),
            Tenant = e.Subscription.GetTenant(),
            Channel = e.Subscription.GetChannel(),
            Destination = e.Subscription.Email,
            Parameters = new Dictionary<string, string>
                {
                    { "FIRST_NAME", e.Subscription.FirstName.ToUpperInvariant() },
                    { "LAST_NAME", String.IsNullOrWhiteSpace(e.Subscription.LastName) ? String.Empty : e.Subscription.LastName.ToUpperInvariant() },
                }
        }, cancellationToken);
    }
}
