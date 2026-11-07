using Azure;
using Niobium.Notification.Delivery;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace Niobium.Notification.Storage.Tests;

[TestClass]
public sealed class SubmissionConcurrencyTests
{
    [TestMethod]
    public async Task PinnedRepository_InsertRace_ETagClaims_AndRestartRoundTrip()
    {
        await using LocalStorageFixture storage = new();
        await storage.InitializeAsync();
        IRepository<NotificationSubmission> repository = storage.Repository();
        NotificationSubmission input = new()
        {
            PartitionKey = "tenant", RowKey = "operation", Version = NotificationSubmission.CurrentVersion,
            Fingerprint = "fingerprint", StateCode = (int)SubmissionState.Pending, WriteID = "initial",
        };
        bool[] created = await Task.WhenAll(TryWrite(() => repository.CreateAsync(input)), TryWrite(() => repository.CreateAsync(input)));
        Assert.AreEqual(1, created.Count(x => x));
        NotificationSubmission stored = (await repository.RetrieveAsync(input.PartitionKey, input.RowKey))!;
        Assert.IsFalse(String.IsNullOrWhiteSpace(stored.ETag));
        DateTimeOffset first = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
        NotificationSubmission claim = stored with
        {
            StateCode = (int)SubmissionState.Sending, Attempts = 1, FirstSubmission = first,
            NextAttempt = first.AddMinutes(2), OutcomeCode = (int)SubmissionOutcome.SubmissionUnknown,
        };
        bool[] claims = await Task.WhenAll(TryWrite(() => repository.UpdateAsync(claim)), TryWrite(() => repository.UpdateAsync(claim)));
        Assert.AreEqual(1, claims.Count(x => x));
        NotificationSubmission winner = (await storage.Repository().RetrieveAsync(input.PartitionKey, input.RowKey))!;
        Assert.AreNotEqual(stored.ETag, winner.ETag);
        bool staleSucceeded = await TryWrite(() => repository.UpdateAsync(stored));
        Assert.IsFalse(staleSucceeded);
        NotificationSubmission result = await repository.UpdateAsync(winner with
        {
            StateCode = (int)SubmissionState.Accepted, NextAttempt = null,
            ProviderID = "provider-id", OutcomeCode = (int)SubmissionOutcome.Accepted,
        });
        // Pinned mapper discards the write response ETag. Domain tests require verified read-back.
        Assert.IsNull(result.ETag);
        NotificationSubmission reread = (await storage.Repository().RetrieveAsync(input.PartitionKey, input.RowKey))!;
        Assert.IsNull(reread.NextAttempt);
        Assert.AreEqual(first, reread.FirstSubmission);
        Assert.AreEqual((int)SubmissionState.Accepted, reread.StateCode);
        Assert.AreEqual("provider-id", reread.ProviderID);
        Assert.IsFalse(String.IsNullOrWhiteSpace(reread.ETag));
    }

    private static async Task<bool> TryWrite(Func<Task<NotificationSubmission>> write)
    {
        try { await write(); return true; }
        catch (RequestFailedException error) when (error.Status is 409 or 412) { return false; }
    }
}
