using DogdouSpec.Core.Diagnostics;

namespace DogdouSpec.Core.XPath;

/// <summary>
/// Exception representing a domain or limit error during XPath compilation or evaluation.
/// </summary>
public sealed class DogdouXPathException : Exception
{
    public string Code { get; }
    public int ExitCode { get; }
    public string? Document { get; }
    public int? Line { get; }
    public int? Column { get; }

    public DogdouXPathException(
        string code,
        string message,
        int exitCode = 2,
        string? document = null,
        int? line = null,
        int? column = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        ExitCode = exitCode;
        Document = document;
        Line = line;
        Column = column;
    }

    public Diagnostic ToDiagnostic() =>
        Diagnostic.Error(Code, Message, Document, Line, Column);
}
