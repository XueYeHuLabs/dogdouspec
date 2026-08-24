using System.Globalization;
using System.Text;
using System.Xml;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;

namespace DogdouSpec.Core.Validation;

public sealed record DocumentValidationResult(
    string RelativePath,
    bool IsValid,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Result of a validation operation across a workspace, iteration, or single document.
/// </summary>
public sealed class ValidationResult
{
    public const string SemanticNotice =
        "Schema and semantic validation passed.";

    public bool IsValid { get; }
    public string Scope { get; }
    public string? IterationId { get; }
    public string? DocumentPath { get; }
    public int CheckedDocumentsCount { get; }
    public IReadOnlyList<DocumentValidationResult> DocumentResults { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public static string SemanticValidationNotice => SemanticNotice;

    public ValidationResult(
        bool isValid,
        string scope,
        int checkedDocumentsCount,
        IReadOnlyList<DocumentValidationResult> documentResults,
        IReadOnlyList<Diagnostic> diagnostics,
        string? iterationId = null,
        string? documentPath = null)
    {
        IsValid = isValid;
        Scope = scope;
        CheckedDocumentsCount = checkedDocumentsCount;
        DocumentResults = documentResults ?? Array.Empty<DocumentValidationResult>();
        Diagnostics = diagnostics ?? Array.Empty<Diagnostic>();
        IterationId = iterationId;
        DocumentPath = documentPath;
    }

    public string ToSuccessXmlString()
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            OmitXmlDeclaration = false,
            Encoding = new UTF8Encoding(false),
            NewLineHandling = NewLineHandling.Replace,
            NewLineChars = "\n"
        };

        using var memoryStream = new MemoryStream();
        using (var writer = XmlWriter.Create(memoryStream, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("validation");
            writer.WriteAttributeString("valid", "true");
            writer.WriteAttributeString("scope", Scope);

            if (!string.IsNullOrEmpty(IterationId))
            {
                writer.WriteAttributeString("iteration", IterationId);
            }

            if (!string.IsNullOrEmpty(DocumentPath))
            {
                writer.WriteAttributeString("document", DocumentPath);
            }

            writer.WriteAttributeString("schema", "passed");
            writer.WriteAttributeString("semantic", "passed");
            writer.WriteAttributeString("checked_documents", CheckedDocumentsCount.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(memoryStream.ToArray()) + "\n";
    }

    public string ToSuccessHumanString()
    {
        var sb = new StringBuilder();
        var scopeDesc = Scope switch
        {
            "iteration" => $"iteration '{IterationId}'",
            "document" => $"document '{DocumentPath}'",
            _ => "workspace"
        };

        sb.AppendLine(CultureInfo.InvariantCulture, $"Validation passed: {CheckedDocumentsCount} document(s) checked in {scopeDesc}.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Notice: {SemanticValidationNotice}");
        return sb.ToString();
    }

    public DiagnosticsEnvelope CreateDiagnosticsEnvelope(string command = "validate") =>
        new(command, Diagnostics);
}
