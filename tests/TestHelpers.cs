using System;
using System.IO;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RichardSzalay.MockHttp;

namespace FileTransfer.Tests
{
    // ------------------------------------------------------------------ //
    //  TempFolder — creates an isolated temp directory per test and
    //  cleans it up automatically (IDisposable).
    // ------------------------------------------------------------------ //

    public sealed class TempFolder : IDisposable
    {
        public string Root       { get; }
        public string Source     { get; }
        public string Archive    { get; }
        public string ErrorDir   { get; }

        public TempFolder()
        {
            Root     = Path.Combine(Path.GetTempPath(), "hft_test_" + Guid.NewGuid().ToString("N"));
            Source   = Path.Combine(Root, "incoming");
            Archive  = Path.Combine(Root, "archive");
            ErrorDir = Path.Combine(Root, "error");

            Directory.CreateDirectory(Source);
            Directory.CreateDirectory(Archive);
            Directory.CreateDirectory(ErrorDir);
        }

        /// <summary>Creates a file with the given content in the Source folder.</summary>
        public string CreateSourceFile(string name, string content = "hello world")
        {
            string path = Path.Combine(Source, name);
            File.WriteAllText(path, content);
            return path;
        }

        /// <summary>Creates a file inside a sub-folder of the Source folder.</summary>
        public string CreateSourceFile(string subFolder, string name, string content)
        {
            string dir  = Path.Combine(Source, subFolder);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch { /* best effort */ }
        }
    }

    // ------------------------------------------------------------------ //
    //  OptionsFactory — builds HotFolderOptions wired to a TempFolder.
    // ------------------------------------------------------------------ //

    public static class OptionsFactory
    {
        public static IOptions<HotFolderOptions> Create(
            TempFolder tmp,
            bool deleteSource     = true,
            bool archiveEnabled   = false,
            bool errorEnabled     = true,
            int  maxRetries       = 1,
            int  retryDelay       = 0,
            int  lockTimeout      = 5,
            int  lockPoll         = 100,
            string uploadEndpoint = "/api/files/upload",
            string formField      = "file")
        {
            var opts = new HotFolderOptions
            {
                SourceFolder              = tmp.Source,
                ArchiveFolder             = tmp.Archive,
                ErrorFolder               = tmp.ErrorDir,
                DeleteSourceAfterTransfer = deleteSource,
                ArchiveEnabled            = archiveEnabled,
                ErrorFolderEnabled        = errorEnabled,
                MaxRetries                = maxRetries,
                RetryDelaySeconds         = retryDelay,
                LockTimeoutSeconds        = lockTimeout,
                LockPollIntervalMs        = lockPoll,
                Api = new ApiOptions
                {
                    BaseUrl        = "https://test.example.com",
                    UploadEndpoint = uploadEndpoint,
                    FormFieldName  = formField
                }
            };
            return Options.Create(opts);
        }
    }

    // ------------------------------------------------------------------ //
    //  HandlerFactory — assembles a RestFileTransferHandler with a
    //  mocked HttpMessageHandler (RichardSzalay.MockHttp).
    // ------------------------------------------------------------------ //

    public static class HandlerFactory
    {
        public static (RestFileTransferHandler handler, MockHttpMessageHandler mockHttp)
            Create(IOptions<HotFolderOptions> options, HttpStatusCode responseCode = HttpStatusCode.OK)
        {
            var mockHttp = new MockHttpMessageHandler();
            mockHttp
                .When(HttpMethod.Post, "*")
                .Respond(responseCode, "application/json", "{}");

            var httpClient = mockHttp.ToHttpClient();
            httpClient.BaseAddress = new Uri(options.Value.Api.BaseUrl);

            var handler = new RestFileTransferHandler(
                NullLogger<RestFileTransferHandler>.Instance,
                options,
                httpClient, new StubCallbackConfiguration());

            return (handler, mockHttp);
        }

        /// <summary>Variant that throws on the first N calls then succeeds.</summary>
        public static (RestFileTransferHandler handler, MockHttpMessageHandler mockHttp)
            CreateWithFailsThenSuccess(IOptions<HotFolderOptions> options, int failCount)
        {
            var mockHttp = new MockHttpMessageHandler();
            int calls    = 0;

            mockHttp
                .When(HttpMethod.Post, "*")
                .Respond(_ =>
                {
                    calls++;
                    var code = calls <= failCount
                        ? HttpStatusCode.InternalServerError
                        : HttpStatusCode.OK;
                    return new HttpResponseMessage(code)
                    {
                        Content = new StringContent("{}")
                    };
                });

            var httpClient = mockHttp.ToHttpClient();
            httpClient.BaseAddress = new Uri(options.Value.Api.BaseUrl);

            var handler = new RestFileTransferHandler(
                NullLogger<RestFileTransferHandler>.Instance,
                options,
                httpClient, new StubCallbackConfiguration());

            return (handler, mockHttp);
        }
    }
}


