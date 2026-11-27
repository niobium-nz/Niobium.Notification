using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Niobium.Messaging;
using Niobium.Notification.Delivery;

namespace Niobium.Notification.Server.Delivery;

internal sealed class NotificationDeliveryRoutingOptions
{
    public string RoutesJson { get; set; } = "[]";

    private sealed record Route(string Channel, string Queue);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 4,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal Dictionary<string, string[]> ReadRoutes()
    {
        try
        {
            if (RoutesJson is not { Length: > 0 and <= 8192 }) throw Invalid();
            Route?[] routes = JsonSerializer.Deserialize<Route?[]>(RoutesJson, Json) ?? throw Invalid();
            if (routes.Length > 32 || routes.Any(route => !Valid(route))) throw Invalid();
            if (routes.Select(route => (route!.Channel, route.Queue)).Distinct().Count() != routes.Length)
                throw Invalid();
            return routes.GroupBy(route => route!.Channel, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Select(route => route!.Queue).ToArray(), StringComparer.Ordinal);
        }
        catch (JsonException) { throw Invalid(); }
    }

    private static bool Valid(Route? route)
        => route is { Channel: { Length: > 0 and <= 50 } channel, Queue: { Length: > 26 and <= 48 } queue }
            && channel == channel.Trim() && !channel.Any(Char.IsControl)
            && queue.StartsWith("notificationdeliveryevent-", StringComparison.Ordinal)
            && Char.IsAsciiLetterOrDigit(queue[^1])
            && queue.All(value => value is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static InvalidOperationException Invalid() => new("Invalid Notification delivery route configuration.");
}

// The typed framework broker retains the canonical queue. The host adapter owns
// additional named destinations in this application's configured namespace.
internal sealed class ServiceBusNotificationDeliveryRoutes : INotificationDeliveryRoutes, IAsyncDisposable
{
    private readonly Dictionary<string, string[]> routes;
    private readonly Lazy<ServiceBusClient> client;
    private readonly ConcurrentDictionary<string, Lazy<ServiceBusSender>> senders = new(StringComparer.Ordinal);

    public ServiceBusNotificationDeliveryRoutes(IOptions<NotificationDeliveryRoutingOptions> options, IConfiguration configuration)
        : this(options.Value, () => Open(configuration)) { }

    internal ServiceBusNotificationDeliveryRoutes(NotificationDeliveryRoutingOptions options, Func<ServiceBusClient> open)
    {
        this.routes = options.ReadRoutes();
        this.client = new(open);
    }

    public async Task PublishAsync(MessagingEntry<NotificationDeliveryEvent> receipt, CancellationToken cancellationToken)
    {
        NotificationDeliveryEvent delivery = receipt.Value;
        if (!this.routes.TryGetValue(delivery.Channel, out string[]? queues)) return;
        foreach (string queue in queues)
        {
            Lazy<ServiceBusSender> sender = this.senders.GetOrAdd(queue,
                name => new(() => this.client.Value.CreateSender(name)));
            ServiceBusMessage message = new(receipt.Body)
            {
                MessageId = receipt.ID,
                ContentType = "application/json",
            };
            if (receipt.Type != null)
                message.ApplicationProperties.Add("Content-Type", receipt.Type);
            await sender.Value.SendMessageAsync(message, cancellationToken);
        }
    }

    internal static ServiceBusClient Open(IConfiguration configuration)
    {
        string? host = configuration["AzureWebJobsServiceBus:fullyQualifiedNamespace"];
        string? clientID = configuration["AzureWebJobsServiceBus:clientId"];
        if (host == null || Uri.CheckHostName(host) != UriHostNameType.Dns
            || !Guid.TryParse(clientID, out Guid identity) || identity == Guid.Empty)
            throw new InvalidOperationException("Notification delivery routes require a Service Bus namespace and managed identity.");
        return new ServiceBusClient(host, new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(identity.ToString())));
    }

    public ValueTask DisposeAsync() => this.client.IsValueCreated ? this.client.Value.DisposeAsync() : ValueTask.CompletedTask;
}
