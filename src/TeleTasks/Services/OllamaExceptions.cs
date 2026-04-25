namespace TeleTasks.Services;

public sealed class OllamaUnreachableException : Exception
{
    public OllamaUnreachableException(string message, Exception inner) : base(message, inner) { }
}

public sealed class OllamaModelMissingException : Exception
{
    public string Model { get; }

    public OllamaModelMissingException(string message, string model) : base(message)
    {
        Model = model;
    }
}
