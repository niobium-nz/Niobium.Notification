using System.Reflection;
using Cod.Platform.Notification.Email.Resend;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Niobium.EmailNotification;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
#if DEBUG
    .ConfigureAppConfiguration(builder =>
        builder.SetBasePath(new FileInfo(Assembly.GetExecutingAssembly().Location).DirectoryName ?? throw new Exception())
        .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true))
#endif
    .ConfigureServices((ctx, s) => s.AddNotification(ctx))
    .UseDefaultServiceProvider((_, options) =>
    {
        options.ValidateScopes = true;
        options.ValidateOnBuild = true;
    })
    .Build();

host.Run();
