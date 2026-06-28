using System.Collections.Generic;

namespace FileTransfer
{
    /// <summary>
    /// All settings for the hot-folder transfer service.
    /// Bound from appsettings.json section "HotFolder".
    /// </summary>
    public class HotFolderOptions
    {
        // ------------------------------------------------------------------ //
        //  Folders
        // ------------------------------------------------------------------ //

        public string SourceFolder      { get; set; } = @"C:\HotFolder\Incoming";
        public string ArchiveFolder     { get; set; } = @"C:\HotFolder\Archive";
        public string ErrorFolder       { get; set; } = @"C:\HotFolder\Error";

        // ------------------------------------------------------------------ //
        //  Watcher
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Glob pattern(s) of files to watch. A single pattern ("*.pdf") or
        /// several separated by space, comma, semicolon or pipe
        /// (e.g. "*.pdf *.jpg *.7z") which are matched with OR semantics.
        /// </summary>
        public string FileFilter            { get; set; } = "*.*";
        public bool   WatchSubdirectories   { get; set; } = false;

        // ------------------------------------------------------------------ //
        //  Application-type routing
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Maps an immediate sub-folder of <see cref="SourceFolder"/> to the
        /// <c>applicationType</c> sent in the upload metadata, e.g.
        /// <c>{ "qcs": "QCS", "dfe": "DFE", "jrm": "JRM" }</c>. Each producing
        /// application drops its files in its own sub-folder. Folder names are
        /// matched case-insensitively; requires <see cref="WatchSubdirectories"/>.
        /// When this map is non-empty, a file that does not sit under a mapped
        /// sub-folder is moved to the error folder. Leave empty to disable
        /// metadata/applicationType tagging entirely.
        /// </summary>
        public Dictionary<string, string> ApplicationTypeFolders { get; set; } = new();

        // ------------------------------------------------------------------ //
        //  Source file handling after upload
        // ------------------------------------------------------------------ //

        public bool DeleteSourceAfterTransfer { get; set; } = true;
        public bool ArchiveEnabled            { get; set; } = false;
        public bool ErrorFolderEnabled        { get; set; } = true;

        // ------------------------------------------------------------------ //
        //  Retry
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Number of upload attempts before a file is moved to the error folder.
        /// Set to 0 (or a negative value) to retry indefinitely — the file is
        /// retried forever (with <see cref="RetryDelaySeconds"/> between attempts)
        /// until it is sent successfully or the service shuts down.
        /// </summary>
        public int MaxRetries        { get; set; } = 3;
        public int RetryDelaySeconds { get; set; } = 5;

        // ------------------------------------------------------------------ //
        //  Lock detection
        // ------------------------------------------------------------------ //

        public int LockTimeoutSeconds { get; set; } = 30;
        public int LockPollIntervalMs { get; set; } = 500;

        // ------------------------------------------------------------------ //
        //  Folder cleanup (retention)
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Files older than this (by last-write time) are purged from the source,
        /// error and archive folders by the background cleanup, in hours
        /// (default 168 = 7 days). Set to 0 or negative to disable cleanup.
        /// </summary>
        public int RetentionHours { get; set; } = 168;

        /// <summary>How often the folder cleanup runs, in minutes (default 60).</summary>
        public int CleanupIntervalMinutes { get; set; } = 60;

        // ------------------------------------------------------------------ //
        //  REST API
        // ------------------------------------------------------------------ //

        public ApiOptions Api { get; set; } = new();
    }

    /// <summary>
    /// REST API configuration (base URL, endpoint, HTTP client tuning).
    /// </summary>
    public class ApiOptions
    {
        /// <summary>Base address of the REST API, e.g. "https://api.example.com"</summary>
        public string BaseUrl { get; set; } = "https://api.example.com";

        /// <summary>
        /// Path of the upload endpoint relative to BaseUrl.
        /// e.g. "/api/files/upload"
        /// </summary>
        public string UploadEndpoint { get; set; } = "/api/files/upload";

        /// <summary>
        /// Name of the multipart form field that carries the file.
        /// Most APIs expect "file"; adjust to match your server.
        /// </summary>
        public string FormFieldName { get; set; } = "file";

        /// <summary>
        /// Optional static key/value pairs added as extra form fields
        /// alongside the file (e.g. metadata, source system identifier).
        /// </summary>
        public Dictionary<string, string> ExtraFormFields { get; set; } = new();

        // ------------------------------------------------------------------ //
        //  HTTP client tuning
        // ------------------------------------------------------------------ //

        /// <summary>Total request timeout in seconds (0 = no timeout).</summary>
        public int TimeoutSeconds { get; set; } = 120;

        /// <summary>Maximum number of simultaneous uploads.</summary>
        public int MaxConcurrentUploads { get; set; } = 4;
    }
}

