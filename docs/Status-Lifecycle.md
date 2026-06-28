# Status Lifecycle

[[_TOC_]]

Every received file is tracked by a **`FileRecord`** held in `IFileRecordStore`
and persisted to `Iot_FileTransfer_Records.json` (in `Receiver:ReceivedFolder`).
The store is rewritten on every change and reloaded at startup, so records
survive restarts.

## Record shape

```json
{
  "fileName": "export.csv.gz",
  "receivedAt": "2026-06-19T10:00:01.234Z",
  "timestamp": "2026-06-19T10:00:00Z",
  "applicationType": "QCS",
  "status": "unknown",
  "message": "File received and decompressed successfully."
}
```

`fileName` is the key (used by the status update). `receivedAt` is the server UTC
receive time (used for retention). `timestamp` and `applicationType` come from the
upload `metadata`; `applicationType` is one of `QCS`, `DFE`, `JRM`.

## Statuses

| Status | Meaning |
|--------|---------|
| `unknown` | Initial state of a successfully received file (awaiting validation) |
| `fileTransferSuccess` | Set via `status update` |
| `fileTransferError` | Receive failed, or set via `status update` |
| `fileFormatError` | Upload was not a `.gz`/`.zip`, or set via `status update` |
| `fileValidationError` | Set via `status update` |
| `valid` | Set via `status update`; terminal, no callback |

## State machine

::: mermaid
stateDiagram-v2
  [*] --> unknown: file received OK
  [*] --> fileFormatError: bad extension
  [*] --> fileTransferError: receive error
  unknown --> fileTransferSuccess: status update
  unknown --> fileTransferError: status update
  unknown --> fileFormatError: status update
  unknown --> fileValidationError: status update
  unknown --> valid: status update
  fileTransferSuccess --> [*]: callback 2xx → removed
  fileTransferError --> [*]: callback 2xx → removed
  fileFormatError --> [*]: callback 2xx → removed
  fileValidationError --> [*]: callback 2xx → removed
  valid --> valid: kept (no callback)
:::

## Callback contract

On a status update:

1. The change is written to the record and persisted.
2. **If the status is anything other than `valid`** and a callback URL is
   registered, the full record is `POST`ed (as JSON) to that URL.
   - On a **2xx** response the record is **removed** from the store.
   - On a non-2xx response, an exception, or no registered callback, the record
     is **kept** (with its updated status) so the change is not lost.
3. **If the status is `valid`**, no callback is sent and the record is kept.

::: mermaid
flowchart TD
  A[status update] --> B{status == valid?}
  B -- yes --> K[keep record, no callback]
  B -- no --> C{callback registered?}
  C -- no --> K2[keep record]
  C -- yes --> D[POST record to callback]
  D --> E{2xx?}
  E -- yes --> R[remove record]
  E -- no --> K3[keep record]
:::

## Retention / cleanup

The callback path above is the *normal* way a record leaves the store. But a file
that **never receives a status update** (status stays `unknown`), a `valid` record,
or a record whose callback keeps failing would otherwise persist forever.

A background **cleanup** (`RecordCleanupService`) is the safety net: every
`Receiver:RecordCleanupIntervalMinutes` (default 60) it purges any record whose
`receivedAt` is older than `Receiver:RecordRetentionHours` (default 168 = 7 days),
**regardless of status**, and persists the change. Set `RecordRetentionHours` to
`0` (or negative) to disable cleanup and keep records indefinitely.

- `receivedAt` is stamped by the server when the record is first stored, and
  survives restarts (so age is measured from the original receive time).
- Legacy records persisted before `receivedAt` existed have no value and are
  never auto-pruned.
