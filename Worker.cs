using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SecureFileTransferService
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IConfiguration _config;
        private readonly MainProgram _mainProgram;

        private readonly int _loopInterval;

        public Worker(
            ILogger<Worker> logger,
            IConfiguration config,
            MainProgram mainProgram)
        {
            _logger = logger;
            _config = config;
            _mainProgram = mainProgram;

            _loopInterval = _config.GetValue<int>("WorkerSettings:LoopInterval");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("=========================SERVICE STARTED=========================");

            await Task.Delay(2000, stoppingToken);

            bool isEnabled = _config.GetValue<bool>("WorkerSettings:IsEnabled");

            //_logger.LogInformation("IsEnabled: {value}", isEnabled);

            //CASE 1: Worker DISABLED → RUN ONCE
            if (!isEnabled)
            {
                try
                {
                    //_logger.LogInformation("Running ONE-TIME processing...");

                    await _mainProgram.RunAsync(stoppingToken);

                    _logger.LogInformation("Service Stopped");
                    _logger.LogInformation("====================================================");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in one-time processing");
                }

                return; //STOP SERVICE AFTER ONE RUN
            }

            //CASE 2: Worker ENABLED → CONTINUOUS LOOP
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("====================================================");
                    _logger.LogInformation("Running at: {time}", DateTime.Now);

                    await _mainProgram.RunAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Worker Error: {message}", ex.Message);
                }

                try
                {
                    await Task.Delay(_loopInterval, stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }

            _logger.LogInformation("Service Stopped");
            _logger.LogInformation("====================================================");
        }
    }

    }