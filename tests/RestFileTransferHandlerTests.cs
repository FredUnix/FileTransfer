using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using RichardSzalay.MockHttp;
using Xunit;

namespace FileTransfer.Tests
{
    public class RestFileTransferHandlerTests : IDisposable
    {
        private readonly TempFolder _tmp = new();

        public void Dispose() => _tmp.Dispose();

        // ------------------------------------------------------------------ //
        //  Happy path
        // ------------------------------------------------------------------ //

        [Fact]
        public async Task Transfer_SuccessResponse_DeletesSourceFile()
        {
            // Arrange
            string filePath = _tmp.CreateSourceFile("report.pdf");
            var opts = OptionsFactory.Create(_tmp, deleteSource: true);
            var (handler, _) = HandlerFactory.Create(opts);

            // Act
            await handler.TransferAsync(filePath, CancellationToken.None);

            // Assert
            Assert.False(File.Exists(filePath), "Source file should have been deleted after success.");
        }

        [Fact]
        public async Task Transfer_SuccessResponse_SendsPostToConfiguredEndpoint()
        {
            // Arrange
            string filePath  = _tmp.CreateSourceFile("data.csv");
            var opts         = OptionsFactory.Create(_tmp, uploadEndpoint: "/api/v2/ingest");

            var mockHttp = new MockHttpMessageHandler();
            var request  = mockHttp
                .Expect(HttpMethod.Post, "https://test.example.com/api/v2/ingest")
                .Respond(HttpStatusCode.OK, "application/json", "{}");

            var httpClient = mockHttp.ToHttpClient();
            httpClient.BaseAddress = new Uri("https://test.example.com");

            var handler = new RestFileTransferHandler(
                NullLogger<RestFileTransferHandler>.Instance, opts, httpClient, new StubCallbackConfiguration());

            // Act
            await handler.TransferAsync(filePath, CancellationToken.None);

            // Assert
            mockHttp.VerifyNoOutstandingExpectation();
        }

        [Fact]
        public async Task Transfer_SuccessResponse_UsesConfiguredFormFieldName()
        {
            // Arrange
            string filePath = _tmp.CreateSourceFile("image.png");
            string? capturedFieldName = null;

            var mockHttp = new MockHttpMessageHandler();
            mockHttp
                .When(HttpMethod.Post, "*")
                .Respond(req =>
                {
                    if (req.Content is MultipartFormDataContent multipart)
                        foreach (var part in multipart)
                            if (part.Headers.ContentDisposition?.Name is not null)
                                capturedFieldName = part.Headers.ContentDisposition.Name!.Trim('"');

                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{}")
                    });
                });

            var opts = OptionsFactory.Create(_tmp, formField: "document");
            var httpClient = mockHttp.ToHttpClient();
            httpClient.BaseAddress = new Uri("https://test.example.com");

            var handler = new RestFileTransferHandler(
                NullLogger<RestFileTransferHandler>.Instance, opts, httpClient, new StubCallbackConfiguration());

            // Act
            await handler.TransferAsync(filePath, CancellationToken.None);

            // Assert
            Assert.Equal("document", capturedFieldName);
        }

        // ------------------------------------------------------------------ //
        //  Source-file handling after success
        // ------------------------------------------------------------------ //

        [Fact]
        public async Task Transfer_ArchiveEnabled_MovesSourceToArchiveFolder()
        {
            // Arrange
            string filePath = _tmp.CreateSourceFile("invoice.xml");
            var opts = OptionsFactory.Create(_tmp, archiveEnabled: true, deleteSource: false);
            var (handler, _) = HandlerFactory.Create(opts);

            // Act
            await handler.TransferAsync(filePath, CancellationToken.None);

            // Assert
            Assert.False(File.Exists(filePath), "Source should have been moved.");
            Assert.Single(Directory.GetFiles(_tmp.Archive));
        }

        [Fact]
        public async Task Transfer_ArchiveDisabledDeleteEnabled_DeletesSource()
        {
            string filePath = _tmp.CreateSourceFile("order.json");
            var opts = OptionsFactory.Create(_tmp, archiveEnabled: false, deleteSource: true);
            var (handler, _) = HandlerFactory.Create(opts);

            await handler.TransferAsync(filePath, CancellationToken.None);

            Assert.False(File.Exists(filePath));
            Assert.Empty(Directory.GetFiles(_tmp.Archive));
        }

        [Fact]
        public async Task Transfer_ArchiveDisabledDeleteDisabled_LeavesSourceInPlace()
        {
            string filePath = _tmp.CreateSourceFile("keep.txt");
            var opts = OptionsFactory.Create(_tmp, archiveEnabled: false, deleteSource: false);
            var (handler, _) = HandlerFactory.Create(opts);

            await handler.TransferAsync(filePath, CancellationToken.None);

            Assert.True(File.Exists(filePath), "Source should remain when both archive and delete are disabled.");
        }

        // ------------------------------------------------------------------ //
        //  Retry logic
        // ------------------------------------------------------------------ //

        [Fact]
        public async Task Transfer_ServerFailsThenSucceeds_RetriesAndSucceeds()
        {
            // Arrange: 1 failure then success, MaxRetries = 2
            string filePath = _tmp.CreateSourceFile("retry.pdf");
            var opts = OptionsFactory.Create(_tmp, maxRetries: 2, retryDelay: 0);
            var (handler, _) = HandlerFactory.CreateWithFailsThenSuccess(opts, failCount: 1);

            // Act
            await handler.TransferAsync(filePath, CancellationToken.None);

            // Assert: success path executed â†’ source deleted
            Assert.False(File.Exists(filePath));
        }

        [Fact]
        public async Task Transfer_AllAttemptsFailWith500_MovesToErrorFolder()
        {
            // Arrange: MaxRetries = 2, server always returns 500
            string filePath = _tmp.CreateSourceFile("broken.pdf");
            var opts = OptionsFactory.Create(_tmp, maxRetries: 2, retryDelay: 0, errorEnabled: true);
            var (handler, _) = HandlerFactory.Create(opts, HttpStatusCode.InternalServerError);

            // Act
            await handler.TransferAsync(filePath, CancellationToken.None);

            // Assert: error folder has the file
            Assert.Empty(Directory.GetFiles(_tmp.Source));
            Assert.Single(Directory.GetFiles(_tmp.ErrorDir));
        }

        [Fact]
        public async Task Transfer_MaxRetriesZero_RetriesUntilSuccess()
        {
            // Arrange: MaxRetries = 0 means retry forever. Fail 5 times (more than
            // any finite retry count would allow) then succeed.
            string filePath = _tmp.CreateSourceFile("infinite.pdf");
            var opts = OptionsFactory.Create(_tmp, maxRetries: 0, retryDelay: 0, errorEnabled: true);
            var (handler, _) = HandlerFactory.CreateWithFailsThenSuccess(opts, failCount: 5);

            // Act
            await handler.TransferAsync(filePath, CancellationToken.None);

            // Assert: it kept retrying, eventually succeeded → source deleted,
            // nothing landed in the error folder.
            Assert.False(File.Exists(filePath));
            Assert.Empty(Directory.GetFiles(_tmp.ErrorDir));
        }

        [Fact]
        public async Task Transfer_MaxRetriesZero_CancellationStopsRetrying()
        {
            // Arrange: MaxRetries = 0 (retry forever), server always fails.
            // A cancelled token must break the otherwise-infinite loop.
            string filePath = _tmp.CreateSourceFile("cancel.pdf");
            var opts = OptionsFactory.Create(_tmp, maxRetries: 0, retryDelay: 1, errorEnabled: true);
            var (handler, _) = HandlerFactory.Create(opts, HttpStatusCode.InternalServerError);

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(200));

            // Act / Assert: completes (throws OperationCanceledException) rather than hanging.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => handler.TransferAsync(filePath, cts.Token));
        }

        [Fact]
        public async Task Transfer_AllAttemptsFailWith500_ErrorFolderDisabled_LeavesSourceInPlace()
        {
            string filePath = _tmp.CreateSourceFile("no_error_folder.pdf");
            var opts = OptionsFactory.Create(_tmp, maxRetries: 1, retryDelay: 0, errorEnabled: false);
            var (handler, _) = HandlerFactory.Create(opts, HttpStatusCode.InternalServerError);

            await handler.TransferAsync(filePath, CancellationToken.None);

            // File stays in source because error folder is disabled
            Assert.True(File.Exists(filePath));
        }

        // ------------------------------------------------------------------ //
        //  Missing / vanished file
        // ------------------------------------------------------------------ //

        [Fact]
        public async Task Transfer_FileDoesNotExist_DoesNotThrow()
        {
            var opts = OptionsFactory.Create(_tmp, lockTimeout: 1, lockPoll: 50);
            var (handler, _) = HandlerFactory.Create(opts);

            // Should complete without throwing
            await handler.TransferAsync(
                Path.Combine(_tmp.Source, "ghost.pdf"),
                CancellationToken.None);
        }

        // ------------------------------------------------------------------ //
        //  Cancellation
        // ------------------------------------------------------------------ //

        [Fact]
        public async Task Transfer_CancelledToken_DoesNotUpload()
        {
            string filePath = _tmp.CreateSourceFile("cancel.pdf");
            var opts = OptionsFactory.Create(_tmp);

            var mockHttp = new MockHttpMessageHandler();
            int postCount = 0;
            mockHttp.When(HttpMethod.Post, "*").Respond(_ =>
            {
                postCount++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            });

            var httpClient = mockHttp.ToHttpClient();
            httpClient.BaseAddress = new Uri("https://test.example.com");

            var handler = new RestFileTransferHandler(
                NullLogger<RestFileTransferHandler>.Instance, opts, httpClient, new StubCallbackConfiguration());

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await handler.TransferAsync(filePath, cts.Token);

            Assert.Equal(0, postCount);
        }
    }
}


