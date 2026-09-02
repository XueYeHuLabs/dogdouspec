using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DogdouSpec.Core.Formatting;

namespace DogdouSpec.Core.Tasks;

public sealed record ScopePathExplanation(
    string Path,
    bool InScope,
    string? MatchedRule = null,
    string? RuleKind = null,
    string? Reason = null);

/// <summary>
/// Result container for task repository scope verification.
/// </summary>
public sealed class TaskScopeResult
{
    public string TaskId { get; }
    public string IterationId { get; }
    public XElement? DeclaredScopeElement { get; }
    public IReadOnlyList<string> InScopePaths { get; }
    public IReadOnlyList<string> OutOfScopePaths { get; }
    public IReadOnlyList<ScopePathExplanation> Explanations { get; }
    public bool IsValid => OutOfScopePaths.Count == 0;

    public TaskScopeResult(
        string taskId,
        string iterationId,
        XElement? declaredScopeElement,
        IReadOnlyList<string> inScopePaths,
        IReadOnlyList<string> outOfScopePaths,
        IReadOnlyList<ScopePathExplanation>? explanations = null)
    {
        TaskId = taskId ?? string.Empty;
        IterationId = iterationId ?? string.Empty;
        DeclaredScopeElement = declaredScopeElement;
        InScopePaths = inScopePaths ?? Array.Empty<string>();
        OutOfScopePaths = outOfScopePaths ?? Array.Empty<string>();
        Explanations = explanations ?? Array.Empty<ScopePathExplanation>();
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

        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("task-scope");
            writer.WriteAttributeString("task", TaskId);
            writer.WriteAttributeString("iteration", IterationId);
            writer.WriteAttributeString("valid", IsValid ? "true" : "false");
            writer.WriteAttributeString("in_scope_count", InScopePaths.Count.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("out_of_scope_count", OutOfScopePaths.Count.ToString(CultureInfo.InvariantCulture));

            if (DeclaredScopeElement != null)
            {
                writer.WriteStartElement("declared_scope");
                foreach (var child in DeclaredScopeElement.Elements())
                {
                    child.WriteTo(writer);
                }
                writer.WriteEndElement();
            }

            writer.WriteStartElement("in_scope");
            foreach (var path in InScopePaths)
            {
                writer.WriteElementString("path", path);
            }
            writer.WriteEndElement();

            writer.WriteStartElement("out_of_scope");
            foreach (var path in OutOfScopePaths)
            {
                writer.WriteElementString("path", path);
            }
            writer.WriteEndElement();

            if (Explanations.Count > 0)
            {
                writer.WriteStartElement("explanations");
                foreach (var exp in Explanations)
                {
                    writer.WriteStartElement("explanation");
                    writer.WriteAttributeString("path", exp.Path);
                    writer.WriteAttributeString("in_scope", exp.InScope ? "true" : "false");
                    if (!string.IsNullOrWhiteSpace(exp.MatchedRule))
                    {
                        writer.WriteAttributeString("matched_rule", exp.MatchedRule);
                    }
                    if (!string.IsNullOrWhiteSpace(exp.RuleKind))
                    {
                        writer.WriteAttributeString("rule_kind", exp.RuleKind);
                    }
                    if (!string.IsNullOrWhiteSpace(exp.Reason))
                    {
                        writer.WriteString(exp.Reason);
                    }
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }

            writer.WriteEndElement(); // </task-scope>
            writer.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    public string ToHumanString()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Task Scope Verification: {TaskId} (Iteration: {IterationId})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Result: {(IsValid ? "VALID" : "VIOLATION")} ({InScopePaths.Count} in-scope, {OutOfScopePaths.Count} out-of-scope)");

        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"In-Scope Paths ({InScopePaths.Count}):");
        if (InScopePaths.Count == 0)
        {
            sb.AppendLine("  (None)");
        }
        else
        {
            foreach (var path in InScopePaths)
            {
                var exp = Explanations.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.Ordinal));
                var matchInfo = exp?.MatchedRule != null ? $" [matched: {exp.MatchedRule}]" : string.Empty;
                sb.AppendLine(CultureInfo.InvariantCulture, $"  + {path}{matchInfo}");
            }
        }

        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Out-of-Scope Paths ({OutOfScopePaths.Count}):");
        if (OutOfScopePaths.Count == 0)
        {
            sb.AppendLine("  (None)");
        }
        else
        {
            foreach (var path in OutOfScopePaths)
            {
                var exp = Explanations.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.Ordinal));
                var reasonInfo = exp?.Reason != null ? $" ({exp.Reason})" : string.Empty;
                sb.AppendLine(CultureInfo.InvariantCulture, $"  ! {path}{reasonInfo}");
            }
        }

        return sb.ToString();
    }

    public string Format(OutputFormat format) =>
        format == OutputFormat.Xml ? ToXmlString() : ToHumanString();
}
