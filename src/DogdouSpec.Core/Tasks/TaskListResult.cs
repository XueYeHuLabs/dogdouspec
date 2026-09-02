using System.Globalization;
using System.Text;
using System.Xml;
using DogdouSpec.Core.Formatting;

namespace DogdouSpec.Core.Tasks;

public sealed record TaskListItem(
    string Id,
    string Status,
    string Title,
    string? Summary = null,
    string? Agent = null,
    IReadOnlyList<string>? Dependencies = null,
    string? StartedAt = null,
    string? CompletedAt = null,
    string? UpdatedAt = null);

public sealed class TaskListResult
{
    public string IterationId { get; }
    public int TasksRevision { get; }
    public IReadOnlyList<TaskListItem> Tasks { get; }
    public string? FilterStatus { get; }

    public TaskListResult(
        string iterationId,
        int tasksRevision,
        IReadOnlyList<TaskListItem> tasks,
        string? filterStatus = null)
    {
        IterationId = iterationId;
        TasksRevision = tasksRevision;
        Tasks = tasks ?? Array.Empty<TaskListItem>();
        FilterStatus = filterStatus;
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
            writer.WriteStartElement("tasks");
            writer.WriteAttributeString("iteration", IterationId);
            writer.WriteAttributeString("tasks_revision", TasksRevision.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("count", Tasks.Count.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(FilterStatus))
            {
                writer.WriteAttributeString("filter_status", FilterStatus);
            }

            foreach (var task in Tasks)
            {
                writer.WriteStartElement("task");
                writer.WriteAttributeString("id", task.Id);
                writer.WriteAttributeString("status", task.Status);
                if (!string.IsNullOrWhiteSpace(task.Agent))
                {
                    writer.WriteAttributeString("agent", task.Agent);
                }
                if (!string.IsNullOrWhiteSpace(task.StartedAt))
                {
                    writer.WriteAttributeString("started_at", task.StartedAt);
                }
                if (!string.IsNullOrWhiteSpace(task.CompletedAt))
                {
                    writer.WriteAttributeString("completed_at", task.CompletedAt);
                }
                if (!string.IsNullOrWhiteSpace(task.UpdatedAt))
                {
                    writer.WriteAttributeString("updated_at", task.UpdatedAt);
                }

                writer.WriteElementString("title", task.Title);
                if (!string.IsNullOrWhiteSpace(task.Summary))
                {
                    writer.WriteElementString("summary", task.Summary);
                }

                if (task.Dependencies is { Count: > 0 })
                {
                    writer.WriteStartElement("dependencies");
                    foreach (var dep in task.Dependencies)
                    {
                        writer.WriteStartElement("ref");
                        writer.WriteAttributeString("target", dep);
                        writer.WriteAttributeString("relation", "depends-on");
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }

                writer.WriteEndElement(); // </task>
            }

            writer.WriteEndElement(); // </tasks>
            writer.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    public string ToHumanString()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Tasks in iteration '{IterationId}' (revision {TasksRevision}, total: {Tasks.Count}):");
        if (!string.IsNullOrWhiteSpace(FilterStatus))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Filter status: {FilterStatus}");
        }
        sb.AppendLine();

        if (Tasks.Count == 0)
        {
            sb.AppendLine("  (No tasks found matching criteria)");
            return sb.ToString();
        }

        foreach (var task in Tasks)
        {
            var statusBadge = $"[{task.Status.ToUpperInvariant()}]";
            var agentStr = string.IsNullOrWhiteSpace(task.Agent) ? string.Empty : $" (agent: {task.Agent})";
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {statusBadge,-16} {task.Id}{agentStr}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    Title: {task.Title}");
            if (!string.IsNullOrWhiteSpace(task.Summary) && !string.Equals(task.Summary, task.Title, StringComparison.Ordinal))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    Summary: {task.Summary}");
            }
            if (task.Dependencies is { Count: > 0 })
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    Depends on: {string.Join(", ", task.Dependencies)}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public string Format(OutputFormat format) =>
        format == OutputFormat.Xml ? ToXmlString() : ToHumanString();
}
