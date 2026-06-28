# Architecture

[[_TOC_]]

## Process model

`Program.cs` builds a generic `Host` (`UseWindowsService`) and registers two
`BackgroundService` hosted services that run concurrently in the same process.
Logging is **Serilog** (console + rolling file), configured from the `Serilog`
section of `appsettings.json`.

Shared singletons:

| Service | Role |
|---------|------|
| `ICallbackConfiguration` | holds the runtime-registered callback URL; persisted to a file so it survives restarts |
| `IStartTrigger` | gate that releases the sender once a callback has been registered |
| `IFileRecordStore` | thread-safe store of `FileRecord` entries, persisted to JSON |

## Components

::: mermaid
graph LR
  subgraph Process["FileTransfer Worker Service"]
    HFS["HotFolderService<br/>(sender)"]
    RFH["RestFileTransferHandler"]
    GRS["GzReceiverService<br/>(receiver, Kestrel)"]
    STORE["IFileRecordStore<br/>(persisted JSON)"]
    CB["ICallbackConfiguration<br/>(persisted)"]
    TRIG["IStartTrigger"]
  end

  FS["SourceFolder<br/>(hot folder)"] --> HFS
  HFS --> RFH --> API["Upstream REST API"]
  CLIENT["HTTP client"] -->|POST file| GRS
  GRS --> STORE
  GRS -->|status callback| API
  CB --- HFS
  CB --- GRS
  TRIG --- HFS
:::

## Sender pipeline (`HotFolder`)

1. `HotFolderService` waits for a callback to be registered (see `IStartTrigger`).
2. On release it scans `SourceFolder` for pre-existing files, then watches for new ones with a `FileSystemWatcher`. A `SemaphoreSlim` (sized by `Api.MaxConcurrentUploads`) throttles concurrent uploads; the watcher self-restarts on its `Error` event.
3. `RestFileTransferHandler` waits for the writer to release the file lock, resolves the `applicationType` from the producing sub-folder, uploads via `multipart/form-data` (file + `metadata` JSON) with retries, then archives/deletes the source on success or moves it to the error folder on failure.

See [Sender (Hot Folder)](/Sender-Hot-Folder).

## Receiver pipeline (`Receiver`)

`GzReceiverService` builds and runs its **own embedded Kestrel** `WebApplication`
on `Receiver.Port`. It exposes the endpoints documented in
[Receiver API](/Receiver-API), persists each file's status in `IFileRecordStore`,
and posts status callbacks to the registered URL.

## End-to-end flow

::: mermaid
sequenceDiagram
  participant Ext as External system
  participant Rec as Receiver
  participant Snd as Sender
  participant Api as Upstream API

  Ext->>Rec: GET /filetransfer/registercallback?callbackuri=...
  Note over Snd: StartTrigger released → watcher starts
  Ext->>Rec: POST /filetransfer (file.gz + metadata)
  Rec->>Rec: decompress + record status = unknown
  Ext->>Rec: POST /filetransfer/status (status != valid)
  Rec->>Api: POST callback (file record)
  Api-->>Rec: 2xx
  Rec->>Rec: remove record from store
  Snd->>Api: POST {callback}/api/files/upload (file)
:::
