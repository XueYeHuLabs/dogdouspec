using System.Globalization;
using System.Text;
using System.Xml;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Security;

namespace DogdouSpec.Core.Iterations;

/// <summary>
/// Result container for iteration list command.
/// </summary>
public sealed class IterationListResult
{
    public string WorkspaceRoot { get; }
    public IReadOnlyList<IterationSummary> Iterations { get; }

    public IterationListResult(string workspaceRoot, IReadOnlyList<IterationSummary> iterations)
    {
        WorkspaceRoot = workspaceRoot ?? string.Empty;
        Iterations = iterations ?? Array.Empty<IterationSummary>();
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
            writer.WriteStartElement("iterations");
            writer.WriteAttributeString("workspace", PathSecurity.NormalizeSeparators(WorkspaceRoot));

            foreach (var iter in Iterations)
            {
                writer.WriteStartElement("iteration");
                writer.WriteAttributeString("id", iter.Id);
                writer.WriteAttributeString("path", iter.RelativePath);
                writer.WriteAttributeString("kind", iter.Kind);
                writer.WriteAttributeString("status", iter.Status);
                writer.WriteAttributeString("spec_revision", iter.SpecRevision.ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("tasks_revision", iter.TasksRevision.ToString(CultureInfo.InvariantCulture));

                if (iter.IndexElement != null)
                {
                    iter.IndexElement.WriteTo(writer);
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    public string ToHumanString()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Iterations in workspace: {PathSecurity.NormalizeSeparators(WorkspaceRoot)}");
        if (Iterations.Count == 0)
        {
            sb.AppendLine("(No iterations found)");
            return sb.ToString();
        }

        foreach (var iter in Iterations)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {iter.Id} ({iter.Kind}, status: {iter.Status}, spec rev: {iter.SpecRevision}, tasks rev: {iter.TasksRevision})");
            var summary = iter.IndexElement?.Element("summary")?.Value;
            if (!string.IsNullOrWhiteSpace(summary))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Summary: {summary.Trim()}");
            }
        }
        return sb.ToString();
    }

    public string Format(OutputFormat format) =>
        format == OutputFormat.Xml ? ToXmlString() : ToHumanString();
}
