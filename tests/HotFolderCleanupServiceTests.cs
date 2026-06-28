using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FileTransfer.Tests
{
    public class HotFolderCleanupServiceTests : IDisposable
    {
        private readonly TempFolder _tmp = new();
        public void Dispose() => _tmp.Dispose();

        private static string MakeFile(string dir, string name, TimeSpan age)
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
            return path;
        }

        private async Task RunSweep(IOptions<HotFolderOptions> opts)
        {
            var svc = new HotFolderCleanupService(
                NullLogger<HotFolderCleanupService>.Instance,
                opts,
                new StubFileRecordStore());
            using var cts = new CancellationTokenSource();
            await svc.StartAsync(cts.Token);   // sweeps immediately
            await Task.Delay(150);
            cts.Cancel();
            await svc.StopAsync(CancellationToken.None);
        }

        [Fact]
        public async Task Cleanup_RemovesOldFiles_FromAllFolders_KeepsRecent()
        {
            var opts = OptionsFactory.Create(_tmp);
            opts.Value.RetentionHours = 1;

            // Old files (5h) in each folder ...
            string oldSrc = MakeFile(_tmp.Source,   "old.gz", TimeSpan.FromHours(5));
            string oldErr = MakeFile(_tmp.ErrorDir, "old.gz", TimeSpan.FromHours(5));
            string oldArc = MakeFile(_tmp.Archive,  "old.gz", TimeSpan.FromHours(5));
            // ... and recent files (just now).
            string newSrc = MakeFile(_tmp.Source,   "new.gz", TimeSpan.Zero);
            string newErr = MakeFile(_tmp.ErrorDir, "new.gz", TimeSpan.Zero);
            string newArc = MakeFile(_tmp.Archive,  "new.gz", TimeSpan.Zero);

            await RunSweep(opts);

            Assert.False(File.Exists(oldSrc));
            Assert.False(File.Exists(oldErr));
            Assert.False(File.Exists(oldArc));
            Assert.True(File.Exists(newSrc));
            Assert.True(File.Exists(newErr));
            Assert.True(File.Exists(newArc));
        }

        [Fact]
        public async Task Cleanup_RecursesIntoApplicationTypeSubfolders()
        {
            var opts = OptionsFactory.Create(_tmp);
            opts.Value.RetentionHours = 1;

            string oldInSub = MakeFile(Path.Combine(_tmp.Source, "qcs"), "old.gz", TimeSpan.FromHours(5));
            string newInSub = MakeFile(Path.Combine(_tmp.Source, "qcs"), "new.gz", TimeSpan.Zero);

            await RunSweep(opts);

            Assert.False(File.Exists(oldInSub));
            Assert.True(File.Exists(newInSub));
            // The sub-folder itself is not deleted.
            Assert.True(Directory.Exists(Path.Combine(_tmp.Source, "qcs")));
        }

        [Fact]
        public async Task Cleanup_Disabled_WhenRetentionNotPositive()
        {
            var opts = OptionsFactory.Create(_tmp);
            opts.Value.RetentionHours = 0;

            string old = MakeFile(_tmp.ErrorDir, "old.gz", TimeSpan.FromDays(30));

            await RunSweep(opts);

            Assert.True(File.Exists(old));   // cleanup disabled -> kept
        }
    }
}
