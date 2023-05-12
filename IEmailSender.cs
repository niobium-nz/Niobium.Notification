namespace Niobium.EmailNotification
{
    public interface IEmailSender
    {
        Task<bool> SendEmailAsync(string tenant, string message, string name, string contact);
    }
}
