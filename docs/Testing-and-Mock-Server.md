# Testing and Mock Server

[[_TOC_]]

## Unit / integration tests

The `tests` project uses **xUnit + Moq + RichardSzalay.MockHttp +
Microsoft.AspNetCore.TestHost**. Receiver tests drive an in-process Kestrel
`TestServer` and call the internal handlers directly (exposed via
`InternalsVisibleTo`).

```bash
# all tests
dotnet test tests/FileTransfer.Tests.csproj

# a single class / method
dotnet test tests/FileTransfer.Tests.csproj --filter "FullyQualifiedName~GzReceiverServiceTests"
dotnet test tests/FileTransfer.Tests.csproj --filter "DisplayName~Upload_retries"
```

Shared fixtures live in `tests/TestHelpers.cs` (`TempFolder`, `OptionsFactory`,
`HandlerFactory`) and `tests/GzReceiverTestHelpers.cs` (`MultipartGzBuilder`,
`StubFileRecordStore`, `StubHttpClientFactory`). Prefer these over hand-rolling
setup.

## MockRestServer (end-to-end)

`tools/MockRestServer` is a throwaway REST server that **captures every request**
it receives, so it can stand in for the upstream API for both outbound flows:
file uploads (sender) and status callbacks (receiver).

```bash
# default: listen on :6000, respond 200
dotnet run --project tools/MockRestServer

# simulate a failing upstream (test retries / "record kept on failed callback")
dotnet run --project tools/MockRestServer -- --MockServer:StatusCode=500
```

| Endpoint | Purpose |
|----------|---------|
| `ANY /{**path}` | record the request, reply with `MockServer:StatusCode` |
| `GET /__ping` | health check |
| `GET /__requests` | JSON array of captured requests (method, path, body, files) |
| `GET /__requests/count` | `{ "count": n }` |
| `POST /__reset` | clear the captured log |

### End-to-end recipe

1. Start the mock: `dotnet run --project tools/MockRestServer`
2. Start the service: `dotnet run --project FileTransferService` (override the
   Windows folder paths on macOS/Linux).
3. Register the callback (used as the base for **both** flows):
   ```bash
   curl "http://localhost:5100/filetransfer/registercallback?callbackuri=http://127.0.0.1:6000/"
   ```
4. **Sender:** after `FileSystemWatcher active.` is logged, drop a file into the
   hot folder → uploaded to `http://127.0.0.1:6000/api/files/upload`.
5. **Receiver:** upload a `.gz`, then post a non-`valid` status:
   ```bash
   curl -F "file=@export.csv.gz" -F 'metadata={"applicationType":"QCS"}' http://localhost:5100/filetransfer
   curl -X POST -H "Content-Type: application/json" \
     -d '{"fileName":"export.csv.gz","status":"fileValidationError","message":"bad"}' \
     http://localhost:5100/filetransfer/status
   ```
6. Inspect what arrived: `curl http://127.0.0.1:6000/__requests | python3 -m json.tool`

## Bruno collection

`bruno-collection/` holds a [Bruno](https://www.usebruno.com/) collection
(Ping, Register Callback, Upload File, Update Status ×5, Get Statuses) targeting
the receiver, with `development` and `production` environments.

## CI

`azure-pipelines.yml` runs restore → build → `dotnet test` with
`XPlat Code Coverage` on `ubuntu-latest`, triggered on `main`/`master` and PRs.
