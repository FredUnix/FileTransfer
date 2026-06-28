using System.Text.Json.Serialization;

namespace FileTransfer
{
    /// <summary>
    /// JSON object sent alongside an upload in the multipart "metadata" form
    /// field, e.g. <c>{"fileName":"","timestamp":"","applicationType":"QCS"}</c>.
    /// Contains only these three fields; <see cref="ApplicationType"/> must be one
    /// of the values in <see cref="FileTransfer.ApplicationType"/> (QCS, DFE, JRM).
    /// </summary>
    public sealed class UploadMetadata
    {
        [JsonPropertyName("fileName")]
        public string FileName { get; init; } = string.Empty;

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; init; } = string.Empty;

        [JsonPropertyName("applicationType")]
        public string ApplicationType { get; init; } = string.Empty;
    }
}
