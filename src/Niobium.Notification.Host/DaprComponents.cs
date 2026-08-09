namespace Niobium.Notification.Host
{
    internal static class DaprComponents
    {
        private static volatile bool added;
        private static volatile bool used;

        public const string MessageRoute = "dapr/messages";
        public const string ServiceBusPubSub = "servicebus-pubsub";

        public static WebApplicationBuilder AddDapr(this WebApplicationBuilder builder)
        {
            if (added)
            {
                return builder;
            }

            added = true;

            builder.Services.AddDaprClient();
            builder.Services.AddControllers().AddDapr();

            return builder;
        }

        public static WebApplication UseDapr(this WebApplication app)
        {
            if (used)
            {
                return app;
            }

            used = true;

            app.UseCloudEvents();
            app.MapSubscribeHandler();
            return app;
        }
    }
}
