using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Niobium.Messaging.ServiceBus;
using Niobium.Platform;
using Niobium.Platform.Blob;
using Niobium.Platform.Captcha.ReCaptcha;
using Niobium.Platform.ServiceBus;
using Niobium.Platform.StorageTable;

namespace Niobium.Notification.Functions
{
    internal static class DependencyModule
    {
        private static volatile bool loaded;

        public static void AddNotification(this FunctionsApplicationBuilder builder) => builder.AddNotification(builder.Configuration.GetSection(nameof(NotificationOptions)).Bind);

        public static void AddNotification(this FunctionsApplicationBuilder builder, Action<NotificationOptions>? options)
        {
            if (loaded)
            {
                return;
            }

            loaded = true;

            _ = builder.Services.Configure<NotificationOptions>(o => options?.Invoke(o));

            builder.AddDatabase();
            builder.AddFile();
            builder.AddMessaging();
            Niobium.Platform.Notification.Email.Resend.DependencyModule.AddNotification(builder);
            builder.AddCaptcha();
            builder.AddCore();
            _ = builder.UsePlatform();
        }
    }
}
