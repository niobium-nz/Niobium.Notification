using Niobium.Messaging;

namespace Niobium.Notification;

public enum NotificationDeliveryKind
{
    Submitted = 1, Delivered = 2, Delayed = 3, Bounced = 4, Failed = 5, Suppressed = 6, Complained = 7,
}

// Authenticated internal receipt evidence. Delivered means acceptance by the
// recipient's mail server; reading or inbox placement is not asserted.
public sealed class NotificationDeliveryEvent : DomainEvent
{
    public required Guid Tenant { get; set; }
    public required string NotificationID { get; set; }
    public required string SubmissionID { get; set; }
    public required string Channel { get; set; }
    public required string Provider { get; set; }
    public required string ProviderEventID { get; set; }
    public required string ProviderMessageID { get; set; }
    public NotificationDeliveryKind Kind { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
