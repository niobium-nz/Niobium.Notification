using System.Text.Encodings.Web;
using System.Text.Unicode;
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
        _ = services.AddSingleton(HtmlEncoder.Create(
            allowedRanges: new[] { UnicodeRanges.BasicLatin, UnicodeRanges.CjkUnifiedIdeographs }));
    })
    .Build();

host.Run();
