using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileTransfer
{
    /// <summary>
    /// Background service that periodically purges file records older than
    /// <see cref="ReceiverOptions.RecordRetentionHours"/>. This is the safety net
    /// for records that never receive a status update (and so are never removed by
    /// the callback path). Disabled when the retention is 0 or negative.
    /// </summary>
    public sealed class RecordCleanupService : BackgroundService
    {
        private readonly ILogger<RecordCleanupService> _logger;
        private readonly ReceiverOptions _options;
        private readonly IFileRecordStore _store;

        public RecordCleanupService(
            ILogger<RecordCleanupService> logger,
            IOptions<ReceiverOptions> options,
            IFileRecordStore store)
        {
            _logger  = logger;
            _options = options.Value;
            _store   = store;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_options.RecordRetentionHours <= 0)
            {
                _logger.LogInformation("Record cleanup disabled (RecordRetentionHours <= 0).");
                return;
            }

            var retention = TimeSpan.FromHours(_options.RecordRetentionHours);
            var interval  = TimeSpan.FromMinutes(Math.Max(1, _options.RecordCleanupIntervalMinutes));

            _logger.LogInformation(
                "Record cleanup active: retention {Hours}h, sweeping every {Minutes}min.",
                _options.RecordRetentionHours, interval.TotalMinutes);

            using var timer = new PeriodicTimer(interval);
            try
            {
                do
                {
                    Sweep(retention);
                }
                while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
        }

        private void Sweep(TimeSpan retention)
        {
            try
            {
                var cutoff  = DateTimeOffset.UtcNow - retention;
                int removed = _store.RemoveOlderThan(cutoff);
                if (removed > 0)
                    _logger.LogInformation("Record cleanup removed {Count} stale record(s).", removed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Record cleanup sweep failed.");
            }
        }
    }
}
