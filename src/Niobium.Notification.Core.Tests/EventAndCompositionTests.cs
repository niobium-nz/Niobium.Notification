using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using Niobium.Messaging;
using Niobium.Notification.Delivery;
using Niobium.Platform.Notification.Email;
using static Niobium.Notification.Core.Tests.Helpers.NotificationTestHelper;

namespace Niobium.Notification.Core.Tests;

[TestClass]
public sealed class EventAndCompositionTests
{
    private static T Handler<T>(string name, object dependency)
        => (T)Activator.CreateInstance(typeof(NotificationFlow).Assembly.GetType("Niobium.Notification." + name, true)!, dependency)!;

    [TestMethod]
    [DataRow("First", "Last", "FIRST LAST")]
    [DataRow("First", null, "FIRST ")]
    [DataRow(null, null, " ")]
    public async Task Greeting_RoutesExternalSubscriptionToItsTenantAndRecipient(string? first, string? last, string rendered)
    {
        Guid tenant = Guid.NewGuid();
        var (flow, repository, sender, _) = CreateSut(CreateDomain(BuildTemplate(tenant, "Welcome"), "{{FIRST_NAME}} {{LAST_NAME}}", DefaultOptions));
        var handler = Handler<DomainEventHandler<IDomain<Subscription>, SubscribedEvent>>("GreetingsInitiator", flow);
        Subscription subscription = new() { Belonging = Subscription.BuildBelonging(tenant, "Welcome"), Email = "person@example.invalid", FirstName = first, LastName = last, Subscribed = DateTimeOffset.UtcNow };
        using CancellationTokenSource cancellation = new();
        await handler.HandleCoreAsync(new SubscribedEvent { Subscription = subscription }, cancellation.Token);
        repository.Verify(x => x.GetAsync(tenant.ToString(), "Welcome", false, cancellation.Token), Times.Once);
        sender.Verify(x => x.SendAsync(It.IsAny<EmailAddress>(),
            It.Is<IEnumerable<EmailAddress>>(r => r.Single().Address == subscription.Email),
            It.IsAny<string>(), rendered, cancellation.Token), Times.Once);
    }

    [TestMethod]
    public async Task SubscriptionEvents_OnlyCreatedEntitiesAreEnqueuedAndFailurePropagates()
    {
        Mock<IMessagingBroker<SubscribedEvent>> queue = new(MockBehavior.Strict);
        var handler = Handler<DomainEventHandler<IDomain<Subscription>, EntityChangedEventArgs<Subscription>>>("SubscribedEventAdaptor", queue.Object);
        Subscription subscription = new() { Belonging = Subscription.BuildBelonging(Guid.NewGuid(), "Welcome"), Email = "person@example.invalid", Subscribed = DateTimeOffset.UtcNow };
        using CancellationTokenSource cancellation = new();
        await handler.HandleCoreAsync(new EntityChangedEventArgs<Subscription>(EntityChangeType.Updated, subscription), cancellation.Token);
        Assert.AreEqual(0, queue.Invocations.Count);
        MessagingEntry<SubscribedEvent>? captured = null;
        queue.Setup(x => x.EnqueueAsync(It.IsAny<IEnumerable<MessagingEntry<SubscribedEvent>>>(), cancellation.Token))
            .Callback<IEnumerable<MessagingEntry<SubscribedEvent>>, CancellationToken>((entries, _) => captured = entries.Single())
            .ThrowsAsync(new InvalidOperationException("synthetic queue failure"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => handler.HandleCoreAsync(new EntityChangedEventArgs<Subscription>(EntityChangeType.Created, subscription), cancellation.Token));
        Assert.AreEqual(1, queue.Invocations.Count);
        Assert.IsNotNull(captured);
        Assert.AreEqual(subscription.GetFullID(), captured.ID);
        Assert.AreEqual(subscription.Belonging, captured.Value.Subscription.Belonging);
        Assert.AreEqual(subscription.Email, captured.Value.Subscription.Email);
    }

    [TestMethod]
    public void Composition_RegistersRuntimeServicesAndPreservesConfiguredOptions()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration["NotificationOptions:SelfHostName"] = "test.example.invalid";
        builder.Configuration["NotificationOptions:TemplateFolder"] = "templates";
        Type module = typeof(Server.DeploymentInfo).Assembly.GetType("Niobium.Notification.Server.DependencyModule", true)!;
        MethodInfo register = module.GetMethods(BindingFlags.Public | BindingFlags.Static).Single(m => m.Name == "AddNotification" && m.GetParameters().Length == 1).MakeGenericMethod(typeof(HostApplicationBuilder));
        Assert.AreSame(builder, register.Invoke(null, [builder]));
        int registrations = builder.Services.Count;
        Assert.AreSame(builder, register.Invoke(null, [builder]));
        Assert.AreEqual(registrations, builder.Services.Count);
        using ServiceProvider services = builder.Services.BuildServiceProvider();
        NotificationOptions options = services.GetRequiredService<IOptions<NotificationOptions>>().Value;
        Assert.AreEqual("test.example.invalid", options.SelfHostName);
        Assert.AreEqual("templates", options.TemplateFolder);
        Assert.IsNotNull(services.GetRequiredService<DurableNotificationFlow>());
        Assert.IsFalse(services.GetRequiredService<IOptions<NotificationOptions>>().Value.EnableDurableSubmission);
        HttpMessageHandler handler = services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(nameof(IIdempotentNotificationProvider));
        while (handler is DelegatingHandler wrapper)
        {
            Assert.IsFalse(wrapper.GetType().Name.Contains("Resilience", StringComparison.Ordinal), "The durable provider must not inherit hidden HTTP retries.");
            handler = wrapper.InnerHandler!;
        }
        Assert.IsInstanceOfType<SocketsHttpHandler>(handler);
        Assert.IsFalse(((SocketsHttpHandler)handler).AllowAutoRedirect);
        Assert.IsTrue(builder.Services.Any(s => s.ServiceType == typeof(IEmailNotificationClient)));
        Assert.IsTrue(builder.Services.Any(s => s.ServiceType == typeof(IExternalEventAdaptor<,>)));
    }
}
