using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileTransfer
{
    /// <summary>
    /// Background service: watches a hot folder and uploads
    /// each new file to the configured REST API.
    /// </summary>
    public class HotFolderService : BackgroundService
    {
        private readonly ILogger<HotFolderService> _logger;
        private readonly HotFolderOptions _options;
        private readonly IFileTransferHandler _transferHandler;
        private readonly IStartTrigger _startTrigger;
        private readonly ICallbackConfiguration _callbackConfiguration;
        private readonly SemaphoreSlim _semaphore;
        private FileSystemWatcher? _watcher;

        public HotFolderService(
            ILogger<HotFolderService> logger,
            IOptions<HotFolderOptions> options,
            IFileTransferHandler transferHandler,
            IStartTrigger startTrigger,
            ICallbackConfiguration callbackConfiguration)
        {
            _logger                = logger;
            _options               = options.Value;
            _transferHandler       = transferHandler;
            _startTrigger          = startTrigger;
            _callbackConfiguration = callbackConfiguration;
            _semaphore             = new SemaphoreSlim(_options.Api.MaxConcurrentUploads);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_callbackConfiguration.IsRegistered)
            {
                _logger.LogInformation("Callback endpoint already registered — starting immediately.");
                _startTrigger.Signal();
            }
            else
            {
                _logger.LogInformation("HotFolderService waiting for callback registration.");
            }

            await _startTrigger.WaitAsync(stoppingToken);

            if (stoppingToken.IsCancellationRequested)
                return;

            _logger.LogInformation("HotFolderService starting.");
            _logger.LogInformation("  Source   : {S}", _options.SourceFolder);
            _logger.LogInformation("  API base : {U}", _options.Api.BaseUrl);
            _logger.LogInformation("  Endpoint : {E}", _options.Api.UploadEndpoint);
            _logger.LogInformation("  Filter   : {F}", string.Join(", ", GetFilterPatterns()));

            EnsureDirectoriesExist();
            ProcessExistingFiles(stoppingToken);
            StartWatcher(stoppingToken);
        }

        // ------------------------------------------------------------------ //
        //  Startup helpers
        // ------------------------------------------------------------------ //

        private static readonly char[] FilterSeparators = { ' ', ',', ';', '|', '\t' };

        /// <summary>
        /// Splits <see cref="HotFolderOptions.FileFilter"/> into one or more
        /// glob patterns. Supports a single pattern ("*.pdf") or several
        /// separated by space, comma, semicolon or pipe ("*.pdf *.jpg *.7z").
        /// Falls back to "*.*" when nothing usable is configured.
        /// </summary>
        private string[] GetFilterPatterns()
        {
            var patterns = (_options.FileFilter ?? string.Empty)
                .Split(FilterSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return patterns.Length == 0 ? new[] { "*.*" } : patterns;
        }

        private void EnsureDirectoriesExist()
        {
            Directory.CreateDirectory(_options.SourceFolder);
            if (_options.ArchiveEnabled)    Directory.CreateDirectory(_options.ArchiveFolder);
            if (_options.ErrorFolderEnabled) Directory.CreateDirectory(_options.ErrorFolder);

            // Ensure each configured applicationType has its drop sub-folder, so
            // producing applications always find their folder ready.
            foreach (var folder in _options.ApplicationTypeFolders.Keys)
            {
                string path = Path.Combine(_options.SourceFolder, folder);
                Directory.CreateDirectory(path);
                _logger.LogInformation("Ensured applicationType folder: {Path}", path);
            }
        }

        private void ProcessExistingFiles(CancellationToken ct)
        {
            var files = GetFilterPatterns()
                .SelectMany(pattern => Directory.EnumerateFiles(_options.SourceFolder, pattern))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (files.Length == 0) return;

            _logger.LogInformation("Processing {Count} pre-existing file(s).", files.Length);
            foreach (var f in files)
                _ = UploadWithThrottleAsync(f, ct);
        }

        // ------------------------------------------------------------------ //
        //  FileSystemWatcher
        // ------------------------------------------------------------------ //

        private void StartWatcher(CancellationToken stoppingToken)
        {
            _watcher = new FileSystemWatcher(_options.SourceFolder)
            {
                NotifyFilter          = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = _options.WatchSubdirectories
            };

            // FileSystemWatcher.Filter holds a single pattern; the Filters
            // collection OR-matches several (e.g. "*.pdf *.jpg *.7z").
            _watcher.Filters.Clear();
            foreach (var pattern in GetFilterPatterns())
                _watcher.Filters.Add(pattern);

            _watcher.EnableRaisingEvents = true;

            _watcher.Created += (_, e) =>
            {
                _logger.LogInformation("File detected: {F}", e.FullPath);
                _ = UploadWithThrottleAsync(e.FullPath, stoppingToken);
            };

            _watcher.Error += (_, e) =>
            {
                _logger.LogError(e.GetException(), "Watcher error — restarting.");
                _watcher?.Dispose();
                StartWatcher(stoppingToken);
            };

            stoppingToken.Register(() =>
            {
                _logger.LogInformation("HotFolderService stopping.");
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _semaphore.Dispose();
            });

            _logger.LogInformation("FileSystemWatcher active.");
        }

        // ------------------------------------------------------------------ //
        //  Throttled upload
        // ------------------------------------------------------------------ //

        private async Task UploadWithThrottleAsync(string filePath, CancellationToken ct)
        {
            await _semaphore.WaitAsync(ct);
            try
            {
                await _transferHandler.TransferAsync(filePath, ct);
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }
}

