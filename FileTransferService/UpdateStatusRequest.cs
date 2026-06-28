using System.Text.Json.Serialization;

namespace FileTransfer
{
    public sealed class UpdateStatusRequest
    {
        [JsonPropertyName("fileName")]
        public string FileName { get; init; } = string.Empty;

        [JsonPropertyName("applicationType")]
        public string ApplicationType { get; init; } = string.Empty;

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; init; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; init; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; init; } = string.Empty;
    }
}
