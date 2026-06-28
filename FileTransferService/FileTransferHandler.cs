using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileTransfer
{
    public interface IFileTransferHandler
    {
        Task TransferAsync(string sourceFilePath, CancellationToken ct);
    }

    /// <summary>
    /// Uploads a file to a REST endpoint using multipart/form-data.
    /// Includes lock detection, retry logic, and error/archive folder support.
    /// </summary>
    public class RestFileTransferHandler : IFileTransferHandler
    {
        private readonly ILogger<RestFileTransferHandler> _logger;
        private readonly HotFolderOptions _options;
        private readonly HttpClient _httpClient;
        private readonly ICallbackConfiguration _callbackConfiguration;

        public RestFileTransferHandler(
            ILogger<RestFileTransferHandler> logger,
            IOptions<HotFolderOptions> options,
            HttpClient httpClient,
            ICallbackConfiguration callbackConfiguration)
        {
            _logger                = logger;
            _options               = options.Value;
            _httpClient            = httpClient;
            _callbackConfiguration = callbackConfiguration;
        }

        // ------------------------------------------------------------------ //
        //  Entry point
        // ------------------------------------------------------------------ //

        public async Task TransferAsync(string sourceFilePath, CancellationToken ct)
        {
            _logger.LogInformation("Transfer starting: {File}", sourceFilePath);

            // 1. Wait until the file is fully written
            if (!await WaitForFileReadyAsync(sourceFilePath, ct))
            {
                _logger.LogError("File locked after {S}s — skipping: {File}",
                    _options.LockTimeoutSeconds, sourceFilePath);
                MoveToError(sourceFilePath);
                return;
            }

            // 1b. Resolve the applicationType from the producing sub-folder.
            //     When folder routing is configured, a file that maps to no
            //     applicationType cannot be tagged and is moved to the error folder.
            string? applicationType = ResolveApplicationType(sourceFilePath);
            if (_options.ApplicationTypeFolders.Count > 0 && string.IsNullOrEmpty(applicationType))
            {
                _logger.LogError(
                    "Cannot determine applicationType for {File} (not under a mapped sub-folder); moving to error.",
                    sourceFilePath);
                MoveToError(sourceFilePath);
                return;
            }

            // 2. Upload with retries.
            //    MaxRetries <= 0 means "retry forever" — keep trying until the
            //    upload succeeds or the service is shutting down (ct cancelled).
            bool infiniteRetries = _options.MaxRetries <= 0;
            string maxLabel      = infiniteRetries ? "∞" : _options.MaxRetries.ToString();

            bool success = false;
            for (int attempt = 1; infiniteRetries || attempt <= _options.MaxRetries; attempt++)
            {
                try
                {
                    await UploadFileAsync(sourceFilePath, applicationType, ct);
                    _logger.LogInformation("Upload OK (attempt {A}): {File}", attempt, sourceFilePath);
                    success = true;
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Upload attempt {A}/{Max} failed for {File}",
                        attempt, maxLabel, sourceFilePath);

                    // Keep retrying while in infinite mode, or until the last attempt.
                    if (infiniteRetries || attempt < _options.MaxRetries)
                        await Task.Delay(TimeSpan.FromSeconds(_options.RetryDelaySeconds), ct);
                    else
                        break;
                }
            }

            // 3. Post-transfer source handling
            if (success)
                HandleSourceAfterSuccess(sourceFilePath);
            else
            {
                _logger.LogError("All {Max} upload attempts failed for {File}",
                    maxLabel, sourceFilePath);
                MoveToError(sourceFilePath);
            }
        }

        // ------------------------------------------------------------------ //
        //  HTTP upload — multipart/form-data
        // ------------------------------------------------------------------ //

        private async Task UploadFileAsync(string filePath, string? applicationType, CancellationToken ct)
        {
            string fileName  = Path.GetFileName(filePath);
            string mimeType  = ResolveMimeType(fileName);

            var callbackBase = _callbackConfiguration.CallbackEndpoint;
            string endpoint  = string.IsNullOrWhiteSpace(callbackBase)
                ? _options.Api.UploadEndpoint
                : callbackBase.TrimEnd('/') + _options.Api.UploadEndpoint;

            _logger.LogDebug("POST {Endpoint}  file={File}  mime={Mime}", endpoint, fileName, mimeType);

            await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            using var content    = new MultipartFormDataContent();
            using var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);

            // Field name and filename come from configuration
            content.Add(fileContent, _options.Api.FormFieldName, fileName);

            // Metadata JSON section ({fileName, timestamp, applicationType}).
            if (!string.IsNullOrEmpty(applicationType))
            {
                string metadata = JsonSerializer.Serialize(new
                {
                    fileName,
                    timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    applicationType
                });
                content.Add(new StringContent(metadata, Encoding.UTF8, "application/json"), "metadata");
            }

            // Add any extra static form fields defined in config
            foreach (var field in _options.Api.ExtraFormFields)
                content.Add(new StringContent(field.Value), field.Key);

            using var response = await _httpClient.PostAsync(endpoint, content, ct);

            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException(
                    $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
            }

            _logger.LogInformation("Server response: {Status}", (int)response.StatusCode);
        }

        // ------------------------------------------------------------------ //
        //  Lock detection
        // ------------------------------------------------------------------ //

        private async Task<bool> WaitForFileReadyAsync(string path, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddSeconds(_options.LockTimeoutSeconds);

            while (DateTime.UtcNow < deadline)
            {
                if (ct.IsCancellationRequested) return false;
                if (!File.Exists(path))
                {
                    _logger.LogWarning("File disappeared: {File}", path);
                    return false;
                }
                if (IsFileReady(path)) return true;

                _logger.LogDebug("File locked, waiting {Ms}ms …", _options.LockPollIntervalMs);
                await Task.Delay(_options.LockPollIntervalMs, ct);
            }
            return false;
        }

        private static bool IsFileReady(string path)
        {
            try
            {
                using var s = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return s.Length > 0;
            }
            catch (IOException) { return false; }
        }

        // ------------------------------------------------------------------ //
        //  Post-transfer source handling
        // ------------------------------------------------------------------ //

        private void HandleSourceAfterSuccess(string sourceFile)
        {
            try
            {
                if (_options.ArchiveEnabled)
                {
                    string archivePath = Path.Combine(_options.ArchiveFolder, Path.GetFileName(sourceFile));
                    EnsureUnique(ref archivePath);
                    File.Move(sourceFile, archivePath);
                    _logger.LogInformation("Archived: {P}", archivePath);
                }
                else if (_options.DeleteSourceAfterTransfer)
                {
                    File.Delete(sourceFile);
                    _logger.LogInformation("Deleted source: {F}", sourceFile);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Post-transfer action failed for: {F}", sourceFile);
            }
        }

        private void MoveToError(string sourceFile)
        {
            if (!_options.ErrorFolderEnabled || !File.Exists(sourceFile)) return;
            try
            {
                string errorPath = Path.Combine(_options.ErrorFolder, Path.GetFileName(sourceFile));
                EnsureUnique(ref errorPath);
                File.Move(sourceFile, errorPath);
                _logger.LogWarning("Moved to error folder: {P}", errorPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not move to error folder: {F}", sourceFile);
            }
        }

        // ------------------------------------------------------------------ //
        //  Helpers
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Resolves the applicationType for a file from the immediate sub-folder
        /// of <see cref="HotFolderOptions.SourceFolder"/> it sits under, using
        /// <see cref="HotFolderOptions.ApplicationTypeFolders"/>. Returns null when
        /// routing is not configured or the file is not under a mapped sub-folder.
        /// </summary>
        private string? ResolveApplicationType(string filePath)
        {
            if (_options.ApplicationTypeFolders.Count == 0)
                return null;

            string relative = Path.GetRelativePath(_options.SourceFolder, filePath);
            string[] segments = relative.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            // segments: [<sub-folder>, ..., <fileName>]; need at least one sub-folder.
            if (segments.Length < 2)
                return null;

            string folder = segments[0];
            return _options.ApplicationTypeFolders
                .FirstOrDefault(kv => string.Equals(kv.Key, folder, StringComparison.OrdinalIgnoreCase))
                .Value;
        }

        private static void EnsureUnique(ref string path)
        {
            if (!File.Exists(path)) return;
            string dir  = Path.GetDirectoryName(path)!;
            string name = Path.GetFileNameWithoutExtension(path);
            string ext  = Path.GetExtension(path);
            path = Path.Combine(dir, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss_fff}{ext}");
        }

        private static string ResolveMimeType(string fileName) =>
            Path.GetExtension(fileName).ToLowerInvariant() switch
            {
                ".pdf"  => "application/pdf",
                ".xml"  => "application/xml",
                ".json" => "application/json",
                ".csv"  => "text/csv",
                ".txt"  => "text/plain",
                ".png"  => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".zip"  => "application/zip",
                _       => "application/octet-stream"
            };
    }
}

