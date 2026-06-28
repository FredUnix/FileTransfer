# Sender (Hot Folder)

[[_TOC_]]

The sender (`HotFolderService` + `RestFileTransferHandler`) watches a folder and
uploads each new file to the upstream REST API.

## Startup gate

`HotFolderService` does **not** start processing immediately. It waits on
`IStartTrigger`, which is released when a callback is registered
(`GET /filetransfer/registercallback`) — or immediately if a callback URL was
already persisted from a previous run. Watch the log for:

```
HotFolderService waiting for callback registration.
HotFolderService starting.
FileSystemWatcher active.
```

> Drop files only **after** `FileSystemWatcher active.` is logged. Files created
> during the brief startup window can be missed by the watcher (pre-existing
> files are picked up by the one-time startup scan).

## File filter

`FileFilter` accepts one or more glob patterns, separated by space, comma,
semicolon or pipe:

```
*.pdf *.jpg *.7z
```

Patterns are matched with OR semantics for both the startup scan and the live
watcher. A single pattern (e.g. `*.*`) works as before.

The sender forwards **any file type** it picks up (`.pdf`, `.jpg`, `.zip`, `.7z`,
…) — it does not restrict by extension. Whether the upstream accepts a given
type is the upstream's concern; this project's own [receiver](/Receiver-API),
for example, only accepts `.gz`/`.zip`.

## Upload

`RestFileTransferHandler`:

1. Waits for the writer to release the file lock (`LockTimeoutSeconds` /
   `LockPollIntervalMs`); if it never unlocks, the file is moved to the error folder.
2. Resolves the `applicationType` from the producing sub-folder (see
   [Application-type routing](#application-type-routing) below).
3. Uploads via `multipart/form-data`:
   - field `Api:FormFieldName` carries the file (Content-Type by extension),
   - a `metadata` section (`application/json`) with `{ fileName, timestamp, applicationType }` when routing is configured,
   - each `Api:ExtraFormFields` entry is added as a string field.
4. Target URL: `{callbackUri}{Api:UploadEndpoint}` when a callback is registered,
   otherwise `{Api:BaseUrl}{Api:UploadEndpoint}`.

## Application-type routing

When many applications drop files into the hot folder, each one writes to its
**own sub-folder**, and the sender tags the upload with the matching
`applicationType`. Configure the folder → type map and enable subfolder watching:

```json
"HotFolder": {
  "WatchSubdirectories": true,
  "ApplicationTypeFolders": { "qcs": "QCS", "dfe": "DFE", "jrm": "JRM" }
}
```

```
Incoming/qcs/report.pdf  ->  applicationType = QCS
Incoming/dfe/layout.pdf  ->  applicationType = DFE
Incoming/jrm/job.xml     ->  applicationType = JRM
Incoming/loose.pdf       ->  no mapped sub-folder  ->  Error folder
```

- On startup the service **auto-creates** a sub-folder under `SourceFolder` for
  every key in the map (e.g. `Incoming/qcs`, `Incoming/dfe`, `Incoming/jrm`), so
  producing applications always find their folder ready.
- The `applicationType` is taken from the **first path segment** under
  `SourceFolder`; folder names match **case-insensitively**.
- The sender adds a `metadata` JSON section
  `{ "fileName", "timestamp": <UTC>, "applicationType" }` — matching the
  receiver contract, where `applicationType` is one of `QCS`, `DFE`, `JRM`.
- When `ApplicationTypeFolders` is **non-empty**, a file that resolves to no type
  (e.g. dropped directly in `SourceFolder`) is **moved to the error folder** and
  never uploaded.
- When the map is **empty**, routing is disabled: no `metadata` section is sent
  and every file is uploaded as before (backward compatible).

> Adding a new producer is one folder + one config line — no redeploy. Apps should
> write atomically (write to a temp name, then move into their folder) so the file
> is complete when the watcher picks it up.

## Retries

Controlled by `MaxRetries` and `RetryDelaySeconds`:

- `MaxRetries > 0` — up to N attempts, then move to the error folder.
- **`MaxRetries <= 0` — retry forever** (waiting `RetryDelaySeconds` between
  attempts) until the upload succeeds or the service shuts down. On shutdown the
  file is left in place (not moved to the error folder) and retried next start.

::: mermaid
flowchart TD
  A[File detected] --> B[Wait for file lock]
  B -->|timeout| E[Move to ErrorFolder]
  B -->|ready| AT{applicationType resolved?}
  AT -->|no & routing on| E
  AT -->|yes / routing off| C[Upload attempt]
  C -->|success| S{ArchiveEnabled?}
  S -- yes --> AR[Move to ArchiveFolder]
  S -- no --> DL{DeleteSourceAfterTransfer?}
  DL -- yes --> RM[Delete source]
  DL -- no --> KEEP[Leave in place]
  C -->|fail| R{retries left or infinite?}
  R -- yes --> W[Wait RetryDelaySeconds] --> C
  R -- no --> E
:::

## Post-transfer handling

- **Success** → archive (`ArchiveEnabled`) or delete (`DeleteSourceAfterTransfer`).
- **Failure** → move to `ErrorFolder` (`ErrorFolderEnabled`), otherwise leave in place.

## Folder cleanup (retention)

A background `HotFolderCleanupService` keeps the hot-folder area from growing
without bound. Every `HotFolder:CleanupIntervalMinutes` (default 60) it deletes
files whose last-write time is older than `HotFolder:RetentionHours`
(default 168 = 7 days) from **all three** directories — `SourceFolder` (recursing
into the applicationType sub-folders), `ErrorFolder` and `ArchiveFolder`. Set
`RetentionHours` to `0` (or negative) to disable it.

- Only **files** are removed; sub-folders (e.g. `qcs/`, `dfe/`, `jrm/`) are kept.
- The retention window naturally avoids in-flight files (a just-dropped file is
  far younger than the window).

A `SemaphoreSlim` sized by `Api:MaxConcurrentUploads` throttles parallel uploads.
The `FileSystemWatcher` self-restarts if it raises an `Error` event.
