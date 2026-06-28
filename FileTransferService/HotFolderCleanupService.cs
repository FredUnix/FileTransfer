using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileTransfer
{
    /// <summary>
    /// Background service that periodically deletes files older than
    /// <see cref="HotFolderOptions.RetentionHours"/> from the source, error and
    /// archive folders. This keeps the hot-folder area from growing without bound
    /// (failed/archived files would otherwise linger forever). Disabled when the
    /// retention is 0 or negative.
    /// </summary>
    public sealed class HotFolderCleanupService : BackgroundService
    {
        private readonly ILogger<HotFolderCleanupService> _logger;
        private readonly HotFolderOptions _options;
        private readonly IFileRecordStore _fileRecordStore;

        public HotFolderCleanupService(
            ILogger<HotFolderCleanupService> logger,
            IOptions<HotFolderOptions> options,
            IFileRecordStore fileRecordStore)
        {
            _logger          = logger;
            _options         = options.Value;
            _fileRecordStore = fileRecordStore;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_options.RetentionHours <= 0)
            {
                _logger.LogInformation("Hot-folder cleanup disabled (RetentionHours <= 0).");
                return;
            }

            var retention = TimeSpan.FromHours(_options.RetentionHours);
            var interval  = TimeSpan.FromMinutes(Math.Max(1, _options.CleanupIntervalMinutes));

            _logger.LogInformation(
                "Hot-folder cleanup active: retention {Hours}h, sweeping every {Minutes}min.",
                _options.RetentionHours, interval.TotalMinutes);

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
            DateTime cutoffUtc = DateTime.UtcNow - retention;
            int total = 0;
            total += PurgeFolder(_options.SourceFolder,  cutoffUtc);
            total += PurgeFolder(_options.ErrorFolder,   cutoffUtc);
            total += PurgeFolder(_options.ArchiveFolder, cutoffUtc);

            if (total > 0)
                _logger.LogInformation("Hot-folder cleanup removed {Count} file(s).", total);
        }

        /// <summary>Deletes files (recursively, not directories) older than the
        /// cutoff. Missing folders are skipped.</summary>
        private int PurgeFolder(string folder, DateTime cutoffUtc)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return 0;

            int count = 0;
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoffUtc)
                    {
                        File.Delete(file);
                        count++;
                        _logger.LogDebug("Hot-folder cleanup deleted: {File}", file);

                        // Also drop a matching status record, if one exists.
                        string name = Path.GetFileName(file);
                        if (_fileRecordStore.Get(name) is not null)
                        {
                            _fileRecordStore.Remove(name);
                            _logger.LogInformation("Removed status record for deleted file: {Name}", name);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Hot-folder cleanup could not delete {File}.", file);
                }
            }
            return count;
        }
    }
}
