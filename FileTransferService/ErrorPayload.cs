namespace FileTransfer
{
    public sealed class ErrorPayload
    {
        public string Code { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;

        public override string ToString() => $"[{Code}] {Name}";
    }
}
