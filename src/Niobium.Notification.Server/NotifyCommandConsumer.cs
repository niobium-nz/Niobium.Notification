using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Niobium.Notification.Delivery;
using Niobium.Messaging.ServiceBus;
using Niobium.Platform.ServiceBus;

namespace Niobium.Notification.Server
{
    public class NotifyCommandConsumer(
        NotificationFlow flow,
        ILogger<NotifyCommandConsumer> logger,
        DurableNotificationFlow? durable = null,
        IOptions<NotificationOptions>? options = null)
    {
        [Function(nameof(NotifyCommandConsumer))]
        public async Task Run(
            [ServiceBusTrigger("notifycommand")]
            ServiceBusReceivedMessage message,
            CancellationToken cancellationToken)
        {
            bool parsed = message.TryParse(out NotifyCommand? evt, out var rawBody);
            if (!parsed || evt == null)
            {
                logger.LogError("Notification message could not be parsed.");
                return;
            }

            Transform(evt);
            _ = evt.TryValidate(out var validationState);
            if (!validationState.IsValid)
            {
                logger.LogError("Notification command failed validation.");
                return;
            }

            if (options?.Value.EnableDurableSubmission == true)
            {
                // The queue envelope supplies stable identity even for legacy producers whose
                // body omits DomainEvent.ID. Never generate a new ID on redelivery.
                if (String.IsNullOrWhiteSpace(message.MessageId))
                {
                    throw new ApplicationException(InternalError.BadRequest, "A stable message ID is required.");
                }
                evt.ID = message.MessageId;
                await (durable ?? throw new InvalidOperationException("Durable notification services are unavailable."))
                    .RunAsync(evt, cancellationToken);
            }
            else
            {
                await flow.RunAsync(evt, cancellationToken);
            }
        }

        private static void Transform(NotifyCommand evt)
        {
            // NotifyCommand.Parameters is a Dictionary<string, object>, there could be JsonElement values because of deserialization
            // Transform them to string or IEnumerable<Dictionary<string, string>> for easier usage in templates
            foreach (var key in evt.Parameters.Keys.ToList())
            {
                if (evt.Parameters[key] is JsonElement jsonElement)
                {
                    switch (jsonElement.ValueKind)
                    {
                        case JsonValueKind.String:
                            evt.Parameters[key] = jsonElement.GetString() ?? String.Empty;
                            break;
                        case JsonValueKind.Array:
                            var list = new List<Dictionary<string, string>>();
                            foreach (var item in jsonElement.EnumerateArray())
                            {
                                if (item.ValueKind == JsonValueKind.Object)
                                {
                                    var dict = new Dictionary<string, string>();
                                    foreach (var prop in item.EnumerateObject())
                                    {
                                        dict[prop.Name] = prop.Value.GetString() ?? String.Empty;
                                    }
                                    list.Add(dict);
                                }
                            }
                            evt.Parameters[key] = list;
                            break;
                        default:
                            evt.Parameters[key] = jsonElement.ToString() ?? String.Empty;
                            break;
                    }
                }
            }
        }
    }
}
