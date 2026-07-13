using Microsoft.AspNetCore.HttpOverrides;
using Niobium.Messaging.ServiceBus;
using Niobium.Platform;
using Niobium.Platform.Blob;
using Niobium.Platform.Captcha.ReCaptcha;
using Niobium.Platform.ServiceBus;
using Niobium.Platform.StorageTable;

namespace Niobium.Notification.Host
{
    internal static class DependencyModule
    {
        private static volatile bool loaded;

        public static WebApplicationBuilder AddNotification(this WebApplicationBuilder builder) => builder.AddNotification(builder.Configuration.GetSection(nameof(NotificationOptions)).Bind);

        public static WebApplicationBuilder AddNotification(this WebApplicationBuilder builder, Action<NotificationOptions>? options)
        {
            if (loaded)
            {
                return builder;
            }

            loaded = true;

            builder.Services.Configure<NotificationOptions>(o => options?.Invoke(o));

            builder.Services.AddDaprClient();
            builder.Services.AddControllers().AddDapr();
            builder.Services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
            });

            builder.ConfigureOpenTelemetry();
            builder.AddDatabase();
            builder.AddFile();
            builder.AddMessaging();
            Platform.Notification.Email.Resend.DependencyModule.AddNotification(builder);
            builder.AddCaptcha();
            builder.AddCore();

            return builder;
        }

        public static WebApplication UseNotification(this WebApplication app)
        {
            app.UseForwardedHeaders();
            app.UseRouting();
            app.UseCloudEvents();
            app.UsePlatform();
            app.MapControllers();
            app.MapSubscribeHandler();
            return app;
        }
    }
}
