using System.Text.Json;
using Dapr;
using Microsoft.AspNetCore.Mvc;

namespace Niobium.Notification.Host.Controllers
{
    [ApiController]
    [Route(DaprComponents.MessageRoute)]
    public class NotifyCommandConsumer(NotificationFlow flow, ILogger<NotifyCommandConsumer> logger) : ControllerBase
    {
        [Topic(DaprComponents.ServiceBusPubSub, QueueNames.NotifyCommand, enableRawPayload: true)]
        [HttpPost(QueueNames.NotifyCommand)]
        public async Task<IActionResult> ConsumeAsync(HttpRequest req, CancellationToken cancellationToken)
        {
            NotifyCommand? message = await req.ReadFromJsonAsync<NotifyCommand>(cancellationToken: cancellationToken);
            if (message == null)
            {
                logger.LogError("Failed to parse message.");
                return this.BadRequest();
            }

            Transform(message);
            message.TryValidate(out ValidationState? validationState);
            if (!validationState.IsValid)
            {
                logger.LogError($"Validation failed for order evt: {JsonMarshaller.Marshall(validationState.ToDictionary())}");
                return this.BadRequest(validationState);
            }

            await flow.RunAsync(message, cancellationToken);
            return this.NoContent();
        }

        private static void Transform(NotifyCommand evt)
        {
            // NotifyCommand.Parameters is a Dictionary<string, object>, there could be JsonElement values because of deserialization
            // Transform them to string or IEnumerable<Dictionary<string, string>> for easier usage in templates
            foreach (string? key in evt.Parameters.Keys.ToList())
            {
                if (evt.Parameters[key] is JsonElement jsonElement)
                {
                    switch (jsonElement.ValueKind)
                    {
                        case JsonValueKind.String:
                            evt.Parameters[key] = jsonElement.GetString() ?? String.Empty;
                            break;
                        case JsonValueKind.Array:
                            List<Dictionary<string, string>> list = [];
                            foreach (JsonElement item in jsonElement.EnumerateArray())
                            {
                                if (item.ValueKind == JsonValueKind.Object)
                                {
                                    Dictionary<string, string> dict = [];
                                    foreach (JsonProperty prop in item.EnumerateObject())
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
