using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Niobium.Messaging.ServiceBus;
using Niobium.Platform.ServiceBus;

namespace Niobium.Notification.Functions
{
    public class NotifyCommandConsumer(
        NotificationFlow flow,
        ILogger<NotifyCommandConsumer> logger)
    {
        [Function(nameof(NotifyCommandConsumer))]
        public async Task Run(
            [ServiceBusTrigger("notifycommand", AutoCompleteMessages = true, Connection = nameof(ServiceBusTriggerOptions))]
            ServiceBusReceivedMessage message,
            CancellationToken cancellationToken)
        {
            if (!message.TryParse(out NotifyCommand? evt, out var rawBody))
            {
                logger.LogError($"Failed to parse message {message.MessageId}: {rawBody}");
                return;
            }

            Transform(evt);
            _ = evt.TryValidate(out var validationState);
            if (!validationState.IsValid)
            {
                logger.LogError($"Validation failed for order evt: {JsonMarshaller.Marshall(validationState.ToDictionary())}");
                return;
            }

            await flow.RunAsync(evt, cancellationToken);
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
