using System;
using System.IO;
using System.IO.Compression;
using static System.IO.Compression.ZipFile;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileTransfer
{
    /// <summary>
    /// Background service that starts a Kestrel HTTP server and exposes a
    /// POST endpoint to receive files via multipart/form-data.
    /// When DecompressOnReceive is true (default) the received .gz is decompressed;
    /// otherwise the file is stored as-is.
    /// </summary>
    public class GzReceiverService : BackgroundService
    {
        private readonly ILogger<GzReceiverService> _logger;
        private readonly ReceiverOptions _options;
        private readonly ICallbackConfiguration _callbackConfiguration;
        private readonly IStartTrigger _startTrigger;
        private readonly IFileRecordStore _fileRecordStore;
        private readonly IHttpClientFactory _httpClientFactory;
        private WebApplication? _app;

        public GzReceiverService(
            ILogger<GzReceiverService> logger,
            IOptions<ReceiverOptions> options,
            ICallbackConfiguration callbackConfiguration,
            IStartTrigger startTrigger,
            IFileRecordStore fileRecordStore,
            IHttpClientFactory httpClientFactory)
        {
            _logger                = logger;
            _options               = options.Value;
            _callbackConfiguration = callbackConfiguration;
            _startTrigger          = startTrigger;
            _fileRecordStore       = fileRecordStore;
            _httpClientFactory     = httpClientFactory;
        }

        // ------------------------------------------------------------------ //
        //  Lifecycle
        // ------------------------------------------------------------------ //

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            EnsureDirectoriesExist();

            _app = BuildWebApplication();

            _logger.LogInformation("GzReceiverService starting on port {Port}.", _options.Port);
            _logger.LogInformation("  Receive endpoint   : {E}", _options.ReceiveEndpoint);
            _logger.LogInformation("  Received folder    : {R}", _options.ReceivedFolder);
            _logger.LogInformation("  Decompress         : {D}", _options.DecompressOnReceive);
            if (_options.DecompressOnReceive)
                _logger.LogInformation("  Decompressed folder: {D}", _options.DecompressedFolder);

            await _app.RunAsync(stoppingToken);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("GzReceiverService stopping.");
            if (_app is not null)
                await _app.StopAsync(cancellationToken);

            await base.StopAsync(cancellationToken);
        }

        // ------------------------------------------------------------------ //
        //  WebApplication setup
        // ------------------------------------------------------------------ //

        private WebApplication BuildWebApplication()
        {
            var builder = WebApplication.CreateBuilder();

            // Kestrel - listen only on the configured port
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.Listen(IPAddress.Any, _options.Port);
                k.Limits.MaxRequestBodySize = _options.MaxUploadBytes;
            });

            // Increase multipart body limit to match Kestrel
            builder.Services.Configure<FormOptions>(o =>
            {
                o.MultipartBodyLengthLimit = _options.MaxUploadBytes;
            });

            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            var app = builder.Build();

            app.UseSwagger();
            app.UseSwaggerUI();

            // Routes
            app.MapPost(_options.ReceiveEndpoint, (HttpRequest req) => HandleUploadAsync(req))
               .WithName("ReceiveFile")
               .WithOpenApi();
            app.MapGet("/filetransfer/registercallback", (HttpRequest req) => HandleRegisterCallback(req))
               .WithName("RegisterCallback")
               .WithOpenApi();
            app.MapPost("/filetransfer/status",
                    async (HttpRequest req) => await HandleUpdateStatusAsync(req))
               .WithName("ReceiveFileStatus")
               .WithOpenApi();
            app.MapGet("/filetransfer/statuses", () => HandleGetRecords())
               .WithName("GetFileStatuses")
               .WithOpenApi();
            app.MapGet("/filetransfer/ping", () => Results.Text(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")))
               .WithName("Ping")
               .WithOpenApi();

            return app;
        }

        // ------------------------------------------------------------------ //
        //  Request handler - internal so tests can call it directly
        // ------------------------------------------------------------------ //

        internal async Task<IResult> HandleUploadAsync(HttpRequest request)
        {
            if (!ValidateApiKey(request, out string? keyError))
            {
                _logger.LogWarning("Rejected request - {Reason}", keyError);
                return Results.Json(new { error = keyError }, statusCode: 401);
            }

            // The spec sends multipart/mixed with a 'file' section
            // (application/octet-stream) and a 'metadata' section
            // (application/json). It shares the same wire format as
            // multipart/form-data (Content-Disposition: form-data sections), so
            // re-tag it as multipart/form-data — keeping the boundary — and parse
            // it with the standard form reader.
            const string mixedType = "multipart/mixed";
            if (request.ContentType is { } ct &&
                ct.StartsWith(mixedType, StringComparison.OrdinalIgnoreCase))
            {
                request.ContentType = "multipart/form-data" + ct[mixedType.Length..];
            }

            if (!request.HasFormContentType)
            {
                _logger.LogWarning("Rejected request - Content-Type is not multipart/form-data or multipart/mixed.");
                return Results.Json(
                    new { error = "Content-Type must be multipart/form-data or multipart/mixed." },
                    statusCode: 415);
            }

            string originalName    = string.Empty;
            string applicationType = string.Empty;
            string timestamp       = string.Empty;
            try
            {
                var form = await request.ReadFormAsync();

                // Parse the optional metadata JSON field
                // ({"fileName","timestamp","applicationType"}).
                var metadataRaw = form["metadata"].ToString();
                if (!string.IsNullOrWhiteSpace(metadataRaw))
                {
                    try
                    {
                        var meta = JsonSerializer.Deserialize<UploadMetadata>(metadataRaw);
                        if (meta is not null)
                        {
                            applicationType = meta.ApplicationType;
                            timestamp       = meta.Timestamp;
                        }
                    }
                    catch { /* metadata is optional */ }
                }

                // When supplied, applicationType must be one of QCS, DFE, JRM.
                if (!string.IsNullOrEmpty(applicationType) && !ApplicationType.IsValid(applicationType))
                {
                    _logger.LogWarning("Rejected request - invalid applicationType '{A}'.", applicationType);
                    return Results.Json(
                        new { error = $"applicationType '{applicationType}' is not valid. Expected one of: {string.Join(", ", ApplicationType.Allowed)}." },
                        statusCode: 400);
                }

                IFormFile? file = form.Files[_options.FormFieldName];
                if (file is null)
                {
                    _logger.LogWarning("Rejected request - form field '{F}' not found.", _options.FormFieldName);
                    return Results.Json(
                        new { error = $"Form field '{_options.FormFieldName}' is required." },
                        statusCode: 400);
                }

                originalName = Path.GetFileName(file.FileName);

                if (!originalName.EndsWith(".gz",  StringComparison.OrdinalIgnoreCase) &&
                    !originalName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Rejected file '{F}' - not a .gz or .zip file.", originalName);
                    _fileRecordStore.Add(new FileRecord
                    {
                        FileName        = originalName,
                        Timestamp       = timestamp,
                        ApplicationType = applicationType,                        Status          = FileStatus.FileFormatError,
                        Message         = "Only .gz and .zip files are accepted."
                    });
                    return Results.Json(
                        new { error = "Only .gz and .zip files are accepted." },
                        statusCode: 400);
                }

                string gzPath = await SaveGzFileAsync(file, originalName);
                _logger.LogInformation("Saved file: {P} ({Bytes} bytes)", gzPath, file.Length);

                if (_options.DecompressOnReceive)
                {
                    string decompressedPath = await DecompressAsync(gzPath, originalName);
                    _logger.LogInformation("Decompressed to: {P}", decompressedPath);

                    if (!_options.KeepGzAfterDecompression)
                    {
                        File.Delete(gzPath);
                        _logger.LogDebug("Deleted archive file: {P}", gzPath);
                    }

                    _fileRecordStore.Add(new FileRecord
                    {
                        FileName        = originalName,
                        Timestamp       = timestamp,
                        ApplicationType = applicationType,                        Status          = FileStatus.Unknown,
                        Message         = "File received and decompressed successfully."
                    });

                    // Spec: a successful file transfer returns 204 (no content).
                    return Results.NoContent();
                }

                _fileRecordStore.Add(new FileRecord
                {
                    FileName        = originalName,
                    Timestamp       = timestamp,
                    ApplicationType = applicationType,                    Status          = FileStatus.Unknown,
                    Message         = "File received successfully."
                });

                // Spec: a successful file transfer returns 204 (no content).
                return Results.NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing upload.");
                if (!string.IsNullOrWhiteSpace(originalName))
                    _fileRecordStore.Add(new FileRecord
                    {
                        FileName        = originalName,
                        Timestamp       = timestamp,
                        ApplicationType = applicationType,                        Status          = FileStatus.FileTransferError,
                        Message         = ex.Message
                    });
                return Results.Json(
                    new { error = "Internal server error.", detail = ex.Message },
                    statusCode: 500);
            }
        }

        // ------------------------------------------------------------------ //
        //  Get all file records
        // ------------------------------------------------------------------ //

        internal IResult HandleGetRecords()
        {
            var records = _fileRecordStore.GetAll();
            return Results.Json(records, statusCode: 200);
        }

        // ------------------------------------------------------------------ //
        //  Update file status
        // ------------------------------------------------------------------ //

        internal async Task<IResult> HandleUpdateStatusAsync(HttpRequest request)
        {
            UpdateStatusRequest? body;
            try
            {
                body = await request.ReadFromJsonAsync<UpdateStatusRequest>();
            }
            catch
            {
                return Results.Json(new { error = "Invalid JSON body." }, statusCode: 400);
            }

            if (body is null || string.IsNullOrWhiteSpace(body.FileName))
                return Results.Json(new { error = "fileName is required." }, statusCode: 400);

            string[] allowed =
            [
                FileStatus.FileTransferSuccess,
                FileStatus.FileTransferError,
                FileStatus.FileFormatError,
                FileStatus.FileValidationError,
                FileStatus.Valid
            ];

            if (!allowed.Contains(body.Status))
                return Results.Json(new { error = $"Status '{body.Status}' is not valid." }, statusCode: 400);

            var record = _fileRecordStore.Get(body.FileName);
            if (record is null)
                return Results.Json(new { error = $"File '{body.FileName}' not found." }, statusCode: 404);

            record.Status  = body.Status;
            record.Message = body.Message;
            if (!string.IsNullOrWhiteSpace(body.ApplicationType))
                record.ApplicationType = body.ApplicationType;
            if (!string.IsNullOrWhiteSpace(body.Timestamp))
                record.Timestamp = body.Timestamp;

            // Persist the change. The record is kept in the store regardless of status.
            _fileRecordStore.Add(record);

            // Notify the callback for every status EXCEPT 'valid'.
            if (body.Status != FileStatus.Valid)
                await SendRecordToCallbackAsync(record);

            return Results.Ok();
        }

        /// <summary>
        /// Posts a file record to the registered callback endpoint. Called for
        /// every status update other than <c>valid</c>. When the callback is
        /// delivered successfully (2xx) the record is removed from the store;
        /// otherwise it is kept so the change is not lost.
        /// </summary>
        private async Task SendRecordToCallbackAsync(FileRecord record)
        {
            if (!_callbackConfiguration.IsRegistered)
            {
                _logger.LogWarning(
                    "Cannot send callback for {FileName}: no callback URL registered.", record.FileName);
                return;
            }

            using var http = _httpClientFactory.CreateClient();
            try
            {
                var resp = await http.PostAsJsonAsync(_callbackConfiguration.CallbackEndpoint, record);
                _logger.LogInformation("Callback sent for {FileName} ({Status}): HTTP {Code}",
                    record.FileName, record.Status, (int)resp.StatusCode);

                if (resp.IsSuccessStatusCode)
                    _fileRecordStore.Remove(record.FileName);
                else
                    _logger.LogWarning(
                        "Callback for {FileName} returned non-success {Code}; record kept.",
                        record.FileName, (int)resp.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send callback for {FileName}; record kept.", record.FileName);
            }
        }

        // ------------------------------------------------------------------ //
        //  Register callback
        // ------------------------------------------------------------------ //

        internal IResult HandleRegisterCallback(HttpRequest request)
        {
            string? callbackEndpoint = request.Query["callbackuri"];

            if (string.IsNullOrWhiteSpace(callbackEndpoint) || !IsUrlValid(callbackEndpoint))
            {
                var error = new ErrorPayload
                {
                    Code = "callbackEndpointError",
                    Name = $"Uri '{callbackEndpoint}' is not valid."
                };
                _logger.LogError("{ErrorPayload}", error.ToString());
                return Results.Json(error, statusCode: 400);
            }

            _callbackConfiguration.CallbackEndpoint = callbackEndpoint;
            _startTrigger.Signal();

            _logger.LogInformation("Callback endpoint registered: {Endpoint}", callbackEndpoint);
            return Results.NoContent();
        }

        private static bool IsUrlValid(string source)
        {
            var isUriValid = Uri.TryCreate(source, UriKind.Absolute, out var uri);
            if (isUriValid && uri is not null)
                return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
            return false;
        }

        // ------------------------------------------------------------------ //
        //  Save file to ReceivedFolder
        // ------------------------------------------------------------------ //

        private async Task<string> SaveGzFileAsync(IFormFile file, string originalName)
        {
            string destPath = Path.Combine(_options.ReceivedFolder, originalName);
            EnsureUnique(ref destPath);

            await using var dest = new FileStream(
                destPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true);

            await file.CopyToAsync(dest);
            return destPath;
        }

        // ------------------------------------------------------------------ //
        //  Decompress -> DecompressedFolder
        // ------------------------------------------------------------------ //

        private Task<string> DecompressAsync(string archivePath, string originalName) =>
            originalName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? DecompressZipAsync(archivePath, originalName)
                : DecompressGzAsync(archivePath, originalName);

        private async Task<string> DecompressGzAsync(string gzPath, string originalName)
        {
            // Strip .gz -> "data.csv.gz" becomes "data.csv"
            string innerName = originalName[..^3];
            string destPath  = Path.Combine(_options.DecompressedFolder, innerName);
            EnsureUnique(ref destPath);

            await using var compressedStream = new FileStream(
                gzPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, useAsync: true);

            await using var gzStream = new GZipStream(compressedStream, CompressionMode.Decompress);

            await using var outputStream = new FileStream(
                destPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true);

            await gzStream.CopyToAsync(outputStream);
            return destPath;
        }

        private async Task<string> DecompressZipAsync(string zipPath, string originalName)
        {
            string destDir = Path.Combine(_options.DecompressedFolder,
                Path.GetFileNameWithoutExtension(originalName));

            Directory.CreateDirectory(destDir);

            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, destDir, overwriteFiles: true));

            _logger.LogInformation("Extracted zip to: {Dir}", destDir);
            return destDir;
        }

        // ------------------------------------------------------------------ //
        //  Helpers
        // ------------------------------------------------------------------ //

        private void EnsureDirectoriesExist()
        {
            Directory.CreateDirectory(_options.ReceivedFolder);
            if (_options.DecompressOnReceive)
                Directory.CreateDirectory(_options.DecompressedFolder);
        }

        private bool ValidateApiKey(HttpRequest request, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(_options.ApiKey))
                return true;  // check disabled

            if (!request.Headers.TryGetValue("X-Api-Key", out var provided) ||
                provided.ToString() != _options.ApiKey)
            {
                error = "Missing or invalid X-Api-Key header.";
                return false;
            }
            return true;
        }

        private static void EnsureUnique(ref string path)
        {
            if (!File.Exists(path)) return;
            string dir  = Path.GetDirectoryName(path)!;
            string name = Path.GetFileNameWithoutExtension(path);
            string ext  = Path.GetExtension(path);
            path = Path.Combine(dir, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss_fff}{ext}");
        }
    }
}
