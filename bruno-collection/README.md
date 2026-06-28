# FileTransfer API - Bruno Collection

This Bruno collection provides comprehensive testing and management of the FileTransfer API status endpoints.

## Setup

1. **Install Bruno**: Download from [https://www.usebruno.com/](https://www.usebruno.com/)
2. **Open Collection**: File → Open Collection → Select this folder
3. **Configure Environment**: Select environment (development/production) from dropdown

## API Endpoints

### 1. Ping (GET /filetransfer/ping)
Health check endpoint that returns current UTC timestamp.

### 2. Register Callback (GET /filetransfer/registercallback)
Registers a callback URL that receives notifications when files are validated.
- **Query Param**: `callbackuri` - Full HTTP/HTTPS URL

### 3. Upload File (POST /filetransfer)
Uploads a file via multipart/form-data (or multipart/mixed).
- **Header**: `X-Api-Key` - API key (if authentication enabled)
- **Form Field**: `file` - The archive file
- **Form Field**: `metadata` - JSON `{ fileName, timestamp, applicationType }` (applicationType: QCS, DFE or JRM)
- **Accepted formats**: only `.gz` and `.zip` succeed (`204`). Other extensions
  such as `.pdf`, `.jpg`, `.7z` are rejected with `400` (`fileFormatError`).

### 4. Update Status (POST /filetransfer/status)
Updates the processing status of a received file.

**Valid Status Values**:
- `fileTransferSuccess` - File received successfully
- `fileTransferError` - Transfer failed
- `fileFormatError` - Invalid file format
- `fileValidationError` - Failed validation rules
- `valid` - Passed all checks

Every status update **other than `valid`** posts the file record payload to the
registered callback endpoint; once the callback is delivered successfully (2xx)
the record is removed from the store. If the callback fails (or none is
registered) the record is kept. `valid` updates the record but sends no callback
and keeps it.

### 5. Get Statuses (GET /filetransfer/statuses)
Returns all file records currently held by the receiver as a JSON array.
Records are persisted to disk (`Iot_FileTransfer_Records.json` in the received
folder) and survive a service restart. Each record contains `fileName`,
`applicationType`, `status`, and `message`.

## File Status Workflow

## Testing Workflow

1. **Start API**: Ensure the FileTransfer service is running
2. **Ping**: Verify API is responsive
3. **Register Callback**: Set callback endpoint for notifications
4. **Upload File**: Transfer a test file
5. **Update Status**: Manage file status through various states
6. **Monitor**: Check logs to see callback triggers when status set to 'valid'

## Environment Variables

- `baseUrl`: API base URL
- `apiKey`: API authentication key
- `callbackUri`: Callback endpoint URL

## Sample Test Files

Place test files in `./sample-files/` directory:
- `test.csv.gz` - Compressed CSV file
- `sample.zip` - Zip archive

## Notes

- Every status update except `valid` posts the file record to the registered callback endpoint
- A record is removed from the store after its callback is delivered successfully (2xx)
- `valid` updates the record without sending a callback and keeps it
- Records that fail to deliver (or have no callback registered) are kept and persisted across restarts
- API key authentication can be disabled in configuration