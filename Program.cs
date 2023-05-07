using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(s =>
    {
        //s.AddSingleton<IHttpResponderService, DefaultHttpResponderService>();
    })
    .Build();

host.Run();
