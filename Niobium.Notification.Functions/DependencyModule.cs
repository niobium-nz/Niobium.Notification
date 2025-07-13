using Cod.Messaging.ServiceBus;
using Cod.Platform;
using Cod.Platform.Blob;
using Cod.Platform.Captcha.ReCaptcha;
using Cod.Platform.ServiceBus;
using Cod.Platform.StorageTable;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Niobium.Notification.Functions
{
    internal static class DependencyModule
    {
        private static volatile bool loaded;

        public static void AddNotification(this FunctionsApplicationBuilder builder)
        {
            builder.AddNotification(builder.Configuration.GetSection(nameof(NotificationOptions)).Bind);
        }

        public static void AddNotification(this FunctionsApplicationBuilder builder, Action<NotificationOptions>? options)
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
            Cod.Platform.Notification.Email.Resend.DependencyModule.AddNotification(builder);
            builder.AddCaptcha();
            builder.Services.AddCore();
            builder.UsePlatform();
        }
    }
}
