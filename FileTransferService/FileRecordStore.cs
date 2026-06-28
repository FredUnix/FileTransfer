using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileTransfer
{
    public interface IFileRecordStore
    {
        void Add(FileRecord record);
        FileRecord? Get(string fileName);
        IReadOnlyList<FileRecord> GetAll();
        void Remove(string fileName);

        /// <summary>Removes records received strictly before <paramref name="cutoff"/>.
        /// Returns the number removed.</summary>
        int RemoveOlderThan(System.DateTimeOffset cutoff);
    }

    /// <summary>
    /// Thread-safe store of <see cref="FileRecord"/> entries, persisted to a JSON
    /// file inside <see cref="ReceiverOptions.ReceivedFolder"/> so records survive
    /// a service restart. The in-memory dictionary is the source of truth; every
    /// mutation is flushed to disk.
    /// </summary>
    public sealed class FileRecordStore : IFileRecordStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private readonly ConcurrentDictionary<string, FileRecord> _records =
            new(System.StringComparer.OrdinalIgnoreCase);

        private readonly string _filePath;
        private readonly ILogger<FileRecordStore> _logger;
        private readonly object _fileLock = new();

        public FileRecordStore(
            IOptions<ReceiverOptions> options,
            ILogger<FileRecordStore> logger)
        {
            _filePath = Path.Combine(options.Value.ReceivedFolder, options.Value.FileRecordStoreFile);
            _logger   = logger;

            Load();
        }

        public void Add(FileRecord record)
        {
            // Stamp the server receive time once, when the record is first stored.
            if (record.ReceivedAt == default)
                record.ReceivedAt = System.DateTimeOffset.UtcNow;

            _records[record.FileName] = record;
            Save();
        }

        public FileRecord? Get(string fileName) =>
            _records.TryGetValue(fileName, out var r) ? r : null;

        public IReadOnlyList<FileRecord> GetAll() => [.. _records.Values];

        public void Remove(string fileName)
        {
            if (_records.TryRemove(fileName, out _))
                Save();
        }

        public int RemoveOlderThan(System.DateTimeOffset cutoff)
        {
            // Skip records with no receive time (legacy/default) — never auto-prune those.
            var stale = _records
                .Where(kv => kv.Value.ReceivedAt > System.DateTimeOffset.MinValue &&
                             kv.Value.ReceivedAt < cutoff)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in stale)
                _records.TryRemove(key, out _);

            if (stale.Count > 0)
            {
                Save();
                _logger.LogInformation("Pruned {Count} record(s) older than {Cutoff:o}.", stale.Count, cutoff);
            }
            return stale.Count;
        }

        // ------------------------------------------------------------------ //
        //  Persistence
        // ------------------------------------------------------------------ //

        private void Load()
        {
            try
            {
                if (!File.Exists(_filePath))
                    return;

                string json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json))
                    return;

                var records = JsonSerializer.Deserialize<List<FileRecord>>(json, JsonOptions);
                if (records is null)
                    return;

                foreach (var record in records.Where(r => !string.IsNullOrWhiteSpace(r.FileName)))
                    _records[record.FileName] = record;

                _logger.LogInformation(
                    "Loaded {Count} file record(s) from {Path}.", _records.Count, _filePath);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Failed to load file records from {Path}.", _filePath);
            }
        }

        private void Save()
        {
            lock (_fileLock)
            {
                try
                {
                    string? dir = Path.GetDirectoryName(_filePath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);

                    string json = JsonSerializer.Serialize(_records.Values.ToList(), JsonOptions);
                    File.WriteAllText(_filePath, json);
                }
                catch (System.Exception ex)
                {
                    _logger.LogError(ex, "Failed to persist file records to {Path}.", _filePath);
                }
            }
        }
    }
}
