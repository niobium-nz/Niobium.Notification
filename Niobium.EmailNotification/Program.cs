using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Cod.Database.StorageTable;
using Cod.File.Blob;
using Cod.Messaging.ServiceBus;
using Cod.Platform;
using Cod.Platform.Blob;
using Cod.Platform.Notification.Email.Resend;
using Cod.Platform.StorageTable;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Niobium.EmailNotification;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
#if DEBUG
    .ConfigureAppConfiguration(builder =>
        builder.SetBasePath(new FileInfo(Assembly.GetExecutingAssembly().Location).DirectoryName ?? throw new Exception())
        .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true))
#endif
    .ConfigureServices((ctx, s) =>
    {
        var isDevelopment = ctx.Configuration.IsDevelopmentEnvironment();

        s.AddApplicationInsightsTelemetryWorkerService();
        s.ConfigureFunctionsApplicationInsights();

        s.AddDatabase(ctx.Configuration.GetRequiredSection(nameof(StorageTableOptions)))
         .PostConfigure<StorageTableOptions>(opt => opt.EnableInteractiveIdentity = isDevelopment);

        s.AddFile(ctx.Configuration.GetRequiredSection(nameof(StorageBlobOptions)))
         .PostConfigure<StorageBlobOptions>(opt => opt.EnableInteractiveIdentity = isDevelopment);

        s.AddMessaging(ctx.Configuration.GetRequiredSection(nameof(ServiceBusOptions)))
         .PostConfigure<ServiceBusOptions>(opt => opt.EnableInteractiveIdentity = isDevelopment);

        s.AddNotification(ctx.Configuration.GetRequiredSection(nameof(ResendServiceOptions)));
        s.Configure<EmailNotificationOptions>((settings) =>
        {
            ctx.Configuration.GetSection(nameof(EmailNotificationOptions)).Bind(settings);
        });
        s.AddTransient<IVisitorRiskAssessor, GoogleReCaptchaRiskAssessor>();
        s.AddSingleton(HtmlEncoder.Create(allowedRanges: [UnicodeRanges.BasicLatin, UnicodeRanges.CjkUnifiedIdeographs]));
        s.AddHttpClient<IVisitorRiskAssessor, GoogleReCaptchaRiskAssessor>((sp, httpClient) =>
        {
            httpClient.BaseAddress = new Uri("https://www.google.com/");
        })
        .AddStandardResilienceHandler();
    })
    .UseDefaultServiceProvider((_, options) =>
    {
        options.ValidateScopes = true;
        options.ValidateOnBuild = true;
    })
    .Build();

host.Run();
