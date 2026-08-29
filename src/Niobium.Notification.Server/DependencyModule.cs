using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Niobium.Messaging.ServiceBus;
using Niobium.Platform;
using Niobium.Platform.Blob;
using Niobium.Platform.Captcha.ReCaptcha;
using Niobium.Platform.ServiceBus;
using Niobium.Platform.StorageTable;

namespace Niobium.Notification.Server
{
    internal static class DependencyModule
    {
        private static volatile bool loaded;

        public static TBuilder AddNotification<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
        {
            builder.AddPlatform();
            builder.AddDatabase();
            builder.AddFile();
            builder.AddMessaging();
            Platform.Notification.Email.Resend.DependencyModule.AddNotification(builder);
            builder.AddCaptcha();
            builder.AddCore();
            return builder.AddNotification(builder.Configuration.GetSection(nameof(NotificationOptions)).Bind);
        }

        public static TBuilder AddNotification<TBuilder>(this TBuilder builder, Action<NotificationOptions>? options)
             where TBuilder : IHostApplicationBuilder
        {
            if (loaded)
            {
                return builder;
            }

            loaded = true;

            builder.Services.Configure<NotificationOptions>(o => options?.Invoke(o));

            return builder;
        }
    }
}
