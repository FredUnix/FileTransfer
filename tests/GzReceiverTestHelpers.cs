using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using RichardSzalay.MockHttp;

namespace FileTransfer.Tests
{
    /// <summary>
    /// Builds multipart/form-data HttpContent containing a .gz file,
    /// ready to POST to the receiver endpoint.
    /// </summary>
    public static class MultipartGzBuilder
    {
        /// <summary>
        /// Creates a MultipartFormDataContent that contains a gzip-compressed
        /// version of <paramref name="innerContent"/>.
        /// </summary>
        /// <param name="gzFileName">File name of the .gz part, e.g. "data.csv.gz"</param>
        /// <param name="innerContent">Raw bytes to compress</param>
        /// <param name="formFieldName">Multipart field name (default: "file")</param>
        public static MultipartFormDataContent Build(
            string gzFileName,
            byte[] innerContent,
            string formFieldName = "file",
            string? metadata     = null)
        {
            byte[] compressed = Compress(innerContent);

            var fileContent = new ByteArrayContent(compressed);
            fileContent.Headers.ContentType        = new MediaTypeHeaderValue("application/gzip");
            fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
            {
                Name     = $"\"{formFieldName}\"",
                FileName = $"\"{gzFileName}\""
            };

            var multipart = new MultipartFormDataContent();
            multipart.Add(fileContent, formFieldName, gzFileName);
            if (metadata is not null)
                multipart.Add(new StringContent(metadata), "metadata");
            return multipart;
        }

        /// <summary>Convenience overload that compresses a UTF-8 string.</summary>
        public static MultipartFormDataContent Build(
            string gzFileName,
            string innerText,
            string formFieldName = "file",
            string? metadata     = null) =>
            Build(gzFileName, Encoding.UTF8.GetBytes(innerText), formFieldName, metadata);

        /// <summary>
        /// Builds a <b>multipart/mixed</b> body matching the spec: a 'file' section
        /// (application/octet-stream) and an optional 'metadata' section
        /// (application/json). Same wire format as form-data, only the top-level
        /// media type differs.
        /// </summary>
        public static MultipartFormDataContent BuildMixed(
            string gzFileName,
            string innerText,
            string? metadata     = null,
            string formFieldName = "file")
        {
            byte[] compressed = Compress(Encoding.UTF8.GetBytes(innerText));

            var fileContent = new ByteArrayContent(compressed);
            fileContent.Headers.ContentType        = new MediaTypeHeaderValue("application/octet-stream");
            fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
            {
                Name     = $"\"{formFieldName}\"",
                FileName = $"\"{gzFileName}\""
            };

            var multipart = new MultipartFormDataContent();
            multipart.Add(fileContent, formFieldName, gzFileName);
            if (metadata is not null)
                multipart.Add(new StringContent(metadata, Encoding.UTF8, "application/json"), "metadata");

            // Re-tag the top-level type as multipart/mixed, keeping the boundary.
            multipart.Headers.ContentType!.MediaType = "multipart/mixed";
            return multipart;
        }

        // ------------------------------------------------------------------

        private static byte[] Compress(byte[] data)
        {
            using var ms     = new MemoryStream();
            using var gz     = new GZipStream(ms, CompressionLevel.Fastest);
            gz.Write(data, 0, data.Length);
            gz.Close();
            return ms.ToArray();
        }

        /// <summary>
        /// Decompresses a .gz byte array back to raw bytes (used in assertions).
        /// </summary>
        public static byte[] Decompress(byte[] compressed)
        {
            using var input  = new MemoryStream(compressed);
            using var gz     = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gz.CopyTo(output);
            return output.ToArray();
        }

        /// <summary>Decompresses the content of a file path to a string.</summary>
        public static string ReadDecompressedText(string filePath)
        {
            using var fs  = File.OpenRead(filePath);
            using var gz  = new GZipStream(fs, CompressionMode.Decompress);
            using var sr  = new StreamReader(gz, Encoding.UTF8);
            return sr.ReadToEnd();
        }

        /// <summary>Reads a plain (already-decompressed) file as UTF-8 text.</summary>
        public static string ReadText(string filePath) =>
            File.ReadAllText(filePath, Encoding.UTF8);
    }

    /// <summary>
    /// Builds multipart/form-data HttpContent containing a .zip file.
    /// </summary>
    public static class MultipartZipBuilder
    {
        /// <summary>
        /// Creates a MultipartFormDataContent with a zip archive that contains
        /// <paramref name="entries"/> (name -> UTF-8 text content).
        /// </summary>
        public static MultipartFormDataContent Build(
            string zipFileName,
            Dictionary<string, string> entries,
            string formFieldName = "file")
        {
            byte[] zipBytes = CreateZip(entries);

            var fileContent = new ByteArrayContent(zipBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

            var multipart = new MultipartFormDataContent();
            multipart.Add(fileContent, formFieldName, zipFileName);
            return multipart;
        }

        /// <summary>Convenience overload for a single-entry zip.</summary>
        public static MultipartFormDataContent Build(
            string zipFileName,
            string entryName,
            string entryContent,
            string formFieldName = "file") =>
            Build(zipFileName, new Dictionary<string, string> { [entryName] = entryContent }, formFieldName);

        public static byte[] CreateZip(Dictionary<string, string> entries)
        {
            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (name, content) in entries)
                {
                    var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                    using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                    writer.Write(content);
                }
            return ms.ToArray();
        }
    }

    /// <summary>
    /// In-memory stub for <see cref="ICallbackConfiguration"/> — no file I/O.
    /// </summary>
    public sealed class StubCallbackConfiguration : ICallbackConfiguration
    {
        public string CallbackEndpoint { get; set; } = string.Empty;
        public bool IsRegistered => !string.IsNullOrWhiteSpace(CallbackEndpoint);
    }

    /// <summary>
    /// In-memory stub for <see cref="IFileRecordStore"/>.
    /// </summary>
    public sealed class StubFileRecordStore : IFileRecordStore
    {
        private readonly ConcurrentDictionary<string, FileRecord> _records =
            new(StringComparer.OrdinalIgnoreCase);

        public void Add(FileRecord record)      => _records[record.FileName] = record;
        public FileRecord? Get(string fileName) => _records.TryGetValue(fileName, out var r) ? r : null;
        public IReadOnlyList<FileRecord> GetAll() => [.. _records.Values];
        public void Remove(string fileName)     => _records.TryRemove(fileName, out _);

        public int RemoveOlderThan(System.DateTimeOffset cutoff)
        {
            var stale = _records
                .Where(kv => kv.Value.ReceivedAt > System.DateTimeOffset.MinValue &&
                             kv.Value.ReceivedAt < cutoff)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in stale) _records.TryRemove(key, out _);
            return stale.Count;
        }
    }

    /// <summary>
    /// Stub <see cref="IHttpClientFactory"/> that returns an <see cref="HttpClient"/> backed by
    /// a <see cref="MockHttpMessageHandler"/> which accepts any request with HTTP 200.
    /// Each callback request (target URI + serialized body) is captured in
    /// <see cref="SentRequests"/> so tests can assert what was posted.
    /// </summary>
    public sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly MockHttpMessageHandler _handler = new();

        public List<(string Url, string Body)> SentRequests { get; } = new();

        /// <summary>Status code returned to every callback request (default 200).</summary>
        public HttpStatusCode ResponseCode { get; set; } = HttpStatusCode.OK;

        public StubHttpClientFactory()
        {
            _handler.Fallback.Respond(req =>
            {
                string body = req.Content is null
                    ? string.Empty
                    : req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                SentRequests.Add((req.RequestUri?.ToString() ?? string.Empty, body));
                return new HttpResponseMessage(ResponseCode);
            });
        }

        public HttpClient CreateClient(string name = "") => _handler.ToHttpClient();
    }

    /// <summary>
    /// Builds <see cref="ReceiverOptions"/> wired to a <see cref="TempFolder"/>.
    /// </summary>
    public static class ReceiverOptionsFactory
    {
        public static Microsoft.Extensions.Options.IOptions<ReceiverOptions> Create(
            TempFolder tmp,
            string endpoint              = "/filetransfer",
            string formFieldName         = "file",
            bool   keepGz                = false,
            long   maxUploadBytes        = 50 * 1024 * 1024,
            string apiKey                = "",
            bool   decompressOnReceive   = true)
        {
            // Ensure sub-folders exist inside the shared TempFolder
            string received     = Path.Combine(tmp.Root, "received");
            string decompressed = Path.Combine(tmp.Root, "decompressed");
            Directory.CreateDirectory(received);
            Directory.CreateDirectory(decompressed);

            var opts = new ReceiverOptions
            {
                Port                    = 0,   // not used in unit tests (no real listener)
                ReceiveEndpoint         = endpoint,
                FormFieldName           = formFieldName,
                ReceivedFolder          = received,
                DecompressedFolder      = decompressed,
                DecompressOnReceive     = decompressOnReceive,
                KeepGzAfterDecompression = keepGz,
                MaxUploadBytes          = maxUploadBytes,
                ApiKey                  = apiKey
            };

            return Microsoft.Extensions.Options.Options.Create(opts);
        }
    }
}

