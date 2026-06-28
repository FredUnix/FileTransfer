using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using RichardSzalay.MockHttp;
using Xunit;

namespace FileTransfer.Tests
{
    public class MimeTypeAndConfigTests : IDisposable
    {
        private readonly TempFolder _tmp = new();
        public void Dispose() => _tmp.Dispose();

        // ------------------------------------------------------------------ //
        //  MIME type detection
        // ------------------------------------------------------------------ //

        [Theory]
        [InlineData("file.pdf",  "application/pdf")]
        [InlineData("file.xml",  "application/xml")]
        [InlineData("file.json", "application/json")]
        [InlineData("file.csv",  "text/csv")]
        [InlineData("file.txt",  "text/plain")]
        [InlineData("file.png",  "image/png")]
        [InlineData("file.jpg",  "image/jpeg")]
        [InlineData("file.jpeg", "image/jpeg")]
        [InlineData("file.zip",  "application/zip")]
        [InlineData("file.bin",  "application/octet-stream")]
        [InlineData("file.DAT",  "application/octet-stream")] // uppercase extension
        public async Task Transfer_SetsCorrectContentTypeForExtension(
            string fileName, string expectedMime)
        {
            // Arrange
            string filePath = _tmp.CreateSourceFile(fileName);
            string? capturedMime = null;

            var mockHttp = new MockHttpMessageHandler();
            mockHttp.When(HttpMethod.Post, "*").Respond(req =>
            {
                if (req.Content is MultipartFormDataContent multipart)
                    foreach (var part in multipart)
                        if (part.Headers.ContentDisposition?.FileName is not null)
                            capturedMime = part.Headers.ContentType?.MediaType;

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}")
                });
            });

            var opts = OptionsFactory.Create(_tmp);
            var httpClient = mockHttp.ToHttpClient();
            httpClient.BaseAddress = new Uri("https://test.example.com");

            var handler = new RestFileTransferHandler(
                NullLogger<RestFileTransferHandler>.Instance, opts, httpClient, new StubCallbackConfiguration());

            // Act
            await handler.TransferAsync(filePath, CancellationToken.None);

            // Assert
            Assert.Equal(expectedMime, capturedMime);
        }

        // ------------------------------------------------------------------ //
        //  Extra form fields
        // ------------------------------------------------------------------ //

        [Fact]
        public async Task Transfer_ExtraFormFields_AreSentAlongWithFile()
        {
            string filePath = _tmp.CreateSourceFile("meta.pdf");

            var opts = OptionsFactory.Create(_tmp);
            opts.Value.Api.ExtraFormFields["source"]      = "hotfolder";
            opts.Value.Api.ExtraFormFields["environment"] = "test";

            var capturedFields = new System.Collections.Generic.Dictionary<string, string>();

            var mockHttp = new MockHttpMessageHandler();
            mockHttp.When(HttpMethod.Post, "*").Respond(async req =>
            {
                if (req.Content is MultipartFormDataContent multipart)
                    foreach (var part in multipart)
                    {
                        var name = part.Headers.ContentDisposition?.Name?.Trim('"');
                        if (name is not null && part.Headers.ContentDisposition?.FileName is null)
                            capturedFields[name] = await part.ReadAsStringAsync();
                    }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}")
                };
            });

            var httpClient = mockHttp.ToHttpClient();
            httpClient.BaseAddress = new Uri("https://test.example.com");

            var handler = new RestFileTransferHandler(
                NullLogger<RestFileTransferHandler>.Instance, opts, httpClient, new StubCallbackConfiguration());

            await handler.TransferAsync(filePath, CancellationToken.None);

            Assert.Equal("hotfolder", capturedFields["source"]);
            Assert.Equal("test",      capturedFields["environment"]);
        }

        // ------------------------------------------------------------------ //
        //  Application-type routing (sub-folder per application)
        // ------------------------------------------------------------------ //

        [Theory]
        [InlineData("qcs", "QCS")]
        [InlineData("DFE", "DFE")]   // folder match is case-insensitive
        [InlineData("jrm", "JRM")]
        public async Task Transfer_FileInMappedSubfolder_SendsMetadataWithApplicationType(
            string subFolder, string expectedAppType)
        {
            string filePath = _tmp.CreateSourceFile(subFolder, "report.pdf", "data");

            var opts = OptionsFactory.Create(_tmp);
            opts.Value.WatchSubdirectories = true;
            opts.Value.ApplicationTypeFolders["qcs"] = "QCS";
            opts.Value.ApplicationTypeFolders["dfe"] = "DFE";
            opts.Value.ApplicationTypeFolders["jrm"] = "JRM";

            string? metadataJson = null;
            var mockHttp = new MockHttpMessageHandler();
            mockHttp.When(HttpMethod.Post, "*").Respond(async req =>
            {
                if (req.Content is MultipartFormDataContent multipart)
                    foreach (var part in multipart)
                        if (part.Headers.ContentDisposition?.Name?.Trim('"') == "metadata")
                            metadataJson = await part.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            });

            var httpClient = mockHttp.ToHttpClient();
            httpClient.BaseAddress = new Uri("https://test.example.com");
            var handler = new RestFileTransferHandler(
                NullLogger<RestFileTransferHandler>.Instance, opts, httpClient, new StubCallbackConfiguration());

            await handler.TransferAsync(filePath, CancellationToken.None);

            Assert.NotNull(metadataJson);
            using var doc = JsonDocument.Parse(metadataJson!);
            Assert.Equal(expectedAppType, doc.RootElement.GetProperty("applicationType").GetString());
            Assert.Equal("report.pdf",    doc.RootElement.GetProperty("fileName").GetString());
            Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("timestamp").GetString()));
        }

        [Theory]
        [InlineData("report.pdf")]
        [InlineData("photo.jpg")]
        [InlineData("archive.zip")]
        [InlineData("data.7z")]
        public async Task Transfer_QcsFolderFiles_SentToCallbackRefWithQcsMetadata(string fileName)
        {
            // File of each type sitting in incoming/QCS.
            string filePath = _tmp.CreateSourceFile("QCS", fileName, "payload");

            const string callbackRef = "http://callback.example.com/cb";
            var opts = OptionsFactory.Create(_tmp, uploadEndpoint: "/api/files/upload");
            opts.Value.WatchSubdirectories = true;
            opts.Value.ApplicationTypeFolders["qcs"] = "QCS";   // matched case-insensitively
            var callbackConfig = new StubCallbackConfiguration { CallbackEndpoint = callbackRef };

            string? requestUri   = null;
            string? metadataJson = null;
            var mockHttp = new MockHttpMessageHandler();
            mockHttp.When(HttpMethod.Post, "*").Respond(async req =>
            {
                requestUri = req.RequestUri?.ToString();
                if (req.Content is MultipartFormDataContent multipart)
                    foreach (var part in multipart)
                        if (part.Headers.ContentDisposition?.Name?.Trim('"') == "metadata")
                            metadataJson = await part.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            });

            var httpClient = mockHttp.ToHttpClient();
            httpClient.BaseAddress = new Uri("https://unused.example.com");
            var handler = new RestFileTransferHandler(
                NullLogger<RestFileTransferHandler>.Instance, opts, httpClient, callbackConfig);

            await handler.TransferAsync(filePath, CancellationToken.None);

            // Sent to the registered callback URL (callbackref) + upload endpoint.
            Assert.Equal($"{callbackRef}/api/files/upload", requestUri);

            // Metadata carries the QCS applicationType and the right file name.
            Assert.NotNull(metadataJson);
            using var doc = JsonDocument.Parse(metadataJson!);
            Assert.Equal("QCS",    doc.RootElement.GetProperty("applicationType").GetString());
            Assert.Equal(fileName, doc.RootElement.GetProperty("fileName").GetString());
            Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("timestamp").GetString()));

            // Source consumed after a successful upload.
            Assert.False(File.Exists(filePath));
        }

        [Fact]
        public async Task Transfer_FileNotUnderMappedSubfolder_MovedToErrorAndNotUploaded()
        {
            // File sits directly in the source root, not under a mapped sub-folder.
            string filePath = _tmp.CreateSourceFile("orphan.pdf", "data");

            var opts = OptionsFactory.Create(_tmp, errorEnabled: true);
            opts.Value.ApplicationTypeFolders["qcs"] = "QCS";

            bool uploaded = false;
            var mockHttp = new MockHttpMessageHandler();
            mockHttp.When(HttpMethod.Post, "*").Respond(_ =>
            {
                uploaded = true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            });
            var httpClient = mockHttp.ToHttpClient();
            httpClient.BaseAddress = new Uri("https://test.example.com");
            var handler = new RestFileTransferHandler(
                NullLogger<RestFileTransferHandler>.Instance, opts, httpClient, new StubCallbackConfiguration());

            await handler.TransferAsync(filePath, CancellationToken.None);

            Assert.False(uploaded);                                   // never sent
            Assert.False(File.Exists(filePath));                     // moved out of source
            Assert.Single(Directory.GetFiles(_tmp.ErrorDir));        // landed in error
        }

        [Fact]
        public async Task Transfer_PdfDirectlyInIncoming_Errors_NotUploaded()
        {
            // A .pdf dropped straight into incoming/ (no application sub-folder).
            string filePath = _tmp.CreateSourceFile("report.pdf", "payload");

            var opts = OptionsFactory.Create(_tmp, errorEnabled: true);
            opts.Value.WatchSubdirectories = true;
            opts.Value.ApplicationTypeFolders["qcs"] = "QCS";
            opts.Value.ApplicationTypeFolders["dfe"] = "DFE";
            opts.Value.ApplicationTypeFolders["jrm"] = "JRM";

            bool uploaded = false;
            var mockHttp = new MockHttpMessageHandler();
            mockHttp.When(HttpMethod.Post, "*").Respond(_ =>
            {
                uploaded = true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            });
            var httpClient = mockHttp.ToHttpClient();
            httpClient.BaseAddress = new Uri("https://test.example.com");
            var handler = new RestFileTransferHandler(
                NullLogger<RestFileTransferHandler>.Instance, opts, httpClient, new StubCallbackConfiguration());

            await handler.TransferAsync(filePath, CancellationToken.None);

            // applicationType cannot be resolved -> error: moved to error folder, never sent.
            Assert.False(uploaded);
            Assert.False(File.Exists(filePath));
            var errored = Directory.GetFiles(_tmp.ErrorDir);
            Assert.Single(errored);
            Assert.StartsWith("report", Path.GetFileName(errored[0]));
        }

        [Fact]
        public async Task Transfer_NoRoutingConfigured_SendsNoMetadata()
        {
            string filePath = _tmp.CreateSourceFile("plain.pdf", "data");
            var opts = OptionsFactory.Create(_tmp);   // ApplicationTypeFolders empty

            bool hasMetadata = false;
            var mockHttp = new MockHttpMessageHandler();
            mockHttp.When(HttpMethod.Post, "*").Respond(req =>
            {
                if (req.Content is MultipartFormDataContent multipart)
                    hasMetadata = multipart.Any(p => p.Headers.ContentDisposition?.Name?.Trim('"') == "metadata");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            });
            var httpClient = mockHttp.ToHttpClient();
            httpClient.BaseAddress = new Uri("https://test.example.com");
            var handler = new RestFileTransferHandler(
                NullLogger<RestFileTransferHandler>.Instance, opts, httpClient, new StubCallbackConfiguration());

            await handler.TransferAsync(filePath, CancellationToken.None);

            Assert.False(hasMetadata);
        }

        // ------------------------------------------------------------------ //
        //  HotFolderOptions defaults
        // ------------------------------------------------------------------ //

        [Fact]
        public void HotFolderOptions_Defaults_AreCorrect()
        {
            var opts = new HotFolderOptions();

            Assert.Equal("*.*",   opts.FileFilter);
            Assert.False(opts.WatchSubdirectories);
            Assert.True(opts.DeleteSourceAfterTransfer);
            Assert.False(opts.ArchiveEnabled);
            Assert.True(opts.ErrorFolderEnabled);
            Assert.Equal(3,   opts.MaxRetries);
            Assert.Equal(5,   opts.RetryDelaySeconds);
            Assert.Equal(30,  opts.LockTimeoutSeconds);
            Assert.Equal(500, opts.LockPollIntervalMs);
        }

        [Fact]
        public void ApiOptions_Defaults_AreCorrect()
        {
            var api = new ApiOptions();

            Assert.Equal("/api/files/upload", api.UploadEndpoint);
            Assert.Equal("file",              api.FormFieldName);
            Assert.Equal(120,                 api.TimeoutSeconds);
            Assert.Equal(4,                   api.MaxConcurrentUploads);
            Assert.Empty(api.ExtraFormFields);
        }

        // ------------------------------------------------------------------ //
        //  Server 4xx response
        // ------------------------------------------------------------------ //

        [Theory]
        [InlineData(HttpStatusCode.BadRequest)]
        [InlineData(HttpStatusCode.Unauthorized)]
        [InlineData(HttpStatusCode.NotFound)]
        [InlineData(HttpStatusCode.UnprocessableEntity)]
        public async Task Transfer_Server4xx_MovesToErrorFolder(HttpStatusCode code)
        {
            string filePath = _tmp.CreateSourceFile("bad_request.pdf");
            var opts = OptionsFactory.Create(_tmp, maxRetries: 1, retryDelay: 0, errorEnabled: true);
            var (handler, _) = HandlerFactory.Create(opts, code);

            await handler.TransferAsync(filePath, CancellationToken.None);

            Assert.Single(Directory.GetFiles(_tmp.ErrorDir));
        }
    }
}


