using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Validation;

namespace DogdouSpec.Core.Tasks;

/// <summary>
/// Result container for read-only actionable task selection.
/// </summary>
public sealed class TaskNextResult
{
    public string IterationId { get; }
    public int TasksRevision { get; }
    public bool HasTask => Task != null;
    public ParsedTask? Task { get; }
    public string Reason { get; }

    public TaskNextResult(
        string iterationId,
        int tasksRevision,
        ParsedTask? task,
        string reason)
    {
        IterationId = iterationId ?? string.Empty;
        TasksRevision = tasksRevision;
        Task = task;
        Reason = reason ?? string.Empty;
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
            writer.WriteStartElement("task-next");
            writer.WriteAttributeString("iteration", IterationId);
            writer.WriteAttributeString("tasks_revision", TasksRevision.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("found", HasTask ? "true" : "false");

            if (Task != null)
            {
                writer.WriteStartElement("task");
                writer.WriteAttributeString("id", Task.Id);
                writer.WriteAttributeString("status", Task.Status);

                var agent = Task.Element.Attribute("agent")?.Value;
                if (!string.IsNullOrEmpty(agent))
                {
                    writer.WriteAttributeString("agent", agent);
                }

                var title = Task.Element.Element("title")?.Value;
                if (!string.IsNullOrEmpty(title))
                {
                    writer.WriteElementString("title", title);
                }

                var objective = Task.Element.Element("objective")?.Value;
                if (!string.IsNullOrEmpty(objective))
                {
                    writer.WriteElementString("objective", objective);
                }

                var indexElem = Task.Element.Element("index");
                if (indexElem != null)
                {
                    indexElem.WriteTo(writer);
                }

                writer.WriteEndElement(); // </task>
            }
            else
            {
                writer.WriteElementString("summary", Reason);
            }

            writer.WriteEndElement(); // </task-next>
            writer.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    public string ToHumanString()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Iteration: {IterationId} (Tasks Revision: {TasksRevision})");

        if (Task != null)
        {
            var agentStr = Task.Element.Attribute("agent")?.Value;
            var agentSuffix = !string.IsNullOrEmpty(agentStr) ? $", Agent: {agentStr}" : string.Empty;
            sb.AppendLine(CultureInfo.InvariantCulture, $"Selected Task: {Task.Id} (Status: {Task.Status}{agentSuffix})");

            var title = Task.Element.Element("title")?.Value;
            if (!string.IsNullOrEmpty(title))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"Title: {title}");
            }

            var objective = Task.Element.Element("objective")?.Value;
            if (!string.IsNullOrEmpty(objective))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"Objective: {objective}");
            }

            var summary = Task.Element.Element("index")?.Element("summary")?.Value;
            if (!string.IsNullOrEmpty(summary))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"Summary: {summary}");
            }
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"No actionable task found ({Reason}).");
        }

        return sb.ToString();
    }

    public string Format(OutputFormat format) =>
        format == OutputFormat.Xml ? ToXmlString() : ToHumanString();
}
