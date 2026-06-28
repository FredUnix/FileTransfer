# MockRestServer

A throwaway REST server for exercising the **FileTransfer** service end-to-end.
It stands in for the upstream HTTP API and captures every request it receives,
so you can drive the real sender/receiver and then assert what they actually sent.

It covers **both** outbound HTTP flows of the FileTransfer service:

| Flow | Who calls | Request the mock receives |
|------|-----------|---------------------------|
| File upload   | `HotFolderService` (sender)   | `POST {callback}/api/files/upload` — multipart/form-data with the file |
| Status callback | `GzReceiverService` (receiver) | `POST {callback}` — JSON file-record payload (every status except `valid`) |

## Run

```bash
# default: listen on :6000, respond 200 to everything
dotnet run --project tools/MockRestServer

# pick a port / simulate a failing upstream (e.g. to test retries & "record kept")
dotnet run --project tools/MockRestServer -- --MockServer:Port=6000 --MockServer:StatusCode=500
```

Configuration (appsettings.json, env vars `MockServer__Port` / `MockServer__StatusCode`,
or `--MockServer:Xxx` args):

| Key | Default | Purpose |
|-----|---------|---------|
| `MockServer:Port`       | `6000` | Port to listen on |
| `MockServer:StatusCode` | `200`  | Status code returned to every caller (set `500` to test failure paths) |

## Endpoints

- `ANY /{**path}` — records the request and replies with `MockServer:StatusCode`.
- `GET  /__ping` — health check (`ok`).
- `GET  /__requests` — JSON array of everything captured (method, path, content-type, body, uploaded files).
- `GET  /__requests/count` — `{ "count": n }`.
- `POST /__reset` — clears the captured log.

## End-to-end recipe

1. **Start the mock**: `dotnet run --project tools/MockRestServer`
2. **Start the service**: `dotnet run --project FileTransferService`
   (override the Windows folder paths on macOS/Linux, e.g. `Receiver__ReceivedFolder=...`).
3. **Register the callback** so the sender starts and the receiver knows where to call back.
   The registered URL is used as the base for **both** flows:
   ```bash
   curl "http://localhost:5100/filetransfer/registercallback?callbackuri=http://127.0.0.1:6000/"
   ```
4. **Exercise the sender** — drop a file into the hot folder *after* the watcher is active
   (look for `FileSystemWatcher active.` in the log). It is uploaded to
   `http://127.0.0.1:6000/api/files/upload`.
5. **Exercise the receiver** — upload a `.gz`, then post a non-`valid` status:
   ```bash
   curl -F "file=@export.csv.gz" -F 'metadata={"applicationType":"SampleApp"}' http://localhost:5100/filetransfer
   curl -X POST -H "Content-Type: application/json" \
     -d '{"fileName":"export.csv.gz","status":"fileValidationError","message":"bad"}' \
     http://localhost:5100/filetransfer/updatestatus
   ```
6. **Inspect what arrived**:
   ```bash
   curl http://127.0.0.1:6000/__requests | python3 -m json.tool
   ```
   You should see the multipart upload (`POST /api/files/upload`, with the file) and the
   callback (`POST /`, with the file-record JSON).

> Tip: drop sender files only once `FileSystemWatcher active.` has been logged — files
> created during the brief startup window can be missed by the watcher.
