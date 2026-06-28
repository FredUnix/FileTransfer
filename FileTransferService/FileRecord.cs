using System;
using System.Text.Json.Serialization;

namespace FileTransfer
{
    public sealed class FileRecord
    {
        [JsonPropertyName("fileName")]
        public string FileName { get; init; } = string.Empty;

        /// <summary>Server UTC time the file was received. Used for retention/cleanup.</summary>
        [JsonPropertyName("receivedAt")]
        public DateTimeOffset ReceivedAt { get; set; }

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = string.Empty;

        [JsonPropertyName("applicationType")]
        public string ApplicationType { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    public static class FileStatus
    {
        public const string Unknown             = "unknown";
        public const string FileTransferSuccess = "fileTransferSuccess";
        public const string FileTransferError   = "fileTransferError";
        public const string FileFormatError     = "fileFormatError";
        public const string FileValidationError = "fileValidationError";
        public const string Valid               = "valid";
    }
}
