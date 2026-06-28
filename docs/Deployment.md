# Deployment

[[_TOC_]]

## Build

```bash
dotnet build FileTransfer.sln
```

## Run locally (console)

The Worker Service runs as a plain console app on any platform. Override the
Windows folder defaults on macOS/Linux:

```bash
HotFolder__SourceFolder=/tmp/ft/Incoming \
HotFolder__ErrorFolder=/tmp/ft/Error \
Receiver__ReceivedFolder=/tmp/ft/Received \
Receiver__DecompressedFolder=/tmp/ft/Decompressed \
Receiver__Port=5100 \
dotnet run --project FileTransferService
```

## Publish a self-contained Windows binary

```bash
dotnet publish FileTransferService -c Release -r win-x64 --self-contained true -o ./publish
```

## Install as a Windows Service

The host calls `UseWindowsService` with service name **`FileTransfer`**.

```powershell
sc create FileTransfer binPath= "C:\path\to\publish\FileTransfer.exe" start= auto
sc start  FileTransfer
sc stop   FileTransfer
sc delete FileTransfer
```

## Operational notes

- **Folders** — ensure the configured `HotFolder` and `Receiver` folders exist or
  are writable; the service creates them where it can.
- **Port** — `Receiver:Port` must be free and permitted. Ports below 1024 (e.g.
  the sample `85`) require elevated privileges on some hosts.
- **Persistence** — `Receiver:ReceivedFolder` holds the record store and the
  callback URL file. Preserve this folder across upgrades so in-flight records and
  the registered callback survive restarts.
- **Authentication** — set `Receiver:ApiKey` to require an `X-Api-Key` header on
  inbound requests. Add outbound auth headers in the typed `HttpClient` setup in
  `Program.cs`.
- **Logs** — Serilog writes a daily rolling file; set the sink path appropriately
  per environment.

## Health check

```bash
curl http://<host>:<port>/filetransfer/ping
```

Returns the current UTC timestamp when the receiver is up.
