namespace Niobium.EmailNotification
{
    public interface IVisitorRiskAssessor
    {
        Task<bool> AssessAsync(string token, string action, CancellationToken cancellationToken);
    }
}
