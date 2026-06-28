# FileTransfer

A single **.NET 8 Worker Service** that runs two independent background services
side by side in one process:

- a **Sender** that watches a hot folder and uploads new files to a REST API, and
- a **Receiver** that hosts an HTTP endpoint to accept compressed files,
  decompresses them, and tracks their processing status.

They share no state; each is configured from its own section of `appsettings.json`.

## Pages

- [Architecture](/Architecture) — components, processes and end-to-end flow
- [Configuration](/Configuration) — every `appsettings.json` option
- [Receiver API](/Receiver-API) — HTTP endpoints, payloads and examples
- [Status Lifecycle](/Status-Lifecycle) — file-record states and the callback contract
- [Sender (Hot Folder)](/Sender-Hot-Folder) — watcher, filters, retries
- [Testing and Mock Server](/Testing-and-Mock-Server) — unit tests and the end-to-end harness
- [Deployment](/Deployment) — build, publish and run as a Windows Service

## At a glance

| | Sender (`HotFolder`) | Receiver (`Receiver`) |
|---|---|---|
| Type | `BackgroundService` + `FileSystemWatcher` | `BackgroundService` hosting Kestrel |
| Trigger | new file in `SourceFolder` | inbound HTTP `POST` |
| Action | upload `multipart/form-data` to REST API | save, decompress, record status |
| Outbound HTTP | file upload | status callbacks |

## Solution layout

| Project | Purpose |
|---------|---------|
| `FileTransferService` | the Worker Service (sender + receiver) |
| `tests` | xUnit test suite |
| `tools/MockRestServer` | throwaway REST server for end-to-end testing |
