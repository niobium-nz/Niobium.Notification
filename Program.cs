using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Niobium.EmailNotification;
using SendGrid.Extensions.DependencyInjection;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        _ = services.AddSendGrid(options =>
            options.ApiKey = "test1"
        );
        _ = services.AddTransient<IEmailSender, SendGridEmailSender>();
        _ = services.AddTransient<TenantConfigurationProvider>();
    })
    .Build();

host.Run();
