using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace FileTransfer.Tests
{
    /// <summary>
    /// Tests that verify HotFolderService correctly calls IFileTransferHandler
    /// for pre-existing files, using a Moq mock for the handler.
    /// </summary>
    public class HotFolderServiceTests : IDisposable
    {
        private readonly TempFolder _tmp = new();
        public void Dispose() => _tmp.Dispose();

        private HotFolderService BuildService(
            Mock<IFileTransferHandler> mockHandler,
            string filter = "*.*")
        {
            var opts = new HotFolderOptions
            {
                SourceFolder              = _tmp.Source,
                ArchiveFolder             = _tmp.Archive,
                ErrorFolder               = _tmp.ErrorDir,
                FileFilter                = filter,
                DeleteSourceAfterTransfer = false, // handler is mocked; don't touch files
                ErrorFolderEnabled        = true,
                ArchiveEnabled            = false,
                Api = new ApiOptions
                {
                    BaseUrl        = "https://test.example.com",
                    UploadEndpoint = "/api/files/upload",
                    MaxConcurrentUploads = 2
                }
            };

            var trigger = new StartTrigger();
            trigger.Signal();
            return new HotFolderService(
                NullLogger<HotFolderService>.Instance,
                Options.Create(opts),
                mockHandler.Object,
                trigger,
                new StubCallbackConfiguration());
        }

        // ------------------------------------------------------------------ //
        //  Startup scan
        // ------------------------------------------------------------------ //

        [Fact]
        public async Task ExecuteAsync_PreExistingFiles_CallsTransferForEachFile()
        {
            // Arrange
            _tmp.CreateSourceFile("a.pdf");
            _tmp.CreateSourceFile("b.pdf");
            _tmp.CreateSourceFile("c.pdf");

            var mockHandler = new Mock<IFileTransferHandler>();
            mockHandler
                .Setup(h => h.TransferAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var service = BuildService(mockHandler);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            // Act
            await service.StartAsync(cts.Token);
            await Task.Delay(300); // let fire-and-forget tasks complete
            await service.StopAsync(CancellationToken.None);

            // Assert: TransferAsync called once per file
            mockHandler.Verify(
                h => h.TransferAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Exactly(3));
        }

        [Fact]
        public async Task ExecuteAsync_EmptySourceFolder_CallsTransferZeroTimes()
        {
            var mockHandler = new Mock<IFileTransferHandler>();

            var service = BuildService(mockHandler);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

            await service.StartAsync(cts.Token);
            await Task.Delay(200);
            await service.StopAsync(CancellationToken.None);

            mockHandler.Verify(
                h => h.TransferAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task ExecuteAsync_FileFilter_OnlyMatchingFilesAreTransferred()
        {
            // Arrange: mix of .pdf and .txt — filter = "*.pdf"
            _tmp.CreateSourceFile("doc.pdf");
            _tmp.CreateSourceFile("note.txt");
            _tmp.CreateSourceFile("report.pdf");

            var mockHandler = new Mock<IFileTransferHandler>();
            mockHandler
                .Setup(h => h.TransferAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var service = BuildService(mockHandler, filter: "*.pdf");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            await service.StartAsync(cts.Token);
            await Task.Delay(300);
            await service.StopAsync(CancellationToken.None);

            // Only the 2 PDF files should be transferred
            mockHandler.Verify(
                h => h.TransferAsync(It.Is<string>(p => p.EndsWith(".pdf")), It.IsAny<CancellationToken>()),
                Times.Exactly(2));

            mockHandler.Verify(
                h => h.TransferAsync(It.Is<string>(p => p.EndsWith(".txt")), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task ExecuteAsync_MultiPatternFilter_OnlyMatchingTypesAreTransferred()
        {
            // Arrange: filter accepts several extensions "*.pdf *.jpg *.7z".
            _tmp.CreateSourceFile("alpha.pdf");
            _tmp.CreateSourceFile("beta.jpg");
            _tmp.CreateSourceFile("gamma.7z");
            _tmp.CreateSourceFile("delta.txt");   // ignored
            _tmp.CreateSourceFile("epsilon.csv");  // ignored

            var mockHandler = new Mock<IFileTransferHandler>();
            mockHandler
                .Setup(h => h.TransferAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var service = BuildService(mockHandler, filter: "*.pdf *.jpg *.7z");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            // Act
            await service.StartAsync(cts.Token);
            await Task.Delay(300);
            await service.StopAsync(CancellationToken.None);

            // Assert: the 3 matching files transferred, total transfers == 3
            mockHandler.Verify(
                h => h.TransferAsync(
                    It.Is<string>(p => p.EndsWith(".pdf") || p.EndsWith(".jpg") || p.EndsWith(".7z")),
                    It.IsAny<CancellationToken>()),
                Times.Exactly(3));

            mockHandler.Verify(
                h => h.TransferAsync(
                    It.Is<string>(p => p.EndsWith(".txt") || p.EndsWith(".csv")),
                    It.IsAny<CancellationToken>()),
                Times.Never);

            mockHandler.Verify(
                h => h.TransferAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Exactly(3));
        }

        [Theory]
        [InlineData("*.pdf,*.jpg")]   // comma separated
        [InlineData("*.pdf;*.jpg")]   // semicolon separated
        [InlineData("*.pdf|*.jpg")]   // pipe separated
        [InlineData("*.pdf, *.jpg")]  // separator + extra whitespace
        public async Task ExecuteAsync_MultiPatternFilter_SupportsCommonSeparators(string filter)
        {
            // Arrange
            _tmp.CreateSourceFile("doc.pdf");
            _tmp.CreateSourceFile("pic.jpg");
            _tmp.CreateSourceFile("note.txt"); // ignored

            var mockHandler = new Mock<IFileTransferHandler>();
            mockHandler
                .Setup(h => h.TransferAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var service = BuildService(mockHandler, filter: filter);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            // Act
            await service.StartAsync(cts.Token);
            await Task.Delay(300);
            await service.StopAsync(CancellationToken.None);

            // Assert: both pdf and jpg transferred, txt ignored
            mockHandler.Verify(
                h => h.TransferAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Exactly(2));
            mockHandler.Verify(
                h => h.TransferAsync(It.Is<string>(p => p.EndsWith(".txt")), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task ExecuteAsync_OverlappingPatterns_TransfersEachFileOnce()
        {
            // Arrange: a file matches both patterns ("*.pdf" and "*.*").
            // It must still be transferred only once (patterns are de-duplicated).
            _tmp.CreateSourceFile("report.pdf");

            var mockHandler = new Mock<IFileTransferHandler>();
            mockHandler
                .Setup(h => h.TransferAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var service = BuildService(mockHandler, filter: "*.pdf *.*");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            // Act
            await service.StartAsync(cts.Token);
            await Task.Delay(300);
            await service.StopAsync(CancellationToken.None);

            // Assert: transferred exactly once despite matching two patterns
            mockHandler.Verify(
                h => h.TransferAsync(
                    It.Is<string>(p => p.EndsWith("report.pdf")), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        // ------------------------------------------------------------------ //
        //  Directory creation
        // ------------------------------------------------------------------ //

        [Fact]
        public async Task ExecuteAsync_CreatesSourceFolderIfMissing()
        {
            string newSource = Path.Combine(_tmp.Root, "new_source");
            Assert.False(Directory.Exists(newSource));

            var opts = new HotFolderOptions
            {
                SourceFolder  = newSource,
                ArchiveFolder = _tmp.Archive,
                ErrorFolder   = _tmp.ErrorDir,
                Api           = new ApiOptions { MaxConcurrentUploads = 1 }
            };

            var mockHandler = new Mock<IFileTransferHandler>();
            var trigger2 = new StartTrigger();
            trigger2.Signal();
            var service = new HotFolderService(
                NullLogger<HotFolderService>.Instance,
                Options.Create(opts),
                mockHandler.Object,
                trigger2,
                new StubCallbackConfiguration());

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await service.StartAsync(cts.Token);
            // Give ExecuteAsync time to run past the trigger and create directories
            await Task.Delay(200);
            await service.StopAsync(CancellationToken.None);

            Assert.True(Directory.Exists(newSource));
        }

        [Fact]
        public async Task ExecuteAsync_CreatesAFolderForEachApplicationType()
        {
            var opts = new HotFolderOptions
            {
                SourceFolder           = _tmp.Source,
                ArchiveFolder          = _tmp.Archive,
                ErrorFolder            = _tmp.ErrorDir,
                WatchSubdirectories    = true,
                ApplicationTypeFolders = new() { ["qcs"] = "QCS", ["dfe"] = "DFE", ["jrm"] = "JRM" },
                Api                    = new ApiOptions { MaxConcurrentUploads = 1 }
            };

            var mockHandler = new Mock<IFileTransferHandler>();
            var trigger = new StartTrigger();
            trigger.Signal();
            var service = new HotFolderService(
                NullLogger<HotFolderService>.Instance,
                Options.Create(opts),
                mockHandler.Object,
                trigger,
                new StubCallbackConfiguration());

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await service.StartAsync(cts.Token);
            await Task.Delay(200);
            await service.StopAsync(CancellationToken.None);

            Assert.True(Directory.Exists(Path.Combine(_tmp.Source, "qcs")));
            Assert.True(Directory.Exists(Path.Combine(_tmp.Source, "dfe")));
            Assert.True(Directory.Exists(Path.Combine(_tmp.Source, "jrm")));
        }

        // ------------------------------------------------------------------ //
        //  Graceful stop
        // ------------------------------------------------------------------ //

        [Fact]
        public async Task StopAsync_DoesNotThrow()
        {
            var mockHandler = new Mock<IFileTransferHandler>();
            var service = BuildService(mockHandler);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await service.StartAsync(cts.Token);

            var ex = await Record.ExceptionAsync(() => service.StopAsync(CancellationToken.None));
            Assert.Null(ex);
        }
    }
}

