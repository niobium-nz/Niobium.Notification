using Niobium.Messaging;

namespace Niobium.Notification.Delivery;

public interface INotificationDeliveryRoutes
{
    Task PublishAsync(MessagingEntry<NotificationDeliveryEvent> receipt, CancellationToken cancellationToken);
}
