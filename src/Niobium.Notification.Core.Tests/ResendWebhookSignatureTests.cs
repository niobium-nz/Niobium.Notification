using System.Security.Cryptography;
using System.Text;
using Niobium.Notification.Server.Delivery;

namespace Niobium.Notification.Core.Tests;

[TestClass]
public sealed class ResendWebhookSignatureTests
{
    // Published fixed vector from Svix's manual verification documentation.
    // This is public synthetic test data, never a service credential.
    private const string Secret = "whsec_plJ3nmyCDGBKInavdOK15jsl";
    private const string Payload = "{\"event_type\":\"ping\",\"data\":{\"success\":true}}";
    private const string ID = "msg_loFOjxBNrRLzqYUf";
    private const string Timestamp = "1731705121";
    private const string Signature = "v1,rAvfW3dJ/X/qxhsaXPOyyCGmRKsaKWcsNccKXlIktD0=";

    private sealed class Clock(long seconds) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    private static ResendWebhookSignature Verifier(int offset = 0)
        => new([Secret], new Clock(1731705121 + offset));

    [TestMethod]
    public void OfficialVector_VerifiesWithoutParsingOrReserializingBody()
    {
        Assert.IsTrue(Verifier().Verify(Encoding.UTF8.GetBytes(Payload), ID, Timestamp, Signature));
        Assert.IsFalse(Verifier().Verify(Encoding.UTF8.GetBytes(Payload + "\n"), ID, Timestamp, Signature));
        Assert.IsFalse(Verifier().Verify(Encoding.UTF8.GetBytes(Payload.Replace("true", "false")), ID, Timestamp, Signature));
        Assert.IsFalse(Verifier().Verify(Encoding.UTF8.GetBytes(Payload), ID + "changed", Timestamp, Signature));
        Assert.IsFalse(Verifier().Verify(Encoding.UTF8.GetBytes(Payload), ID, "1731705122", Signature));
    }

    [TestMethod]
    [DataRow(-301, false)]
    [DataRow(-300, true)]
    [DataRow(300, true)]
    [DataRow(301, false)]
    public void ReplayTolerance_RejectsOldAndFutureAttempts(int offset, bool expected)
        => Assert.AreEqual(expected, Verifier(offset).Verify(Encoding.UTF8.GetBytes(Payload), ID, Timestamp, Signature));

    [TestMethod]
    [DataRow(null, Timestamp, Signature)]
    [DataRow("msg\nheader", Timestamp, Signature)]
    [DataRow(ID, null, Signature)]
    [DataRow(ID, "-1", Signature)]
    [DataRow(ID, "9223372036854775807", Signature)]
    [DataRow(ID, "999999999999999999999", Signature)]
    [DataRow(ID, Timestamp, null)]
    [DataRow(ID, Timestamp, "v2,rAvfW3dJ/X/qxhsaXPOyyCGmRKsaKWcsNccKXlIktD0=")]
    [DataRow(ID, Timestamp, "v1,malformed!")]
    [DataRow(ID, Timestamp, "v1,YQ==")]
    public void InvalidHeadersOrSignature_FailClosed(string? id, string? timestamp, string? signature)
        => Assert.IsFalse(Verifier().Verify(Encoding.UTF8.GetBytes(Payload), id, timestamp, signature));

    [TestMethod]
    public void BodyAndHeaderLimits_RejectBeforeUnboundedWork()
    {
        Assert.IsFalse(Verifier().Verify([], ID, Timestamp, Signature));
        Assert.IsFalse(Verifier().Verify(new byte[ResendWebhookSignature.MaximumBodyBytes + 1], ID, Timestamp, Signature));
        Assert.IsFalse(Verifier().Verify(Encoding.UTF8.GetBytes(Payload), new string('a', 129), Timestamp, Signature));
        Assert.IsFalse(Verifier().Verify(Encoding.UTF8.GetBytes(Payload), ID, Timestamp, new string('a', 2049)));
    }

    [TestMethod]
    public void Rotation_AllowsConfiguredKeyAndMatchingV1AmongMultipleSignatures()
    {
        string other = "whsec_" + Convert.ToBase64String(new byte[24]);
        ResendWebhookSignature verifier = new([other, Secret], new Clock(1731705121));
        Assert.IsTrue(verifier.Verify(Encoding.UTF8.GetBytes(Payload), ID, Timestamp, "v2,ignored v1,invalid " + Signature));
        Assert.IsFalse(new ResendWebhookSignature([other], new Clock(1731705121))
            .Verify(Encoding.UTF8.GetBytes(Payload), ID, Timestamp, Signature));
    }

    [TestMethod]
    public void BinaryPayload_UsesOriginalUtf8BytesIncludingUnicode()
    {
        byte[] body = Encoding.UTF8.GetBytes("{\"subject\":\"Kia ora ✓\"}\n");
        byte[] key = Convert.FromBase64String(Secret[6..]);
        byte[] prefix = Encoding.UTF8.GetBytes(ID + "." + Timestamp + ".");
        byte[] signed = [.. prefix, .. body];
        string signature = "v1," + Convert.ToBase64String(HMACSHA256.HashData(key, signed));
        Assert.IsTrue(Verifier().Verify(body, ID, Timestamp, signature));
        Assert.IsFalse(Verifier().Verify(Encoding.ASCII.GetBytes(Encoding.UTF8.GetString(body)), ID, Timestamp, signature));
    }

    [TestMethod]
    public void MissingOrMalformedConfiguration_ProducesFixedDiagnostic()
    {
        foreach (string[] keys in new[] { Array.Empty<string>(), new[] { "private-invalid-value" },
            new[] { "whsec_!" }, new[] { "whsec_YQ==" }, Enumerable.Repeat(Secret, 5).ToArray() })
        {
            InvalidOperationException failure = Assert.ThrowsExactly<InvalidOperationException>(() => new ResendWebhookSignature(keys, new Clock(1731705121)));
            Assert.AreEqual("Valid provider webhook signing keys are required.", failure.Message);
        }
    }
}
