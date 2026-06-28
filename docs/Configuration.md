# Configuration

[[_TOC_]]

All settings are strongly typed and bound from `appsettings.json`. Defaults live
in the option classes; `appsettings.json` overrides them. Any value can also be
overridden by environment variables using the `__` separator (e.g.
`Receiver__Port=5100`) or command-line arguments.

> **Cross-platform note:** folder defaults are Windows paths (`C:\HotFolder\...`).
> Override them when running on macOS/Linux.

## `HotFolder` (sender)

| Key | Default | Description |
|-----|---------|-------------|
| `SourceFolder` | `C:\HotFolder\Incoming` | Folder watched for outbound files |
| `ArchiveFolder` | `C:\HotFolder\Archive` | Destination after a successful upload (when `ArchiveEnabled`) |
| `ErrorFolder` | `C:\HotFolder\Error` | Destination for files that fail to upload |
| `FileFilter` | `*.*` | One or more glob patterns; multiple may be separated by space, comma, semicolon or pipe (e.g. `*.pdf *.jpg *.7z`) |
| `WatchSubdirectories` | `false` | Recurse into subfolders (required for `ApplicationTypeFolders`) |
| `ApplicationTypeFolders` | `{}` | Maps an immediate sub-folder of `SourceFolder` to the `applicationType` sent in the upload metadata; each sub-folder is auto-created on startup (see [Sender](/Sender-Hot-Folder)) |
| `DeleteSourceAfterTransfer` | `true` | Delete the source after a successful upload (ignored when `ArchiveEnabled`) |
| `ArchiveEnabled` | `false` | Move to `ArchiveFolder` instead of deleting |
| `ErrorFolderEnabled` | `true` | Move failed files to `ErrorFolder` |
| `MaxRetries` | `3` | Upload attempts before failing. **`0` (or negative) = retry forever** until success or shutdown |
| `RetryDelaySeconds` | `5` | Delay between attempts |
| `LockTimeoutSeconds` | `30` | How long to wait for the writer to release the file lock |
| `LockPollIntervalMs` | `500` | Poll interval while waiting for the lock |
| `RetentionHours` | `168` (7 days) | Files older than this (by last-write time) are purged from the source, error and archive folders; `0`/negative disables cleanup |
| `CleanupIntervalMinutes` | `60` | How often the folder cleanup runs |

### `HotFolder:Api`

| Key | Default | Description |
|-----|---------|-------------|
| `BaseUrl` | `https://api.example.com` | Base address of the upstream REST API |
| `UploadEndpoint` | `/api/files/upload` | Path appended to the upload base |
| `FormFieldName` | `file` | Multipart field name for the file |
| `ExtraFormFields` | `{}` | Static fields added to every upload (e.g. `source`, `environment`) |
| `TimeoutSeconds` | `120` | HTTP client timeout |
| `MaxConcurrentUploads` | `4` | Concurrency limit for uploads |

> When a callback is registered, the upload target becomes
> `{callbackUri}{UploadEndpoint}`; otherwise it is `{BaseUrl}{UploadEndpoint}`.

## `Receiver`

| Key | Default | Description |
|-----|---------|-------------|
| `Port` | `5100` | Port Kestrel listens on |
| `ReceiveEndpoint` | `/filetransfer` | Path that accepts file uploads |
| `FormFieldName` | `file` | Multipart field name for the file |
| `ReceivedFolder` | `C:\HotFolder\Received` | Where received archives are saved |
| `DecompressedFolder` | `C:\HotFolder\Decompressed` | Where decompressed output is written |
| `DecompressOnReceive` | `true` | Decompress on receipt; `false` stores the archive as-is |
| `KeepGzAfterDecompression` | `false` | Keep the archive after decompression |
| `CallbackEndpointFile` | `Iot_FileTransfer_LastCallbackUrl.Txt` | File (in `ReceivedFolder`) persisting the registered callback URL |
| `FileRecordStoreFile` | `Iot_FileTransfer_Records.json` | File (in `ReceivedFolder`) persisting file records |
| `RecordRetentionHours` | `168` (7 days) | Records older than this are purged by the background cleanup; `0`/negative disables cleanup (see [Status Lifecycle](/Status-Lifecycle)) |
| `RecordCleanupIntervalMinutes` | `60` | How often the retention sweep runs |
| `MaxUploadBytes` | `4294967296` (4 GB) | Maximum upload size (spec requires ≥ 4 GB) |
| `ApiKey` | `""` | When non-empty, requests must send a matching `X-Api-Key` header |

## Persistence files

Both live inside `Receiver:ReceivedFolder`:

- **`Iot_FileTransfer_Records.json`** — the file-record store, rewritten on every
  change and reloaded at startup. See [Status Lifecycle](/Status-Lifecycle).
- **`Iot_FileTransfer_LastCallbackUrl.Txt`** — the last registered callback URL,
  reloaded at startup so the service resumes without re-registration.

## Logging

Serilog is configured from the `Serilog` section (console + daily rolling file).
On non-Windows hosts, override the file sink path (it defaults to a relative
`./temp/...` path).
