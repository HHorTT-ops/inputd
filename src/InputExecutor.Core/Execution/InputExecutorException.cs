namespace InputExecutor.Core.Execution;

public sealed class InputExecutorException : Exception
{
    public InputExecutorException(string code, string message, int httpStatus = 400, object? details = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        HttpStatus = httpStatus;
        Details = details;
    }

    public string Code { get; }
    public int HttpStatus { get; }
    public object? Details { get; }
}
