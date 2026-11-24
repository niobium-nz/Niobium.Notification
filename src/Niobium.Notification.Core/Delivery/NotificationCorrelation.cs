namespace Niobium.Notification.Delivery;

// An opaque model-owned binding between a frozen send and a signed provider
// receipt. It is not an externally supplied callback URL or an invoice reference.
public sealed record NotificationCorrelation(string Tenant, string SubmissionID, string Proof)
{
    // Provider tag values are bounded opaque strings. Consumers must not assume
    // that a submission ID is a particular hash length or reconstruct that ID.
    public bool IsWellFormed() => Tag(Tenant) && Tag(SubmissionID) && Tag(Proof);

    public static NotificationCorrelation? FromTags(string? tenant, string? submissionID, string? proof)
        => Tag(tenant) && Tag(submissionID) && Tag(proof) ? new(tenant!, submissionID!, proof!) : null;

    private static bool Tag(string? value) => !String.IsNullOrEmpty(value) && value.Length <= 256
        && value.All(character => Char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
}
