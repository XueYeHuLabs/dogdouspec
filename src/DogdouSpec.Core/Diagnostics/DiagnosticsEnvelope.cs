using System.Globalization;
using System.Text;
using System.Xml;
using DogdouSpec.Core.Formatting;

namespace DogdouSpec.Core.Diagnostics;

/// <summary>
/// Container for diagnostics returned by a command execution.
/// </summary>
public sealed class DiagnosticsEnvelope
{
    public string Command { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public DiagnosticsEnvelope(string command, IReadOnlyList<Diagnostic> diagnostics)
    {
        Command = command ?? string.Empty;
        Diagnostics = diagnostics ?? Array.Empty<Diagnostic>();
    }

    public DiagnosticsEnvelope(string command, Diagnostic singleDiagnostic)
    {
        Command = command ?? string.Empty;
        Diagnostics = new[] { singleDiagnostic };
    }

    public string ToXmlString()
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
            writer.WriteStartElement("diagnostics");
            writer.WriteAttributeString("command", Command);

            foreach (var diag in Diagnostics)
            {
                writer.WriteStartElement("diagnostic");
                writer.WriteAttributeString("code", diag.Code);
                writer.WriteAttributeString("severity", diag.Severity);

                if (!string.IsNullOrEmpty(diag.Document))
                {
                    writer.WriteAttributeString("document", diag.Document);
                }

                if (diag.Line.HasValue)
                {
                    writer.WriteAttributeString("line", diag.Line.Value.ToString(CultureInfo.InvariantCulture));
                }

                if (diag.Column.HasValue)
                {
                    writer.WriteAttributeString("column", diag.Column.Value.ToString(CultureInfo.InvariantCulture));
                }

                if (diag.ExpectedRevision.HasValue)
                {
                    writer.WriteAttributeString("expected_revision", diag.ExpectedRevision.Value.ToString(CultureInfo.InvariantCulture));
                }

                if (diag.ActualRevision.HasValue)
                {
                    writer.WriteAttributeString("actual_revision", diag.ActualRevision.Value.ToString(CultureInfo.InvariantCulture));
                }

                writer.WriteString(diag.Message);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(memoryStream.ToArray()) + "\n";
    }

    public string ToHumanString()
    {
        var sb = new StringBuilder();
        foreach (var diag in Diagnostics)
        {
            var prefix = diag.Severity.ToUpperInvariant();
            var location = string.Empty;

            if (!string.IsNullOrEmpty(diag.Document))
            {
                if (diag.Line.HasValue)
                {
                    location = diag.Column.HasValue
                        ? $" ({diag.Document}:{diag.Line}:{diag.Column})"
                        : $" ({diag.Document}:{diag.Line})";
                }
                else
                {
                    location = $" ({diag.Document})";
                }
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"[{prefix}] {diag.Code}{location}: {diag.Message}");
        }

        return sb.ToString();
    }

    public string Format(OutputFormat format) =>
        format == OutputFormat.Xml ? ToXmlString() : ToHumanString();

    public int GetExitCode() =>
        GetExitCodeForDiagnostics(Diagnostics);

    public static int GetExitCodeForCode(string code) =>
        code switch
        {
            DiagnosticCodes.RevisionConflict => 4,
            DiagnosticCodes.LockConflict => 4,
            DiagnosticCodes.CardinalityConflict => 4,
            DiagnosticCodes.IdempotencyConflict => 4,
            DiagnosticCodes.TaskTransitionConflict => 4,
            DiagnosticCodes.IterationAlreadyExists => 4,
            DiagnosticCodes.ManagedStateExists => 4,

            DiagnosticCodes.OwnerDecisionRequired => 5,

            DiagnosticCodes.FilesystemError => 6,
            DiagnosticCodes.RecoveryFailed => 6,
            DiagnosticCodes.CommitFailed => 6,
            DiagnosticCodes.InitializationFailed => 6,

            DiagnosticCodes.LimitExceeded => 7,

            DiagnosticCodes.SchemaValidationError => 3,
            DiagnosticCodes.UnknownDocumentType => 3,
            DiagnosticCodes.DuplicateId => 3,
            DiagnosticCodes.InvalidIdGrammar => 3,
            DiagnosticCodes.IterationIdMismatch => 3,
            DiagnosticCodes.TasksIterationMismatch => 3,
            DiagnosticCodes.WorkKindMismatch => 3,
            DiagnosticCodes.DanglingReference => 3,
            DiagnosticCodes.AmbiguousReference => 3,
            DiagnosticCodes.ReferenceScopeViolation => 3,
            DiagnosticCodes.ReferenceScopeNotNarrowest => 3,
            DiagnosticCodes.InvalidReferenceTargetType => 3,
            DiagnosticCodes.ContradictoryConfirmationDecision => 3,
            DiagnosticCodes.DuplicateConfirmationTarget => 3,
            DiagnosticCodes.SemanticContextIncomplete => 3,
            DiagnosticCodes.DependencyCycle => 3,
            DiagnosticCodes.TaskCriterionNotTerminal => 3,
            DiagnosticCodes.TaskCompletedAtMissing => 3,
            DiagnosticCodes.TaskCompletionRecordMissing => 3,
            DiagnosticCodes.TaskCriterionNotCovered => 3,
            DiagnosticCodes.TaskActiveFindingBlocksCompletion => 3,
            DiagnosticCodes.TaskNonDoneHasCompletedAt => 3,
            DiagnosticCodes.TaskPendingHasStartedAt => 3,
            DiagnosticCodes.MissingConfirmationProvenance => 3,
            DiagnosticCodes.IterationCompletionPredicateFailed => 3,
            DiagnosticCodes.IterationCompletedAtMissing => 3,
            DiagnosticCodes.WaiverRationaleMissing => 3,

            _ => 2
        };

    public static int GetExitCodeForDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        var errors = diagnostics.Where(d => string.Equals(d.Severity, "error", StringComparison.OrdinalIgnoreCase)).ToList();
        if (errors.Count == 0) return 0;
        var exitCodes = errors.Select(e => GetExitCodeForCode(e.Code)).ToList();
        if (exitCodes.Contains(6)) return 6;
        if (exitCodes.Contains(5)) return 5;
        if (exitCodes.Contains(4)) return 4;
        if (exitCodes.Contains(3)) return 3;
        if (exitCodes.Contains(7)) return 7;
        return 2;
    }
}
