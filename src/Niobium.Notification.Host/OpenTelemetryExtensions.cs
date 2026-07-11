using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Niobium.Notification.Host
{
    internal static class OpenTelemetryExtensions
    {
        public static IHostApplicationBuilder ConfigureOpenTelemetry(this IHostApplicationBuilder builder)
        {
            string? applicationInsightsConnectionString = builder.Configuration.GetValue<string>("APPLICATION_INSIGHTS_CONNECTION_STRING");
            string? otlpEndpoint = builder.Configuration.GetValue<string>("OTEL_EXPORTER_OTLP_ENDPOINT");
            string? environment = builder.Environment.EnvironmentName;
            Dictionary<string, object> resourceAttributes = new()
            {
                { "service.instance.id", Environment.MachineName },
                { "service.name", builder.Configuration.GetValue<string>("SERVICE_NAME") ?? "unknown-service" },
                { "service.version", builder.Configuration.GetValue<string>("SERVICE_VERSION") ?? "1.0.0-prerelease" },
                { "deployment.environment", environment ?? "local" }
            };

            ResourceBuilder resourceBuilder = ResourceBuilder.CreateDefault().AddAttributes(resourceAttributes);

            TracerProviderBuilder tracerBuilder = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(resourceBuilder)
                .AddHttpClientInstrumentation()
                .AddSource("Niobium.*")
                .AddConsoleExporter();

            if (!String.IsNullOrWhiteSpace(applicationInsightsConnectionString))
            {
                tracerBuilder.AddAzureMonitorTraceExporter(options => options.ConnectionString = applicationInsightsConnectionString);
            }

            if (!String.IsNullOrWhiteSpace(otlpEndpoint))
            {
                tracerBuilder.AddOtlpExporter(o =>
                {
                    o.Endpoint = new Uri(otlpEndpoint);
                    o.Protocol = OtlpExportProtocol.Grpc;
                });
            }

            TracerProvider tracerProvider = tracerBuilder.Build();
            builder.Services.AddSingleton(tracerProvider);

            MeterProviderBuilder meterBuilder = Sdk.CreateMeterProviderBuilder()
                .SetResourceBuilder(resourceBuilder)
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("Niobium.*");

            if (!String.IsNullOrWhiteSpace(applicationInsightsConnectionString))
            {
                meterBuilder.AddAzureMonitorMetricExporter(options => options.ConnectionString = applicationInsightsConnectionString);
            }

            if (!String.IsNullOrWhiteSpace(otlpEndpoint))
            {
                meterBuilder.AddOtlpExporter(o =>
                {
                    o.Endpoint = new Uri(otlpEndpoint);
                    o.Protocol = OtlpExportProtocol.Grpc;
                });
            }

            MeterProvider meterProvider = meterBuilder.Build();
            builder.Services.AddSingleton(meterProvider);

            builder.Logging.ClearProviders();
            builder.Logging.AddConsole();
            builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.SetResourceBuilder(resourceBuilder);
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = true;

                if (!String.IsNullOrWhiteSpace(applicationInsightsConnectionString))
                {
                    logging.AddAzureMonitorLogExporter(options => options.ConnectionString = applicationInsightsConnectionString);
                }

                if (!String.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    logging.AddOtlpExporter(o =>
                    {
                        o.Endpoint = new Uri(otlpEndpoint);
                        o.Protocol = OtlpExportProtocol.Grpc;
                    });
                }
            })
            .SetMinimumLevel(LogLevel.Debug);

            return builder;
        }
    }
}
