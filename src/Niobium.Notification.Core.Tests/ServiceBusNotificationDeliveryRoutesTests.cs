using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Moq;
using Niobium.Messaging;
using Niobium.Notification.Server.Delivery;

namespace Niobium.Notification.Core.Tests;

[TestClass]
public sealed class ServiceBusNotificationDeliveryRoutesTests
{
    private const string RouteA = "notificationdeliveryevent-first";
    private const string RouteB = "notificationdeliveryevent-second";
    private static MessagingEntry<NotificationDeliveryEvent> Entry(string channel)
    {
        NotificationDeliveryEvent delivery = new()
        {
            ID = "resend/synthetic-event",
            Tenant = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            NotificationID = "synthetic-notification",
            SubmissionID = "synthetic-submission",
            Channel = channel,
            Provider = "resend",
            ProviderEventID = "synthetic-event",
            ProviderMessageID = "synthetic-provider-message",
            Kind = NotificationDeliveryKind.Delivered,
            OccurredAt = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
        };
        return new() { ID = delivery.ID, Value = delivery };
    }

    [TestMethod]
    public async Task OnlyTheConfiguredChannelFansOutToItsExplicitQueuesWithTheSameEnvelope()
    {
        const string json = """[{"channel":"Notice","queue":"notificationdeliveryevent-first"},{"channel":"Notice","queue":"notificationdeliveryevent-second"}]""";
        NotificationDeliveryRoutingOptions options = new() { RoutesJson = json };
        Mock<ServiceBusClient> bus = new(MockBehavior.Strict);
        Mock<ServiceBusSender> first = new(MockBehavior.Strict);
        Mock<ServiceBusSender> second = new(MockBehavior.Strict);
        List<ServiceBusMessage> sent = [];
        foreach (Mock<ServiceBusSender> sender in new[] { first, second })
        {
            sender.Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
                .Callback<ServiceBusMessage, CancellationToken>((message, _) => sent.Add(message))
                .Returns(Task.CompletedTask);
        }
        bus.Setup(b => b.CreateSender(RouteA)).Returns(first.Object);
        bus.Setup(b => b.CreateSender(RouteB)).Returns(second.Object);
        bus.Setup(b => b.DisposeAsync()).Returns(ValueTask.CompletedTask);
        int opened = 0;
        await using ServiceBusNotificationDeliveryRoutes routes = new(options, () => { opened++; return bus.Object; });
        await routes.PublishAsync(Entry("Other"), CancellationToken.None);
        await routes.PublishAsync(Entry("notice"), CancellationToken.None);
        Assert.AreEqual(0, opened);
        MessagingEntry<NotificationDeliveryEvent> entry = Entry("Notice");
        await routes.PublishAsync(entry, CancellationToken.None);
        await routes.PublishAsync(entry, CancellationToken.None);
        Assert.HasCount(4, sent);
        Assert.AreEqual(1, opened);
        bus.Verify(b => b.CreateSender(RouteA), Times.Once);
        bus.Verify(b => b.CreateSender(RouteB), Times.Once);
        foreach (ServiceBusMessage message in sent)
        {
            Assert.AreEqual(entry.ID, message.MessageId);
            Assert.AreEqual(entry.Body, message.Body.ToString());
            Assert.AreEqual("application/json", message.ContentType);
            Assert.AreEqual(entry.Type, message.ApplicationProperties["Content-Type"]);
        }
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("[null]")]
    [DataRow("[{")]
    [DataRow("""[{"channel":"Notice","queue":"notifycommand"}]""")]
    [DataRow("""[{"channel":"Notice","queue":"notificationdeliveryevent"}]""")]
    [DataRow("""[{"channel":"Notice","queue":"notificationdeliveryevent-"}]""")]
    [DataRow("""[{"channel":"Notice","queue":"notificationdeliveryevent-other/path"}]""")]
    [DataRow("""[{"channel":" Notice","queue":"notificationdeliveryevent-first"}]""")]
    [DataRow("""[{"channel":"Notice","queue":"notificationdeliveryevent-first","namespace":"other"}]""")]
    [DataRow("""[{"channel":"Notice","queue":"notificationdeliveryevent-first"},{"channel":"Notice","queue":"notificationdeliveryevent-first"}]""")]
    public void InvalidRoutesFailBeforeResolvingTheBusAndDoNotExposeConfiguration(string value)
    {
        NotificationDeliveryRoutingOptions options = new() { RoutesJson = value };
        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            new ServiceBusNotificationDeliveryRoutes(options, () => throw new AssertFailedException("Unexpected bus access.")));
        Assert.AreEqual("Invalid Notification delivery route configuration.", error.Message);
    }

    [TestMethod]
    public async Task EmptyRoutesDoNotRequireAServiceBusClient()
    {
        await using ServiceBusNotificationDeliveryRoutes routes = new(new NotificationDeliveryRoutingOptions(),
            () => throw new AssertFailedException("Unexpected bus access."));
        await routes.PublishAsync(Entry("Notice"), CancellationToken.None);
    }

    [TestMethod]
    public void RoutesRequireTheConfiguredManagedIdentity()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        Assert.ThrowsExactly<InvalidOperationException>(() => ServiceBusNotificationDeliveryRoutes.Open(configuration));
    }
}
