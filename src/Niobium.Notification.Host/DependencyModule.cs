using Niobium.Messaging.ServiceBus;
using Niobium.Platform.Blob;
using Niobium.Platform.Captcha.ReCaptcha;
using Niobium.Platform.ServiceBus;
using Niobium.Platform.StorageTable;

namespace Niobium.Notification.Host
{
    internal static class DependencyModule
    {
        private static volatile bool loaded;

        public static void AddNotification(this IHostApplicationBuilder builder) => builder.AddNotification(builder.Configuration.GetSection(nameof(NotificationOptions)).Bind);

        public static void AddNotification(this IHostApplicationBuilder builder, Action<NotificationOptions>? options)
        {
            if (loaded)
            {
                return;
            }

            loaded = true;

            builder.Services.Configure<NotificationOptions>(o => options?.Invoke(o));

            builder.AddDatabase();
            builder.AddFile();
            builder.AddMessaging();
            Platform.Notification.Email.Resend.DependencyModule.AddNotification(builder);
            builder.AddCaptcha();
            builder.AddCore();
        }
    }
}
