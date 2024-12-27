namespace Niobium.EmailNotification
{
    public interface IVisitorRiskAssessor
    {
        Task<bool> AssessAsync(Guid requestID, string tenant, string token, string? remoteIP, CancellationToken cancellationToken);
    }
}
