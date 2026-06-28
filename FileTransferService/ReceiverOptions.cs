namespace FileTransfer
{
    /// <summary>
    /// Configuration for the inbound HTTP receiver (Kestrel).
    /// Bound from appsettings.json section "Receiver".
    /// </summary>
    public class ReceiverOptions
    {
        // ------------------------------------------------------------------ //
        //  HTTP listener
        // ------------------------------------------------------------------ //

        /// <summary>Port Kestrel listens on for incoming file uploads.</summary>
        public int Port { get; set; } = 5100;

        /// <summary>
        /// Endpoint path that accepts multipart/form-data POSTs.
        /// Default: /filetransfer
        /// </summary>
        public string ReceiveEndpoint { get; set; } = "/filetransfer";

        /// <summary>
        /// Name of the multipart form field that carries the .gz file.
        /// </summary>
        public string FormFieldName { get; set; } = "file";

        // ------------------------------------------------------------------ //
        //  Storage
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Folder where received (still compressed) .gz files are stored.
        /// </summary>
        public string ReceivedFolder { get; set; } = @"C:\HotFolder\Received";

        /// <summary>
        /// Folder where decompressed files are placed after extraction.
        /// </summary>
        public string DecompressedFolder { get; set; } = @"C:\HotFolder\Decompressed";

        /// <summary>
        /// When true (default) the received .gz file is decompressed to
        /// <see cref="DecompressedFolder"/>. Set to false to store the raw
        /// file without decompressing it.
        /// </summary>
        public bool DecompressOnReceive { get; set; } = true;

        /// <summary>
        /// Keep the original .gz file after decompression.
        /// When false the .gz is deleted once extraction succeeds.
        /// </summary>
        public bool KeepGzAfterDecompression { get; set; } = false;

        // ------------------------------------------------------------------ //
        //  Limits
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Path of the file used to persist the registered callback endpoint.
        /// Relative paths are resolved from the working directory.
        /// </summary>
        public string CallbackEndpointFile { get; set; } = "Iot_FileTransfer_LastCallbackUrl.Txt";

        /// <summary>
        /// Name of the JSON file used to persist file records so they survive a
        /// service restart. Stored inside <see cref="ReceivedFolder"/>.
        /// </summary>
        public string FileRecordStoreFile { get; set; } = "Iot_FileTransfer_Records.json";

        /// <summary>
        /// How long a file record is kept before being purged by the background
        /// cleanup, in hours (default 168 = 7 days). This is the safety net for
        /// records that never receive a status update. Set to 0 or negative to
        /// disable cleanup (records are kept indefinitely).
        /// </summary>
        public int RecordRetentionHours { get; set; } = 168;

        /// <summary>How often the retention cleanup runs, in minutes (default 60).</summary>
        public int RecordCleanupIntervalMinutes { get; set; } = 60;

        /// <summary>
        /// Maximum allowed upload size in bytes. The spec requires support for
        /// files of at least 4 GB, which is the default.
        /// </summary>
        public long MaxUploadBytes { get; set; } = 4L * 1024 * 1024 * 1024;

        // ------------------------------------------------------------------ //
        //  Security — simple shared-secret header (optional)
        // ------------------------------------------------------------------ //

        /// <summary>
        /// When non-empty, every incoming request must supply this value
        /// in the X-Api-Key header. Leave empty to disable the check.
        /// </summary>
        public string ApiKey { get; set; } = string.Empty;
    }
}

