using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FileTransfer.Tests
{
    public class FileRecordStoreTests : IDisposable
    {
        private readonly TempFolder _tmp = new();
        public void Dispose() => _tmp.Dispose();

        private IOptions<ReceiverOptions> Opts() => ReceiverOptionsFactory.Create(_tmp);
        private FileRecordStore NewStore(IOptions<ReceiverOptions> opts) =>
            new(opts, NullLogger<FileRecordStore>.Instance);

        // ------------------------------------------------------------------ //
        //  ReceivedAt stamping
        // ------------------------------------------------------------------ //

        [Fact]
        public void Add_StampsReceivedAt_WhenNotSet()
        {
            var before = DateTimeOffset.UtcNow.AddSeconds(-1);
            var store  = NewStore(Opts());

            store.Add(new FileRecord { FileName = "a.csv.gz", Status = FileStatus.Unknown });

            Assert.True(store.Get("a.csv.gz")!.ReceivedAt >= before);
        }

        [Fact]
        public void Add_PreservesExistingReceivedAt()
        {
            var ts    = DateTimeOffset.UtcNow.AddDays(-3);
            var store = NewStore(Opts());

            store.Add(new FileRecord { FileName = "a.csv.gz", ReceivedAt = ts });

            Assert.Equal(ts, store.Get("a.csv.gz")!.ReceivedAt);
        }

        // ------------------------------------------------------------------ //
        //  RemoveOlderThan (retention)
        // ------------------------------------------------------------------ //

        [Fact]
        public void RemoveOlderThan_RemovesStale_KeepsRecent()
        {
            var store = NewStore(Opts());
            store.Add(new FileRecord { FileName = "old.csv.gz", ReceivedAt = DateTimeOffset.UtcNow.AddHours(-200) });
            store.Add(new FileRecord { FileName = "new.csv.gz", ReceivedAt = DateTimeOffset.UtcNow.AddHours(-1) });

            int removed = store.RemoveOlderThan(DateTimeOffset.UtcNow.AddHours(-168));

            Assert.Equal(1, removed);
            Assert.Null(store.Get("old.csv.gz"));
            Assert.NotNull(store.Get("new.csv.gz"));
        }

        [Fact]
        public void RemoveOlderThan_PersistsAcrossReload()
        {
            var opts  = Opts();
            var store = NewStore(opts);
            store.Add(new FileRecord { FileName = "old.csv.gz", ReceivedAt = DateTimeOffset.UtcNow.AddHours(-200) });
            store.Add(new FileRecord { FileName = "new.csv.gz", ReceivedAt = DateTimeOffset.UtcNow.AddHours(-1) });

            store.RemoveOlderThan(DateTimeOffset.UtcNow.AddHours(-168));

            // Reload from disk — the prune must have been persisted.
            var reloaded = NewStore(opts);
            Assert.Null(reloaded.Get("old.csv.gz"));
            Assert.NotNull(reloaded.Get("new.csv.gz"));
        }

        [Fact]
        public void RemoveOlderThan_SkipsLegacyRecordsWithoutReceivedAt()
        {
            var opts = Opts();
            // A legacy records file written before receivedAt existed.
            string path = Path.Combine(opts.Value.ReceivedFolder, opts.Value.FileRecordStoreFile);
            File.WriteAllText(path, """[{"fileName":"legacy.csv.gz","status":"unknown"}]""");

            var store   = NewStore(opts);
            int removed = store.RemoveOlderThan(DateTimeOffset.UtcNow);

            Assert.Equal(0, removed);
            Assert.NotNull(store.Get("legacy.csv.gz"));
        }

        // ------------------------------------------------------------------ //
        //  RecordCleanupService
        // ------------------------------------------------------------------ //

        [Fact]
        public async Task CleanupService_PrunesStaleRecordsOnStart()
        {
            var opts  = Options.Create(new ReceiverOptions
            {
                RecordRetentionHours         = 1,
                RecordCleanupIntervalMinutes = 60
            });
            var store = new StubFileRecordStore();
            store.Add(new FileRecord { FileName = "old.csv.gz", ReceivedAt = DateTimeOffset.UtcNow.AddHours(-5) });
            store.Add(new FileRecord { FileName = "new.csv.gz", ReceivedAt = DateTimeOffset.UtcNow });

            var svc = new RecordCleanupService(NullLogger<RecordCleanupService>.Instance, opts, store);

            using var cts = new CancellationTokenSource();
            await svc.StartAsync(cts.Token);   // sweeps immediately
            await Task.Delay(100);
            cts.Cancel();
            await svc.StopAsync(CancellationToken.None);

            Assert.Null(store.Get("old.csv.gz"));
            Assert.NotNull(store.Get("new.csv.gz"));
        }

        [Fact]
        public async Task CleanupService_Disabled_WhenRetentionNotPositive()
        {
            var opts  = Options.Create(new ReceiverOptions { RecordRetentionHours = 0 });
            var store = new StubFileRecordStore();
            store.Add(new FileRecord { FileName = "old.csv.gz", ReceivedAt = DateTimeOffset.UtcNow.AddYears(-1) });

            var svc = new RecordCleanupService(NullLogger<RecordCleanupService>.Instance, opts, store);

            using var cts = new CancellationTokenSource();
            await svc.StartAsync(cts.Token);
            await Task.Delay(100);
            cts.Cancel();
            await svc.StopAsync(CancellationToken.None);

            // Cleanup disabled -> old record kept.
            Assert.NotNull(store.Get("old.csv.gz"));
        }
    }
}
