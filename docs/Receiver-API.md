# Receiver API

[[_TOC_]]

The receiver hosts these endpoints on `Receiver:Port`. When `Receiver:ApiKey` is
non-empty, every request must include a matching `X-Api-Key` header.

| Method | Path | Purpose |
|--------|------|---------|
| `POST` | `/filetransfer` *(`ReceiveEndpoint`)* | Upload a `.gz`/`.zip` file |
| `GET`  | `/filetransfer/registercallback` | Register the callback URL (starts the sender) |
| `POST` | `/filetransfer/status` | Receive a file's transfer status |
| `GET`  | `/filetransfer/statuses` | List all file records |
| `GET`  | `/filetransfer/ping` | Health check (UTC timestamp) |

## POST /filetransfer — upload a file

Content-Type **`multipart/form-data`** or **`multipart/mixed`** (the spec's type)
— both must include a `boundary`. Sections:

- `file` *(`FormFieldName`)* — the archive file (`application/octet-stream`) **(required)**.
- `metadata` — optional JSON object (`application/json`) with exactly three fields:

> **Accepted formats:** only **`.gz`** and **`.zip`** are accepted. Any other
> extension — e.g. `.pdf`, `.jpg`, `.7z` — is rejected with **`400`** and a
> `fileFormatError` record. (The *sender* hot folder forwards any file type; the
> format restriction applies to this receive endpoint.)


```json
{ "fileName": "", "timestamp": "", "applicationType": "QCS" }
```

`applicationType` must be one of **`QCS`**, **`DFE`**, **`JRM`** when supplied;
an invalid value is rejected with `400`.

The file is saved to `ReceivedFolder` and, when `DecompressOnReceive` is `true`,
decompressed to `DecompressedFolder` (`.gz` → single file, `.zip` → extracted
directory). A `FileRecord` is created with status **`unknown`** and the
`timestamp` / `applicationType` values from the metadata.

**Responses**

| Code | When |
|------|------|
| `204` | Accepted — file received (no response body) |
| `400` | Missing file field, extension not `.gz`/`.zip` (records `fileFormatError`), or invalid `applicationType` |
| `415` | Content-Type is not `multipart/form-data` or `multipart/mixed` |
| `401` | Missing/invalid `X-Api-Key` |
| `500` | Unexpected error (records `fileTransferError`) |

```bash
curl -F "file=@export.csv.gz" \
     -F 'metadata={"applicationType":"QCS"}' \
     http://localhost:5100/filetransfer
```

## GET /filetransfer/registercallback

Registers the URL that receives status callbacks **and** signals the sender to
start. The URL is persisted.

Query parameter: `callbackuri` — an absolute `http`/`https` URL (required).

```bash
curl "http://localhost:5100/filetransfer/registercallback?callbackuri=http://host:6000/"
```

| Code | When |
|------|------|
| `204` | Registered (no content) |
| `400` | Missing or invalid URL (body is an error payload) |

## POST /filetransfer/status

Receives the transfer status of a previously received file. `application/json` body:

```json
{ "fileName": "export.csv.gz", "applicationType": "QCS", "timestamp": "2026-06-19T10:00:00Z", "status": "fileValidationError", "message": "missing CustomerID" }
```

`timestamp` and `applicationType` are optional; when present they update the record.

Allowed `status` values: `fileTransferSuccess`, `fileTransferError`,
`fileFormatError`, `fileValidationError`, `valid`.

Behaviour (see [Status Lifecycle](/Status-Lifecycle)):

- The record is updated and **persisted**.
- For **any status except `valid`**, the record is posted to the registered
  callback; on a successful (2xx) callback the record is **removed** from the store.
- For `valid`, no callback is sent and the record is kept.

| Code | When |
|------|------|
| `200` | Updated |
| `400` | Invalid JSON, missing `fileName`, or unknown `status` |
| `404` | No record for the given `fileName` |

## GET /filetransfer/statuses

Returns all file records as a JSON array:

```json
[
  {
    "fileName": "export.csv.gz",
    "receivedAt": "2026-06-19T10:00:01.234Z",
    "timestamp": "2026-06-19T10:00:00Z",
    "applicationType": "QCS",
    "status": "unknown",
    "message": "File received and decompressed successfully."
  }
]
```

## GET /filetransfer/ping

Returns the current UTC time as `yyyy-MM-ddTHH:mm:ssZ`. Use it to confirm the
receiver is up.
