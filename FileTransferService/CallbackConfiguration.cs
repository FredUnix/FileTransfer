using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileTransfer
{
    public interface ICallbackConfiguration
    {
        string CallbackEndpoint { get; set; }
        bool IsRegistered { get; }
    }

    /// <summary>
    /// Singleton that holds the runtime-registered callback URL.
    /// Persisted to a file so it survives service restarts.
    /// </summary>
    public sealed class CallbackConfiguration : ICallbackConfiguration
    {
        private readonly string _filePath;
        private readonly ILogger<CallbackConfiguration> _logger;
        private string _callbackEndpoint = string.Empty;

        public CallbackConfiguration(
            IOptions<ReceiverOptions> options,
            ILogger<CallbackConfiguration> logger)
        {
            _filePath = Path.Combine(options.Value.ReceivedFolder, options.Value.CallbackEndpointFile);
            _logger   = logger;

            if (File.Exists(_filePath))
            {
                _callbackEndpoint = File.ReadAllText(_filePath).Trim();
                _logger.LogInformation("Loaded callback endpoint from file: {Endpoint}", _callbackEndpoint);
            }
        }

        public bool IsRegistered => !string.IsNullOrWhiteSpace(_callbackEndpoint);

        public string CallbackEndpoint
        {
            get => _callbackEndpoint;
            set
            {
                _callbackEndpoint = value;
                File.WriteAllText(_filePath, value);
                _logger.LogInformation("Callback endpoint saved to file: {Endpoint}", value);
            }
        }
    }
}
