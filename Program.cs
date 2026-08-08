using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting.WindowsServices;
using NLog;
using NLog.Extensions.Logging;
using SecureFileTransferService;
using SecureFileTransferService.Services;

try
{
    // Load NLog properly
    LogManager.Setup()
        .LoadConfigurationFromFile(
            Path.Combine(AppContext.BaseDirectory, "NLog.config"));

    var host = Host.CreateDefaultBuilder(args)
        .UseWindowsService() //  IMPORTANT (fixes green bar + 1053)
        .ConfigureAppConfiguration((context, config) =>
        {
            config.SetBasePath(AppContext.BaseDirectory);
            config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        })
        .ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace);
            logging.AddNLog();
        })
        .ConfigureServices((context, services) =>
        {
            services.AddSingleton<MainProgram>();

            services.AddSingleton<IFileTransferService>(sp =>
            {
                var config = sp.GetRequiredService<IConfiguration>();
                var protocol = config["Protocol"] ?? "FTP";

                return protocol.Equals("SFTP", StringComparison.OrdinalIgnoreCase)
                    ? ActivatorUtilities.CreateInstance<SftpService>(sp)
                    : ActivatorUtilities.CreateInstance<FtpService>(sp);
            });

            services.AddHostedService<Worker>();
        })
        .Build();

    host.Run();
}
catch (Exception ex)
{
    // Startup error capture
    File.WriteAllText(
        Path.Combine(AppContext.BaseDirectory, "startup-error.txt"),
        ex.ToString()
    );
    throw;
}
finally
{
    LogManager.Shutdown();
}