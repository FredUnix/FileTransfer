using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace FileTransfer.Tests
{
    /// <summary>
    /// Integration-style unit tests for GzReceiverService.
    /// Uses an ASP.NET Core TestServer (in-memory, no network socket).
    /// Each test exercises the full pipeline:
    ///   multipart parse â†’ .gz save â†’ GZipStream decompress â†’ file system assertions.
    /// </summary>
    public class GzReceiverServiceTests : IDisposable
    {
        private readonly TempFolder _tmp = new();
        public void Dispose() => _tmp.Dispose();

        // Captures callback HTTP requests sent by the most recent BuildTestServer().
        private StubHttpClientFactory _httpFactory = new();

        // ------------------------------------------------------------------ //
        //  TestServer factory
        // ------------------------------------------------------------------ //

        private (IHost host, HttpClient client, StubCallbackConfiguration callbackConfig, StartTrigger trigger, StubFileRecordStore fileRecordStore) BuildTestServer(
            string formFieldName      = "file",
            bool   keepGz             = false,
            long   maxUploadBytes     = 50 * 1024 * 1024,
            string apiKey             = "",
            bool   decompressOnReceive = true)
        {
            var opts = ReceiverOptionsFactory.Create(
                _tmp,
                formFieldName:       formFieldName,
                keepGz:              keepGz,
                maxUploadBytes:      maxUploadBytes,
                apiKey:              apiKey,
                decompressOnReceive: decompressOnReceive);

            var callbackConfig  = new StubCallbackConfiguration();
            var trigger         = new StartTrigger();
            var fileRecordStore = new StubFileRecordStore();
            var httpFactory     = _httpFactory = new StubHttpClientFactory();

            var host = new HostBuilder()
                .ConfigureWebHost(webHost =>
                {
                    webHost.UseTestServer();
                    webHost.ConfigureServices(services =>
                    {
                        services.Configure<FormOptions>(o =>
                            o.MultipartBodyLengthLimit = maxUploadBytes);
                        services.AddRouting();
                    });
                    webHost.Configure(app =>
                    {
                        var svc = new GzReceiverService(
                            NullLogger<GzReceiverService>.Instance, opts,
                            callbackConfig,
                            trigger,
                            fileRecordStore,
                            httpFactory);

                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapPost(
                                opts.Value.ReceiveEndpoint,
                                (HttpRequest req) => svc.HandleUploadAsync(req));

                            endpoints.MapGet("/filetransfer/registercallback",
                                (HttpRequest req) => svc.HandleRegisterCallback(req));

                            endpoints.MapPost("/filetransfer/status",
                                async (HttpRequest req) => await svc.HandleUpdateStatusAsync(req));

                            endpoints.MapGet("/filetransfer/statuses",
                                () => svc.HandleGetRecords());

                            endpoints.MapGet("/filetransfer/ping",
                                () => Results.Text(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")));

                            endpoints.MapGet("/health",
                                () => Results.Ok(new { status = "ok" }));
                        });
                    });
                })
                .Start();

            return (host, host.GetTestServer().CreateClient(), callbackConfig, trigger, fileRecordStore);
        }

        // ------------------------------------------------------------------ //
        //  Happy path
        // ------------------------------------------------------------------ //

        [Fact]
        public async Task Post_ValidGz_Returns204()
        {
            var (_, client, _, _, _) = BuildTestServer();
            var body = MultipartGzBuilder.Build("export.csv.gz", "id,name\n1,Alice");

            var response = await client.PostAsync("/filetransfer", body);

            // Spec: a successful file transfer returns 204 No Content.
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        [Fact]
        public async Task Post_ValidGz_DecompressedFileContainsOriginalContent()
        {
            var (_, client, _, _, _) = BuildTestServer();
            const string content = "id,name\n1,Alice\n2,Bob";
            var body = MultipartGzBuilder.Build("export.csv.gz", content);

            await client.PostAsync("/filetransfer", body);

            string[] files = Directory.GetFiles(Path.Combine(_tmp.Root, "decompressed"));
            Assert.Single(files);
            Assert.Equal("export.csv",    Path.GetFileName(files[0]));
            Assert.Equal(content,         File.ReadAllText(files[0], Encoding.UTF8));
        }

        [Fact]
        public async Task Post_ValidGz_Returns204WithEmptyBody()
        {
            var (_, client, _, _, store) = BuildTestServer();
            var body = MultipartGzBuilder.Build("data.json.gz", "{\"k\":\"v\"}");

            var response = await client.PostAsync("/filetransfer", body);
            var content  = await response.Content.ReadAsStringAsync();

            // Spec: 204 No Content — success carries no response body.
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal(string.Empty, content);
            // The file is still tracked.
            Assert.NotNull(store.Get("data.json.gz"));
        }

        [Fact]
        public async Task Post_BinaryGz_DecompressesCorrectly()
        {
            var raw = new byte[1024];
            new Random(42).NextBytes(raw);

            var (_, client, _, _, _) = BuildTestServer(keepGz: true);
            var body = MultipartGzBuilder.Build("binary.bin.gz", raw);

            var response = await client.PostAsync("/filetransfer", body);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

            string path = Directory.GetFiles(Path.Combine(_tmp.Root, "decompressed")).Single();
            Assert.Equal(raw, File.ReadAllBytes(path));
        }

        // ------------------------------------------------------------------ //
        //  GZ file retention
        // ------------------------------------------------------------------ //

        [Fact]
        public async Task Post_KeepGzFalse_GzFileIsDeletedAfterDecompression()
        {
            var (_, client, _, _, _) = BuildTestServer(keepGz: false);
            var body = MultipartGzBuilder.Build("log.txt.gz", "line1\nline2");

            await client.PostAsync("/filetransfer", body);

            Assert.Empty(Directory.GetFiles(Path.Combine(_tmp.Root, "received")));
        }

        [Fact]
        public async Task Post_KeepGzTrue_GzFileIsRetainedAfterDecompression()
        {
            var (_, client, _, _, _) = BuildTestServer(keepGz: true);
            var body = MultipartGzBuilder.Build("report.xml.gz", "<root/>");

            await client.PostAsync("/filetransfer", body);

            string[] gz = Directory.GetFiles(Path.Combine(_tmp.Root, "received"));
            Assert.Single(gz);
            Assert.EndsWith(".gz", gz[0]);
        }

        // ------------------------------------------------------------------ //
        //  Validation errors
        // ------------------------------------------------------------------ //

        [Fact]
        public async Task Post_JsonContentType_Returns415()
        {
            var (_, client, _, _, _) = BuildTestServer();
            var body = new StringContent("{}", Encoding.UTF8, "application/json");

            var response = await client.PostAsync("/filetransfer", body);

            Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        }

        [Fact]
        public async Task Post_MissingFileField_Returns400()
        {
            var (_, client, _, _, _) = BuildTestServer(formFieldName: "file");

            var wrong = new MultipartFormDataContent();
            wrong.Add(new StringContent("data"), "wrongfield", "test.gz");

            var response = await client.PostAsync("/filetransfer", wrong);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task Post_UnsupportedExtension_Returns400()
        {
            var (_, client, _, _, _) = BuildTestServer();

            var mp = new MultipartFormDataContent();
            var bytes = new ByteArrayContent(Encoding.UTF8.GetBytes("hello"));
            bytes.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            mp.Add(bytes, "file", "document.txt");

            var response = await client.PostAsync("/filetransfer", mp);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            string body = await response.Content.ReadAsStringAsync();
            Assert.Contains("gz", body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("zip", body, StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ //
        //  API-key security
        // ------------------------------------------------------------------ //

        [Fact]
        public async Task Post_CorrectApiKey_Returns204()
        {
            var (_, client, _, _, _) = BuildTestServer(apiKey: "secret-123");
            client.DefaultRequestHeaders.Add("X-Api-Key", "secret-123");

            var response = await client.PostAsync(
                "/filetransfer",
                MultipartGzBuilder.Build("secure.csv.gz", "a,b"));

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        [Fact]
        public async Task Post_WrongApiKey_Returns401()
        {
            var (_, client, _, _, _) = BuildTestServer(apiKey: "secret-123");
            client.DefaultRequestHeaders.Add("X-Api-Key", "bad-key");

            var response = await client.PostAsync(
                "/filetransfer",
                MultipartGzBuilder.Build("secure.csv.gz", "a,b"));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task Post_MissingApiKey_Returns401()
        {
            var (_, client, _, _, _) = BuildTestServer(apiKey: "secret-123");
            // No header

            var response = await client.PostAsync(
                "/filetransfer",
                MultipartGzBuilder.Build("secure.csv.gz", "a,b"));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task Post_ApiKeyDisabled_NoHeaderNeeded()
        {
            var (_, client, _, _, _) = BuildTestServer(apiKey: "");
            // No X-Api-Key header at all

            var response = await client.PostAsync(
                "/filetransfer",
                MultipartGzBuilder.Build("open.csv.gz", "x,y"));

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        // ------------------------------------------------------------------ //
        //  Health endpoint
        // ------------------------------------------------------------------ //

        [Fact]
        public async Task Get_HealthEndpoint_Returns200()
        {
            var (_, client, _, _, _) = BuildTestServer();
            var response = await client.GetAsync("/health");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // ------------------------------------------------------------------ //
        //  Custom form field name
        // ------------------------------------------------------------------ //

        [Fact]
        public async Task Post_CustomFormFieldName_AcceptsFile()
        {
            var (_, client, _, _, _) = BuildTestServer(formFieldName: "payload");
            var body = MultipartGzBuilder.Build("custom.csv.gz", "col1,col2",
                formFieldName: "payload");

            var response = await client.PostAsync("/filetransfer", body);

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        // ------------------------------------------------------------------ //
        //  ZIP files
        // ------------------------------------------------------------------ //

        [Fact]
        public async Task Post_ValidZip_Returns204()
        {
            var (_, client, _, _, _) = BuildTestServer();
            var body = MultipartZipBuilder.Build("archive.zip", "data.csv", "id,name\n1,Alice");

            var response = await client.PostAsync("/filetransfer", body);

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        [Fact]
        public async Task Post_ValidZip_ExtractsEntryToDecompressedFolder()
        {
            var (_, client, _, _, _) = BuildTestServer();
            const string content = "id,name\n1,Alice\n2,Bob";
            var body = MultipartZipBuilder.Build("report.zip", "data.csv", content);

            await client.PostAsync("/filetransfer", body);

            // Zip is extracted into a subfolder named after the archive
            string extractDir = Path.Combine(_tmp.Root, "decompressed", "report");
            Assert.True(Directory.Exists(extractDir));
            string[] files = Directory.GetFiles(extractDir);
            Assert.Single(files);
            Assert.Equal("data.csv", Path.GetFileName(files[0]));
            Assert.Equal(content, File.ReadAllText(files[0], Encoding.UTF8));
        }

        [Fact]
        public async Task Post_ValidZip_MultipleEntries_AllExtracted()
        {
            var (_, client, _, _, _) = BuildTestServer();
            var entries = new Dictionary<string, string>
            {
                ["a.txt"] = "hello",
                ["b.txt"] = "world"
            };
            var body = MultipartZipBuilder.Build("multi.zip", entries);

            await client.PostAsync("/filetransfer", body);

            string extractDir = Path.Combine(_tmp.Root, "decompressed", "multi");
            string[] files = Directory.GetFiles(extractDir).Select(p => Path.GetFileName(p)!).Order().ToArray();
            Assert.Equal(new[] { "a.txt", "b.txt" }, files);
        }

        [Fact]
        public async Task Post_ZipKeepFalse_ZipDeletedAfterExtraction()
        {
            var (_, client, _, _, _) = BuildTestServer(keepGz: false);
            var body = MultipartZipBuilder.Build("cleanup.zip", "f.txt", "content");

            await client.PostAsync("/filetransfer", body);

            Assert.Empty(Directory.GetFiles(Path.Combine(_tmp.Root, "received")));
        }

        [Fact]
        public async Task Post_ZipKeepTrue_ZipRetainedAfterExtraction()
        {
            var (_, client, _, _, _) = BuildTestServer(keepGz: true);
            var body = MultipartZipBuilder.Build("kept.zip", "f.txt", "content");

            await client.PostAsync("/filetransfer", body);

            string[] files = Directory.GetFiles(Path.Combine(_tmp.Root, "received"));
            Assert.Single(files);
            Assert.EndsWith(".zip", files[0]);
        }

        [Fact]
        public async Task Post_ValidZip_Returns204AndTracksRecord()
        {
            var (_, client, _, _, store) = BuildTestServer();
            var body = MultipartZipBuilder.Build("payload.zip", "entry.xml", "<root/>");

            var response = await client.PostAsync("/filetransfer", body);

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.NotNull(store.Get("payload.zip"));
        }

        // ------------------------------------------------------------------ //
        //  ReceiverOptions defaults
        // ------------------------------------------------------------------ //

        [Fact]
        public void ReceiverOptions_Defaults_AreCorrect()
        {
            var opts = new ReceiverOptions();

            Assert.Equal(5100,                 opts.Port);
            Assert.Equal("/filetransfer", opts.ReceiveEndpoint);
            Assert.Equal("file",               opts.FormFieldName);
            Assert.False(opts.KeepGzAfterDecompression);
            Assert.Equal(4L * 1024 * 1024 * 1024, opts.MaxUploadBytes);
            Assert.Equal(string.Empty,          opts.ApiKey);
        }

        // ------------------------------------------------------------------ //
        //  Ping
        // ------------------------------------------------------------------ //

        [Fact]
        public async Task Ping_Returns200()
        {
            var (_, client, _, _, _) = BuildTestServer();
            var response = await client.GetAsync("/filetransfer/ping");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task Ping_ReturnsUtcDateTimeString()
        {
            var before = DateTime.UtcNow;
            var (_, client, _, _, _) = BuildTestServer();
            var response = await client.GetAsync("/filetransfer/ping");
            var after   = DateTime.UtcNow;

            var body = await response.Content.ReadAsStringAsync();

            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", body);

            // Parsed value must be within the test window
            var parsed = DateTime.Parse(body, null, System.Globalization.DateTimeStyles.RoundtripKind);
            Assert.True(parsed >= before.AddSeconds(-1) && parsed <= after.AddSeconds(1));
        }

        // ------------------------------------------------------------------ //
        //  RegisterCallback
        // ------------------------------------------------------------------ //

        [Fact]
        public async Task RegisterCallback_ValidUrl_Returns204()
        {
            var (_, client, _, _, _) = BuildTestServer();
            var response = await client.GetAsync("/filetransfer/registercallback?callbackuri=http://localhost:5200/callback");
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        [Fact]
        public async Task RegisterCallback_ValidUrl_SavesEndpoint()
        {
            var (_, client, callbackConfig, _, _) = BuildTestServer();
            await client.GetAsync("/filetransfer/registercallback?callbackuri=http://localhost:5200/callback");
            Assert.Equal("http://localhost:5200/callback", callbackConfig.CallbackEndpoint);
        }

        [Fact]
        public async Task RegisterCallback_ValidUrl_SignalsTrigger()
        {
            var (_, client, _, trigger, _) = BuildTestServer();

            // Trigger must not be set before the call
            bool signalledBefore = trigger.IsSignalled;

            await client.GetAsync("/filetransfer/registercallback?callbackuri=http://localhost:5200/callback");

            Assert.False(signalledBefore);
            Assert.True(trigger.IsSignalled);
        }

        [Fact]
        public async Task RegisterCallback_InvalidUrl_Returns400()
        {
            var (_, client, _, _, _) = BuildTestServer();
            var response = await client.GetAsync("/filetransfer/registercallback?callbackuri=not-a-valid-url");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task RegisterCallback_InvalidUrl_ReturnsErrorPayload()
        {
            var (_, client, _, _, _) = BuildTestServer();
            var response = await client.GetAsync("/filetransfer/registercallback?callbackuri=not-a-valid-url");
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("callbackEndpointError", doc.RootElement.GetProperty("code").GetString());
        }

        [Fact]
        public async Task RegisterCallback_MissingParam_Returns400()
        {
            var (_, client, _, _, _) = BuildTestServer();
            var response = await client.GetAsync("/filetransfer/registercallback");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task RegisterCallback_MissingParam_DoesNotSaveEndpoint()
        {
            var (_, client, callbackConfig, _, _) = BuildTestServer();
            await client.GetAsync("/filetransfer/registercallback");
            Assert.False(callbackConfig.IsRegistered);
        }

        // ------------------------------------------------------------------ //
        //  UpdateStatus
        // ------------------------------------------------------------------ //

        private static StringContent Json(string json) =>
            new(json, Encoding.UTF8, "application/json");

        [Fact]
        public async Task UpdateStatus_ValidStatus_Returns200()
        {
            var (_, client, _, _, store) = BuildTestServer();
            store.Add(new FileRecord { FileName = "data.csv.gz", Status = FileStatus.FileTransferSuccess });

            var resp = await client.PostAsync("/filetransfer/status",
                Json("""{"fileName":"data.csv.gz","status":"fileValidationError","message":"bad format"}"""));

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        [Fact]
        public async Task UpdateStatus_ValidStatus_UpdatesRecord()
        {
            var (_, client, _, _, store) = BuildTestServer();
            store.Add(new FileRecord { FileName = "data.csv.gz", Status = FileStatus.FileTransferSuccess });

            await client.PostAsync("/filetransfer/status",
                Json("""{"fileName":"data.csv.gz","status":"fileValidationError","message":"bad format"}"""));

            var record = store.Get("data.csv.gz");
            Assert.NotNull(record);
            Assert.Equal(FileStatus.FileValidationError, record.Status);
            Assert.Equal("bad format", record.Message);
        }

        [Fact]
        public async Task UpdateStatus_WithTimestamp_UpdatesRecordTimestamp()
        {
            var (_, client, _, _, store) = BuildTestServer();
            store.Add(new FileRecord { FileName = "data.csv.gz", Status = FileStatus.Unknown });

            await client.PostAsync("/filetransfer/status",
                Json("""{"fileName":"data.csv.gz","timestamp":"2026-06-19T10:00:00Z","status":"fileTransferSuccess","message":"ok"}"""));

            var record = store.Get("data.csv.gz");
            Assert.NotNull(record);
            Assert.Equal("2026-06-19T10:00:00Z", record.Timestamp);
        }

        [Fact]
        public async Task UpdateStatus_UnknownFile_Returns404()
        {
            var (_, client, _, _, _) = BuildTestServer();

            var resp = await client.PostAsync("/filetransfer/status",
                Json("""{"fileName":"ghost.gz","status":"fileTransferSuccess","message":""}"""));

            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }

        [Fact]
        public async Task UpdateStatus_InvalidStatus_Returns400()
        {
            var (_, client, _, _, store) = BuildTestServer();
            store.Add(new FileRecord { FileName = "data.csv.gz", Status = FileStatus.FileTransferSuccess });

            var resp = await client.PostAsync("/filetransfer/status",
                Json("""{"fileName":"data.csv.gz","status":"bogusStatus","message":""}"""));

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }

        [Fact]
        public async Task UpdateStatus_ValidStatus_DoesNotSendCallback_AndKeepsRecord()
        {
            var (_, client, callbackConfig, _, store) = BuildTestServer();
            callbackConfig.CallbackEndpoint = "http://localhost:9999/callback";
            store.Add(new FileRecord { FileName = "data.csv.gz", Status = FileStatus.FileTransferSuccess, ApplicationType = "qcs" });

            var resp = await client.PostAsync("/filetransfer/status",
                Json("""{"fileName":"data.csv.gz","status":"valid","message":"all good"}"""));

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            // 'valid' must NOT trigger a callback ...
            Assert.Empty(_httpFactory.SentRequests);

            // ... and the record is kept with its updated status.
            var record = store.Get("data.csv.gz");
            Assert.NotNull(record);
            Assert.Equal(FileStatus.Valid, record.Status);
        }

        [Fact]
        public async Task UpdateStatus_NonValidStatus_SendsCallbackWithPayload()
        {
            var (_, client, callbackConfig, _, store) = BuildTestServer();
            callbackConfig.CallbackEndpoint = "http://localhost:9999/callback";
            store.Add(new FileRecord { FileName = "data.csv.gz", Status = FileStatus.FileTransferSuccess });

            var resp = await client.PostAsync("/filetransfer/status",
                Json("""{"fileName":"data.csv.gz","status":"fileValidationError","message":"bad format"}"""));

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            // Callback fires for non-'valid' status changes.
            Assert.Single(_httpFactory.SentRequests);
            Assert.Equal("http://localhost:9999/callback", _httpFactory.SentRequests[0].Url);
            Assert.Contains("fileValidationError", _httpFactory.SentRequests[0].Body);
            Assert.Contains("data.csv.gz", _httpFactory.SentRequests[0].Body);

            // Callback delivered successfully (200) -> record removed from store.
            Assert.Null(store.Get("data.csv.gz"));
        }

        [Fact]
        public async Task UpdateStatus_CallbackFails_KeepsRecord()
        {
            var (_, client, callbackConfig, _, store) = BuildTestServer();
            callbackConfig.CallbackEndpoint = "http://localhost:9999/callback";
            _httpFactory.ResponseCode = HttpStatusCode.InternalServerError; // callback fails
            store.Add(new FileRecord { FileName = "data.csv.gz", Status = FileStatus.FileTransferSuccess });

            var resp = await client.PostAsync("/filetransfer/status",
                Json("""{"fileName":"data.csv.gz","status":"fileTransferError","message":"oops"}"""));

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            // Callback was attempted ...
            Assert.Single(_httpFactory.SentRequests);
            // ... but failed, so the record is kept with its updated status.
            var record = store.Get("data.csv.gz");
            Assert.NotNull(record);
            Assert.Equal(FileStatus.FileTransferError, record.Status);
        }

        [Fact]
        public async Task UpdateStatus_NoCallbackRegistered_DoesNotSend_KeepsRecord()
        {
            var (_, client, _, _, store) = BuildTestServer();
            // No callback endpoint registered.
            store.Add(new FileRecord { FileName = "data.csv.gz", Status = FileStatus.FileTransferSuccess });

            var resp = await client.PostAsync("/filetransfer/status",
                Json("""{"fileName":"data.csv.gz","status":"fileTransferError","message":"oops"}"""));

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Empty(_httpFactory.SentRequests);
            // Nothing was sent, so the record stays.
            Assert.NotNull(store.Get("data.csv.gz"));
        }

        [Fact]
        public async Task UpdateStatus_ValidStatusNoCallback_RecordNotRemoved()
        {
            var (_, client, _, _, store) = BuildTestServer();
            // callbackConfig has no endpoint registered
            store.Add(new FileRecord { FileName = "data.csv.gz", Status = FileStatus.FileTransferSuccess });

            var resp = await client.PostAsync("/filetransfer/status",
                Json("""{"fileName":"data.csv.gz","status":"valid","message":"ok"}"""));

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            // Record should still be there since callback was not sent
            var record = store.Get("data.csv.gz");
            Assert.NotNull(record);
            Assert.Equal(FileStatus.Valid, record.Status);
        }

        [Fact]
        public async Task Post_ValidGz_StoresRecordWithUnknownStatus()
        {
            var (_, client, _, _, store) = BuildTestServer();
            var body = MultipartGzBuilder.Build("report.csv.gz", "col1,col2\n1,2");

            await client.PostAsync("/filetransfer", body);

            var record = store.Get("report.csv.gz");
            Assert.NotNull(record);
            // A freshly received file starts as 'unknown' until a validator updates it.
            Assert.Equal(FileStatus.Unknown, record.Status);
        }

        [Fact]
        public async Task Post_ValidGz_StoresMetadataFieldsOnRecord()
        {
            var (_, client, _, _, store) = BuildTestServer();
            var metadata = """
                {"fileName":"report.csv","timestamp":"2026-06-19T10:00:00Z","applicationType":"QCS"}
                """;
            var body = MultipartGzBuilder.Build("report.csv.gz", "col1,col2\n1,2", metadata: metadata);

            await client.PostAsync("/filetransfer", body);

            var record = store.Get("report.csv.gz");
            Assert.NotNull(record);
            Assert.Equal("QCS",                  record.ApplicationType);
            Assert.Equal("2026-06-19T10:00:00Z", record.Timestamp);
        }

        [Fact]
        public async Task Post_ValidGz_NoMetadata_LeavesFieldsEmpty()
        {
            var (_, client, _, _, store) = BuildTestServer();
            var body = MultipartGzBuilder.Build("plain.csv.gz", "a,b\n1,2");

            await client.PostAsync("/filetransfer", body);

            var record = store.Get("plain.csv.gz");
            Assert.NotNull(record);
            Assert.Equal(string.Empty, record.ApplicationType);
            Assert.Equal(string.Empty, record.Timestamp);
        }

        [Theory]
        [InlineData("QCS")]
        [InlineData("DFE")]
        [InlineData("JRM")]
        public async Task Post_ValidApplicationType_Accepted(string appType)
        {
            var (_, client, _, _, store) = BuildTestServer();
            var metadata = $$"""{"applicationType":"{{appType}}"}""";
            var body = MultipartGzBuilder.Build("a.csv.gz", "x", metadata: metadata);

            var response = await client.PostAsync("/filetransfer", body);

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal(appType, store.Get("a.csv.gz")!.ApplicationType);
        }

        [Fact]
        public async Task Post_InvalidApplicationType_Returns400()
        {
            var (_, client, _, _, store) = BuildTestServer();
            var metadata = """{"applicationType":"SomethingElse"}""";
            var body = MultipartGzBuilder.Build("a.csv.gz", "x", metadata: metadata);

            var response = await client.PostAsync("/filetransfer", body);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            // Rejected before storage.
            Assert.Null(store.Get("a.csv.gz"));
        }

        // ------------------------------------------------------------------ //
        //  Duplicate upload — overwrite, latest wins
        // ------------------------------------------------------------------ //

        [Fact]
        public async Task Post_SameFileNameAgain_OverwritesRecord_StatusResetToUnknown()
        {
            var (_, client, _, _, store) = BuildTestServer();

            // First upload -> status 'unknown'.
            await client.PostAsync("/filetransfer", MultipartGzBuilder.Build("dup.csv.gz", "a,b"));
            Assert.Equal(FileStatus.Unknown, store.Get("dup.csv.gz")!.Status);

            // A validator sets a status (no callback registered, so the record is kept).
            await client.PostAsync("/filetransfer/status",
                Json("""{"fileName":"dup.csv.gz","status":"fileValidationError","message":"bad"}"""));
            Assert.Equal(FileStatus.FileValidationError, store.Get("dup.csv.gz")!.Status);

            // Re-uploading the same file name overwrites the record: latest wins,
            // status is reset to 'unknown' (the previous status is discarded).
            await client.PostAsync("/filetransfer", MultipartGzBuilder.Build("dup.csv.gz", "a,b"));
            var record = store.Get("dup.csv.gz");
            Assert.NotNull(record);
            Assert.Equal(FileStatus.Unknown, record.Status);
        }

        // ------------------------------------------------------------------ //
        //  DecompressOnReceive = false (store the archive as-is)
        // ------------------------------------------------------------------ //

        [Fact]
        public async Task Post_DecompressDisabled_StoresGzAsIs_NoDecompression()
        {
            var (_, client, _, _, store) = BuildTestServer(decompressOnReceive: false);
            var body = MultipartGzBuilder.Build("export.csv.gz", "id,name\n1,Alice");

            var response = await client.PostAsync("/filetransfer", body);

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

            // The .gz is kept in the received folder, untouched ...
            string[] received = Directory.GetFiles(Path.Combine(_tmp.Root, "received"));
            Assert.Contains(received, f => Path.GetFileName(f) == "export.csv.gz");

            // ... and nothing is written to the decompressed folder.
            Assert.Empty(Directory.GetFiles(Path.Combine(_tmp.Root, "decompressed")));

            // Still tracked.
            Assert.NotNull(store.Get("export.csv.gz"));
        }

        [Fact]
        public async Task Post_DecompressDisabled_ZipStoredAsIs_NotExtracted()
        {
            var (_, client, _, _, _) = BuildTestServer(decompressOnReceive: false);
            var body = MultipartZipBuilder.Build("archive.zip", "data.csv", "id,name\n1,Alice");

            var response = await client.PostAsync("/filetransfer", body);

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            string[] received = Directory.GetFiles(Path.Combine(_tmp.Root, "received"));
            Assert.Contains(received, f => Path.GetFileName(f) == "archive.zip");
            // No extraction directory was created.
            Assert.Empty(Directory.GetDirectories(Path.Combine(_tmp.Root, "decompressed")));
        }

        // ------------------------------------------------------------------ //
        //  multipart/mixed (spec content type)
        // ------------------------------------------------------------------ //

        [Fact]
        public async Task Post_MultipartMixed_AcceptsFileAndMetadata()
        {
            var (_, client, _, _, store) = BuildTestServer();
            var body = MultipartGzBuilder.BuildMixed("report.csv.gz", "col1,col2\n1,2",
                metadata: """{"applicationType":"QCS","timestamp":"2026-06-19T10:00:00Z"}""");

            var response = await client.PostAsync("/filetransfer", body);

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            var record = store.Get("report.csv.gz");
            Assert.NotNull(record);
            Assert.Equal("QCS",                  record.ApplicationType);
            Assert.Equal("2026-06-19T10:00:00Z", record.Timestamp);
        }

        [Fact]
        public async Task Post_MultipartMixed_DecompressesFileContent()
        {
            var (_, client, _, _, _) = BuildTestServer();
            const string content = "id,name\n1,Alice\n2,Bob";
            var body = MultipartGzBuilder.BuildMixed("export.csv.gz", content);

            await client.PostAsync("/filetransfer", body);

            string[] files = Directory.GetFiles(Path.Combine(_tmp.Root, "decompressed"));
            Assert.Single(files);
            Assert.Equal("export.csv", Path.GetFileName(files[0]));
            Assert.Equal(content, File.ReadAllText(files[0], Encoding.UTF8));
        }

        [Fact]
        public async Task Post_InvalidExtension_StoresFileFormatErrorRecord()
        {
            var (_, client, _, _, store) = BuildTestServer();
            // Build a multipart with a .txt file (invalid extension)
            var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("hello"));
            fileContent.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data")
            {
                Name = "\"file\"", FileName = "\"readme.txt\""
            };
            var multipart = new MultipartFormDataContent();
            multipart.Add(fileContent, "file", "readme.txt");

            await client.PostAsync("/filetransfer", multipart);

            var record = store.Get("readme.txt");
            Assert.NotNull(record);
            Assert.Equal(FileStatus.FileFormatError, record.Status);
        }
    }
}

