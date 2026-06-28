# HotFolderTransfer — Windows Service (REST upload)

A .NET 8 Worker Service that watches a **hot folder** and uploads each
new file to a **REST API** using `multipart/form-data`.

---

## Features

| Feature | Detail |
|---|---|
| **Lock detection** | Waits until the writer releases the file before uploading |
| **Multipart upload** | Standard `multipart/form-data` POST, configurable field name |
| **MIME detection** | Automatic Content-Type from extension |
| **Retry logic** | Configurable attempts + delay |
| **Concurrency control** | Semaphore limits simultaneous uploads |
| **Startup scan** | Uploads files already present at service start |
| **Archive mode** | Move source file to archive folder after success |
| **Error folder** | Failed files moved to a dedicated error folder |
| **Extra form fields** | Static metadata fields sent alongside the file |

---

## Project structure

```
HotFolderTransfer.sln
FileTransferService/             ← production project
  HotFolderTransfer.csproj
  Program.cs                     ← entry point, DI, typed HttpClient, hosted services
  appsettings.json               ← all settings (HotFolder + Receiver)
  ── Sender (hot folder → REST upload) ──
  HotFolderService.cs            ← BackgroundService + FileSystemWatcher
  FileTransferHandler.cs         ← REST upload logic
  HotFolderOptions.cs            ← typed config (HotFolderOptions + ApiOptions)
  ── Receiver (HTTP → decompress .gz) ──
  GzReceiverService.cs           ← Kestrel endpoint, accepts multipart .gz
  ReceiverOptions.cs             ← typed config (Receiver section)
tests/                           ← xUnit test project
  HotFolderTransfer.Tests.csproj
```

---

## Configuration (`appsettings.json`)

```jsonc
{
  "HotFolder": {
    // --- Folders ---
    "SourceFolder":              "C:\\HotFolder\\Incoming",
    "ArchiveFolder":             "C:\\HotFolder\\Archive",
    "ErrorFolder":               "C:\\HotFolder\\Error",

    // --- Watcher ---
    "FileFilter":                "*.*",        // "*.pdf", "*.xml", …
    "WatchSubdirectories":       false,

    // --- Source handling ---
    "DeleteSourceAfterTransfer": true,         // delete on success
    "ArchiveEnabled":            false,        // move to ArchiveFolder instead
    "ErrorFolderEnabled":        true,

    // --- Retry ---
    "MaxRetries":                3,
    "RetryDelaySeconds":         5,

    // --- Lock detection ---
    "LockTimeoutSeconds":        30,
    "LockPollIntervalMs":        500,

    // --- REST API ---
    "Api": {
      "BaseUrl":              "https://api.example.com",
      "UploadEndpoint":       "/api/files/upload",
      "FormFieldName":        "file",           // multipart field name
      "TimeoutSeconds":       120,
      "MaxConcurrentUploads": 4,

      // Optional static fields sent with every upload
      "ExtraFormFields": {
        "source":      "hotfolder",
        "environment": "production"
      }
    }
  }
}
```

---

## Adding authentication later

Open `Program.cs` and add headers inside `AddHttpClient`:

```csharp
// API Key
client.DefaultRequestHeaders.Add("X-Api-Key", opts.Api.ApiKey);

// Bearer token
client.DefaultRequestHeaders.Authorization =
    new AuthenticationHeaderValue("Bearer", opts.Api.Token);

// Basic Auth
var creds = Convert.ToBase64String(
    Encoding.ASCII.GetBytes($"{opts.Api.Username}:{opts.Api.Password}"));
client.DefaultRequestHeaders.Authorization =
    new AuthenticationHeaderValue("Basic", creds);
```

---

## Build & run

```bash
# Console (dev)
dotnet run

# Publish self-contained Windows binary
dotnet publish -c Release -r win-x64 --self-contained true -o ./publish
```

---

## Install as Windows Service

```cmd
sc create HotFolderTransfer ^
   binPath="C:\Services\HotFolderTransfer\HotFolderTransfer.exe" ^
   start=auto
sc description HotFolderTransfer "Hot-folder REST upload service"
sc start HotFolderTransfer
```

To stop / remove:

```cmd
sc stop   HotFolderTransfer
sc delete HotFolderTransfer
```
