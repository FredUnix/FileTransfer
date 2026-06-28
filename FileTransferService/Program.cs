using System;
using System.Reflection;
using FileTransfer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var appVersion = Assembly
        .GetEntryAssembly()
        ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion ?? "unknown";

    var title     = $"FileTransfer {appVersion}";
    var separator = new string('=', title.Length + 4);
    Log.Information(separator);
    Log.Information("= {Title} =", title);
    Log.Information(separator);

    IHost host = Host.CreateDefaultBuilder(args)
        .UseWindowsService(options =>
        {
            options.ServiceName = "FileTransfer";
        })
        .UseSerilog((context, _, loggerConfig) =>
            loggerConfig.ReadFrom.Configuration(context.Configuration))
        .ConfigureServices((context, services) =>
        {
            services.Configure<HotFolderOptions>(context.Configuration.GetSection("HotFolder"));
            services.Configure<ReceiverOptions>(context.Configuration.GetSection("Receiver"));

            services.AddHttpClient<IFileTransferHandler, RestFileTransferHandler>((sp, client) =>
            {
                var opts = sp.GetRequiredService<IOptions<HotFolderOptions>>().Value;
                client.BaseAddress = new Uri(opts.Api.BaseUrl);
                if (opts.Api.TimeoutSeconds > 0)
                    client.Timeout = TimeSpan.FromSeconds(opts.Api.TimeoutSeconds);
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            });

            services.AddSingleton<ICallbackConfiguration, CallbackConfiguration>();
            services.AddSingleton<IStartTrigger, StartTrigger>();
            services.AddSingleton<IFileRecordStore, FileRecordStore>();
            services.AddHttpClient();

            services.AddHostedService<HotFolderService>();
            services.AddHostedService<GzReceiverService>();
            services.AddHostedService<RecordCleanupService>();
            services.AddHostedService<HotFolderCleanupService>();
        })
        .Build();

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly.");
}
finally
{
    await Log.CloseAndFlushAsync();
}
