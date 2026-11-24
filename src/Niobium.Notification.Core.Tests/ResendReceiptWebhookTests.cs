using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;
using Niobium.Messaging;
using Niobium.Notification.Delivery;
using Niobium.Notification.Server;
using Niobium.Notification.Server.Delivery;

namespace Niobium.Notification.Core.Tests;

[TestClass]
public sealed class ResendReceiptWebhookTests
{
    private const string Secret = "whsec_plJ3nmyCDGBKInavdOK15jsl";
    private const string EventID = "msg_synthetic_delivery";
    private readonly DurableNotificationTests.Clock clock = new();
    private readonly Mock<IMessagingBroker<CaptureNotificationReceiptCommand>> queue = new(MockBehavior.Strict);

    private ResendReceiptWebhook Handler(params string[] secrets)
        => new(Options.Create(new ResendWebhookOptions { SigningSecretsJson = JsonSerializer.Serialize(secrets) }), queue.Object, clock);

    private byte[] Body(string kind = "email.delivered", object? tags = null) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        type = kind, created_at = clock.Now.AddSeconds(-1),
        data = new { email_id = "synthetic-provider-id", to = new[] { "private@example.invalid" },
            subject = "Private subject", tags = tags ?? new { niobium_tenant = "opaque_tenant", niobium_submission = "future_id_format", niobium_proof = "opaque_proof" } },
    });

    private HttpRequest Request(byte[] body)
    {
        HttpRequest request = new DefaultHttpContext().Request;
        request.Body = new MemoryStream(body);
        request.Headers["svix-id"] = EventID;
        string timestamp = clock.Now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        request.Headers["svix-timestamp"] = timestamp;
        byte[] signed = [.. Encoding.UTF8.GetBytes(EventID + "." + timestamp + "."), .. body];
        request.Headers["svix-signature"] = "v1," + Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(Secret[6..]), signed));
        return request;
    }

    [TestMethod]
    [DataRow("email.sent", NotificationDeliveryKind.Submitted)]
    [DataRow("email.delivered", NotificationDeliveryKind.Delivered)]
    [DataRow("email.delivery_delayed", NotificationDeliveryKind.Delayed)]
    [DataRow("email.failed", NotificationDeliveryKind.Failed)]
    [DataRow("email.bounced", NotificationDeliveryKind.Bounced)]
    [DataRow("email.suppressed", NotificationDeliveryKind.Suppressed)]
    [DataRow("email.complained", NotificationDeliveryKind.Complained)]
    public async Task SignedReceipt_DurablyQueuesNormalizedEvidenceBeforeAcknowledgement(string kind, NotificationDeliveryKind expected)
    {
        CaptureNotificationReceiptCommand? captured = null;
        queue.Setup(q => q.EnqueueAsync(It.IsAny<IEnumerable<MessagingEntry<CaptureNotificationReceiptCommand>>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<MessagingEntry<CaptureNotificationReceiptCommand>>, CancellationToken>((entries, _) =>
            {
                MessagingEntry<CaptureNotificationReceiptCommand> entry = entries.Single();
                Assert.AreEqual(EventID, entry.ID);
                captured = entry.Value;
            }).Returns(Task.CompletedTask);
        IActionResult response = await Handler(Secret).Run(Request(Body(kind)), default);
        Assert.IsInstanceOfType<AcceptedResult>(response);
        Assert.IsNotNull(captured);
        Assert.AreEqual(expected, captured.Kind);
        Assert.AreEqual("resend", captured.Provider);
        Assert.AreEqual("synthetic-provider-id", captured.ProviderMessageID);
        Assert.AreEqual(clock.Now.AddSeconds(-1), captured.OccurredAt);
        Assert.AreEqual("future_id_format", captured.Correlation!.SubmissionID);
        Assert.IsTrue(captured.HasValidRepresentation());
        string wire = JsonMarshaller.Marshall(captured);
        Assert.IsFalse(wire.Contains("private@example.invalid") || wire.Contains("Private subject"));
        CaptureNotificationReceiptCommand roundtrip = JsonMarshaller.Unmarshall<CaptureNotificationReceiptCommand>(wire)!;
        Assert.AreEqual(captured.BodyFingerprint, roundtrip.BodyFingerprint);
        Assert.AreEqual(captured.Correlation, roundtrip.Correlation);
        Assert.IsTrue(roundtrip.HasValidRepresentation());
        queue.Verify(q => q.EnqueueAsync(It.IsAny<IEnumerable<MessagingEntry<CaptureNotificationReceiptCommand>>>(), It.IsAny<CancellationToken>()), Times.Once);
        queue.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task InvalidSignatureAndDuplicateHeaders_NeverQueue()
    {
        HttpRequest request = Request(Body());
        request.Headers["svix-signature"] = "v1,invalid";
        Assert.IsInstanceOfType<UnauthorizedResult>(await Handler(Secret).Run(request, default));
        request = Request(Body());
        request.Headers["svix-id"] = new[] { EventID, EventID };
        Assert.IsInstanceOfType<UnauthorizedResult>(await Handler(Secret).Run(request, default));
        queue.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task MissingConfigurationAndOversizedBodies_FailClosedWithoutQueue()
    {
        Assert.AreEqual(503, ((StatusCodeResult)await Handler().Run(Request(Body()), default)).StatusCode);
        HttpRequest request = Request(Body());
        request.ContentLength = ResendWebhookSignature.MaximumBodyBytes + 1;
        Assert.AreEqual(413, ((StatusCodeResult)await Handler(Secret).Run(request, default)).StatusCode);
        request = Request(new byte[ResendWebhookSignature.MaximumBodyBytes + 1]);
        Assert.IsNull(request.ContentLength);
        Assert.AreEqual(413, ((StatusCodeResult)await Handler(Secret).Run(request, default)).StatusCode);
        queue.VerifyNoOtherCalls();
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("not-json")]
    [DataRow("null")]
    [DataRow("{}")]
    [DataRow("[null]")]
    [DataRow("[1]")]
    public async Task MalformedSigningKeyConfiguration_FailsClosedWithoutPayloadAccess(string configuration)
    {
        var handler = new ResendReceiptWebhook(Options.Create(new ResendWebhookOptions { SigningSecretsJson = configuration }), queue.Object, clock);
        Assert.AreEqual(503, ((StatusCodeResult)await handler.Run(Request(Body()), default)).StatusCode);
        queue.VerifyNoOtherCalls();
    }

    [TestMethod]
    [DataRow("not-json")]
    [DataRow("[]")]
    [DataRow("{\"type\":\"email.delivered\"}")]
    [DataRow("{\"type\":\"email.delivered\",\"created_at\":\"bad\",\"data\":{\"email_id\":\"id\"}}")]
    public async Task AuthenticatedMalformedEvent_IsRejectedWithoutQueue(string payload)
    {
        Assert.IsInstanceOfType<BadRequestResult>(await Handler(Secret).Run(Request(Encoding.UTF8.GetBytes(payload)), default));
        queue.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task UnsupportedEvent_IsAcknowledgedWithoutInventingDeliveryEvidence()
    {
        Assert.IsInstanceOfType<NoContentResult>(await Handler(Secret).Run(Request(Body("email.opened")), default));
        queue.VerifyNoOtherCalls();
    }

    [TestMethod]
    public void LegacyOrInvalidCorrelation_IsCapturedAsUncorrelated()
    {
        foreach (object tags in new object[] { new { category = "legacy" }, new { niobium_tenant = "another/partition", niobium_submission = "id", niobium_proof = "proof" }, new { niobium_tenant = 1 } })
        {
            CaptureNotificationReceiptCommand receipt = ResendWebhookReader.Read(Body(tags: tags), EventID, clock.Now)!;
            Assert.IsNull(receipt.Correlation);
            Assert.AreEqual(NotificationDeliveryKind.Delivered, receipt.Kind);
        }
    }

    [TestMethod]
    public async Task UnknownQueueAcceptance_DoesNotAcknowledgeOrRetryTheWebhook()
    {
        queue.Setup(q => q.EnqueueAsync(It.IsAny<IEnumerable<MessagingEntry<CaptureNotificationReceiptCommand>>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Synthetic unknown queue response"));
        await Assert.ThrowsExactlyAsync<IOException>(() => Handler(Secret).Run(Request(Body()), default));
        queue.Verify(q => q.EnqueueAsync(It.IsAny<IEnumerable<MessagingEntry<CaptureNotificationReceiptCommand>>>(), It.IsAny<CancellationToken>()), Times.Once);
        queue.VerifyNoOtherCalls();
    }
}
