namespace Niobium.Notification.Delivery;

public sealed class NotificationReceiptDomain(Lazy<IRepository<NotificationReceipt>> repository,
    IEnumerable<IDomainEventHandler<IDomain<NotificationReceipt>>> handlers) : GenericDomain<NotificationReceipt>(repository, handlers)
{
    public async Task RegisterAsync(CaptureNotificationReceiptCommand command, CancellationToken cancellationToken = default)
    {
        CheckInitialized();
        Validate(command);
        NotificationReceipt candidate = NotificationReceipt.Create(command);
        if (candidate.Provider != PartitionKey || candidate.ID != RowKey)
            throw Conflict("The receipt belongs to another provider event.");
        NotificationReceipt? existing = await TryGetEntityAsync(cancellationToken);
        if (existing != null)
        {
            if (!existing.MatchesCapture(candidate)) throw Conflict("Provider event identity conflicts with its stored receipt.");
            return;
        }
        await SaveVerifiedAsync(candidate with { PublicationCode = (int)ReceiptPublication.Pending }, true, cancellationToken);
    }

    public async Task<NotificationDeliveryEvent?> PreparePublicationAsync(NotificationSubmission? submission,
        CancellationToken cancellationToken = default)
    {
        NotificationReceipt receipt = await GetEntityAsync(cancellationToken);
        if (receipt.PublicationCode is (int)ReceiptPublication.Published or (int)ReceiptPublication.Uncorrelated) return null;
        if (receipt.PublicationCode != (int)ReceiptPublication.Pending) throw Conflict("Unsupported receipt publication state.");
        NotificationCorrelation? correlation = receipt.GetCorrelation();
        ReceiptHoldReason reason = correlation == null ? ReceiptHoldReason.MissingCorrelation
            : submission == null ? ReceiptHoldReason.SubmissionMissing
            : !submission.MatchesReceiptCorrelation(correlation) ? ReceiptHoldReason.CorrelationMismatch
            : String.IsNullOrWhiteSpace(submission.Operation) || String.IsNullOrWhiteSpace(submission.Channel) || submission.FirstSubmission == null
                ? ReceiptHoldReason.LegacyOperationMissing
            : submission.ProviderID != null && submission.ProviderID != receipt.ProviderMessageID ? ReceiptHoldReason.ProviderMessageMismatch
            : ReceiptHoldReason.None;
        if (reason != ReceiptHoldReason.None)
        {
            await SaveVerifiedAsync(receipt with { PublicationCode = (int)ReceiptPublication.Uncorrelated, HoldReasonCode = (int)reason }, false, cancellationToken);
            return null;
        }
        // Positive authenticated evidence is valid even if the provider response's
        // outcome write is still pending. The independently frozen proof binds it.
        return receipt.ToDeliveryEvent(submission!);
    }

    public async Task<bool> NeedsPublicationAsync(CancellationToken cancellationToken = default)
    {
        NotificationReceipt receipt = await GetEntityAsync(cancellationToken);
        return (ReceiptPublication)receipt.PublicationCode switch
        {
            ReceiptPublication.Pending => true,
            ReceiptPublication.Published or ReceiptPublication.Uncorrelated => false,
            _ => throw Conflict("Unsupported receipt publication state."),
        };
    }

    public async Task MarkPublishedAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        NotificationReceipt receipt = await GetEntityAsync(cancellationToken);
        if (receipt.PublicationCode == (int)ReceiptPublication.Published) return;
        if (receipt.PublicationCode != (int)ReceiptPublication.Pending) throw Conflict("Receipt cannot be published.");
        await SaveVerifiedAsync(receipt with { PublicationCode = (int)ReceiptPublication.Published, PublishedAt = now }, false, cancellationToken);
    }

    private async Task SaveVerifiedAsync(NotificationReceipt next, bool isNew, CancellationToken cancellationToken)
    {
        if (!isNew && (String.IsNullOrWhiteSpace(next.ETag) || next.ETag == "*")) throw Conflict("A conditional receipt version is required.");
        NotificationReceipt expected = next with { WriteID = Guid.NewGuid().ToString("N") };
        await SaveAsync([expected], cancellationToken: cancellationToken);
        await ReloadAsync(cancellationToken);
        NotificationReceipt saved = await GetEntityAsync(cancellationToken);
        if (String.IsNullOrWhiteSpace(saved.ETag) || saved.ETag == "*"
            || saved != expected with { ETag = saved.ETag, Timestamp = saved.Timestamp })
            throw Conflict("Receipt changed while verifying persistence.");
    }

    private static ApplicationException Conflict(string message) => new(InternalError.Conflict, message);

    internal static void Validate(CaptureNotificationReceiptCommand command)
    {
        if (command.Provider != "resend" || !command.HasValidRepresentation() || !Enum.IsDefined(command.Kind)
            || command.OccurredAt == default || command.ReceivedAt == default)
            throw new ApplicationException(InternalError.BadRequest, "Invalid provider receipt.");
    }
}
