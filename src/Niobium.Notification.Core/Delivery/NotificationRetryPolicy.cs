namespace Niobium.Notification.Delivery
{
    public class NotificationRetryPolicy
    {
        public static readonly TimeSpan ProviderWindow = TimeSpan.FromHours(24);
        public static readonly TimeSpan SendMargin = TimeSpan.FromMinutes(1);
        public const int MaximumAttempts = 48;

        public virtual TimeSpan Delay(int attempt)
        {
            double seconds = Math.Min(3600, 60 * Math.Pow(2, Math.Clamp(attempt - 1, 0, 6)));
            // A minimum minute exceeds the provider's 30-second request timeout.
            return TimeSpan.FromSeconds(Math.Max(60, seconds * (0.8 + (Random.Shared.NextDouble() * 0.2))));
        }
    }
}
