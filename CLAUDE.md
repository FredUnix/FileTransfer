# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Build the whole solution
dotnet build FileTransfer.sln

# Run all tests
dotnet test FileTransfer.sln

# Run a single test class / method (xUnit via VSTest filter)
dotnet test tests/FileTransfer.Tests.csproj --filter "FullyQualifiedName~RestFileTransferHandlerTests"
dotnet test tests/FileTransfer.Tests.csproj --filter "DisplayName~Upload_retries"

# Run the service locally as a console app (development)
dotnet run --project FileTransferService

# Publish a self-contained Windows binary (installed via `sc create`, see FileTransferService/README.md)
dotnet publish FileTransferService -c Release -r win-x64 --self-contained true -o ./publish
```

Note: the production project folder is `FileTransferService/`, but its csproj/assembly are named **`FileTransfer`** (the Windows service name is also `FileTransfer`). CI (`azure-pipelines.yml`) runs restore → build → `dotnet test` with `XPlat Code Coverage` on `ubuntu-latest`.

## Architecture

A single .NET 8 **Worker Service** (`Microsoft.NET.Sdk.Worker`, target `net8.0`) that runs as a Windows Service (`UseWindowsService`) but works as a plain console app cross-platform. `Program.cs` wires up DI and registers **two `BackgroundService` hosted services that run side by side in the same process** — a sender and a receiver — plus three shared singletons that coordinate them: `ICallbackConfiguration`, `IStartTrigger`, `IFileRecordStore`. Each pipeline is configured from its own `appsettings.json` section (`HotFolder` / `Receiver`).

**The two services are coupled by a startup gate.** Unlike a naive watcher, the sender does **not** start on boot — it awaits `IStartTrigger.WaitAsync` and only begins watching once a callback URL has been registered (or was persisted from a prior run). This is the central design point of this codebase.

**Sender pipeline (`HotFolder` section):** outbound files → REST upload.
- `HotFolderService` — `BackgroundService` wrapping a `FileSystemWatcher`. Blocks on `IStartTrigger` until the receiver signals it. On release it scans `SourceFolder` for pre-existing files then watches for new ones; a `SemaphoreSlim` (sized by `Api.MaxConcurrentUploads`) throttles concurrent uploads; the watcher self-restarts on its `Error` event.
- `RestFileTransferHandler` (`IFileTransferHandler`) — per file: waits for the writer to release the lock (`LockTimeoutSeconds`/`LockPollIntervalMs`), uploads via `multipart/form-data` with retries (`MaxRetries`/`RetryDelaySeconds`), then archives/deletes the source on success or moves it to the error folder on failure. Registered as a **typed `HttpClient`** (`AddHttpClient`); `BaseAddress`/timeout/headers are configured in `Program.cs` — that's where to add auth headers (see README).

**Receiver pipeline (`Receiver` section):** inbound HTTP → decompress + status/callback workflow.
- `GzReceiverService` — a `BackgroundService` that builds and runs its **own embedded `WebApplication`/Kestrel** on `Receiver.Port` (this is why `FileTransfer.csproj` references `Microsoft.AspNetCore.App` despite being a Worker project). Swagger UI is enabled. Endpoints:
  - `POST {ReceiveEndpoint}` — accepts a multipart `.gz` **or** `.zip` (field `FormFieldName`, plus an optional `metadata` JSON field with `fileName`/`timestamp`/`applicationType` — where `applicationType` must be one of `QCS`/`DFE`/`JRM`); saves to `ReceivedFolder`, and if `DecompressOnReceive` is true decompresses to `DecompressedFolder` (gz → single file, zip → extracted directory). Records a `FileRecord` — a successfully received file starts with status `unknown` (a validator updates it later via `POST /filetransfer/status`); receive failures record `fileFormatError`/`fileTransferError`. Returns **204** on success.
  - `GET /filetransfer/registercallback?callbackuri=<url>` — validates the URL, persists it via `ICallbackConfiguration`, and calls `IStartTrigger.Signal()` to release the sender. **This is what starts the sender.** Returns **204**.
  - `POST /filetransfer/status` — JSON `UpdateStatusRequest` (`fileName`/`applicationType`/`timestamp`/`status`/`message`); mutates a stored `FileRecord`'s status and persists it. For **any status except `valid`** it POSTs the record to the registered callback URL (via `IHttpClientFactory`) and, on a successful (2xx) callback, removes it from the store. `valid` keeps the record and sends no callback.
  - `GET /filetransfer/ping` — returns the current UTC timestamp.
  - Optional shared-secret auth on uploads via the `X-Api-Key` header when `Receiver.ApiKey` is non-empty.

**Coordination singletons.**
- `IStartTrigger` (`StartTrigger`) — a one-shot `TaskCompletionSource` gate; `Signal()` is idempotent. Wired so the sender waits and the receiver's callback registration signals.
- `ICallbackConfiguration` (`CallbackConfiguration`) — holds the registered callback URL and **persists it to a file** (`Receiver.CallbackEndpointFile` inside `ReceivedFolder`) so it survives restarts; if present at startup the sender starts immediately.
- `IFileRecordStore` (`FileRecordStore`) — thread-safe in-memory (`ConcurrentDictionary`) store of `FileRecord` keyed by file name. **Not persisted** — records are lost on restart. Status constants live in `FileStatus`.

**Configuration** is strongly typed: `HotFolderOptions` (+ nested `ApiOptions`) and `ReceiverOptions`, bound via `services.Configure<>`. Defaults live in the option classes; `appsettings.json` overrides them. Folder paths default to Windows `C:\HotFolder\...` — override these when running on macOS/Linux. Logging is **Serilog**, configured from the `Serilog` section.

## Testing conventions

- xUnit + Moq + `RichardSzalay.MockHttp` (mocks the sender's `HttpClient`) + `Microsoft.AspNetCore.TestHost` (in-process Kestrel for receiver tests).
- The production project exposes internals to tests via `<InternalsVisibleTo Include="FileTransfer.Tests" />` — receiver tests call `GzReceiverService.HandleUploadAsync` / `HandleUpdateStatusAsync` / `HandleRegisterCallback` directly rather than over HTTP.
- Shared fixtures live in `tests/TestHelpers.cs` (`TempFolder` for disposable isolated temp dirs, `OptionsFactory`, `HandlerFactory`) and `tests/GzReceiverTestHelpers.cs` (`MultipartGzBuilder`). Prefer these over hand-rolling options/HTTP setup in new tests.

## Manual API testing

`bruno-collection/` is a [Bruno](https://www.usebruno.com/) collection (Ping, Upload File, Register Callback, and the Update-Status variants) with `development`/`production` environments; see its `README.md` for the workflow. `export.csv.gz` at the repo root is a sample upload payload.
