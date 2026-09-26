using System.Globalization;
using System.Text;
using System.Xml;
using DogdouSpec.Core.Formatting;

namespace DogdouSpec.Core.Tasks;

public sealed record TaskBlockerItem(
    string TaskId,
    string TaskTitle,
    string TaskStatus,
    string? FindingId,
    string Kind,
    string Owner,
    string ReviewAt,
    DateTime? ReviewAtUtc,
    string DueStatus,
    bool IsDue,
    string Summary,
    string? Condition,
    string? NextAction,
    string? CreatedAt,
    bool IsDerived,
    int OriginalIndex,
    bool IsRecordOnly = false);

public sealed class TaskBlockersResult
{
    public string IterationId { get; }
    public int TasksRevision { get; }
    public IReadOnlyList<TaskBlockerItem> Blockers { get; }
    public int TotalCount => Blockers.Count;
    public int DueCount => Blockers.Count(b => b.IsDue);

    public TaskBlockersResult(
        string iterationId,
        int tasksRevision,
        IReadOnlyList<TaskBlockerItem> blockers)
    {
        IterationId = iterationId ?? string.Empty;
        TasksRevision = tasksRevision;
        Blockers = blockers ?? Array.Empty<TaskBlockerItem>();
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
            writer.WriteStartElement("blockers");
            writer.WriteAttributeString("iteration", IterationId);
            writer.WriteAttributeString("tasks_revision", TasksRevision.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("total", TotalCount.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("due_count", DueCount.ToString(CultureInfo.InvariantCulture));

            foreach (var b in Blockers)
            {
                writer.WriteStartElement("blocker");
                writer.WriteAttributeString("task", b.TaskId);
                writer.WriteAttributeString("task_title", b.TaskTitle);
                writer.WriteAttributeString("task_status", b.TaskStatus);
                if (!string.IsNullOrEmpty(b.FindingId))
                {
                    writer.WriteAttributeString("finding_id", b.FindingId);
                }
                writer.WriteAttributeString("kind", b.Kind);
                writer.WriteAttributeString("owner", b.Owner);
                writer.WriteAttributeString("review_at", b.ReviewAt);
                writer.WriteAttributeString("due_status", b.DueStatus);
                writer.WriteAttributeString("is_due", b.IsDue ? "true" : "false");
                writer.WriteAttributeString("derived", b.IsDerived ? "true" : "false");
                writer.WriteAttributeString("record_only", b.IsRecordOnly ? "true" : "false");

                if (!string.IsNullOrWhiteSpace(b.CreatedAt))
                {
                    writer.WriteAttributeString("created_at", b.CreatedAt);
                }

                writer.WriteElementString("summary", b.Summary);
                if (!string.IsNullOrWhiteSpace(b.Condition))
                {
                    writer.WriteElementString("condition", b.Condition);
                }
                if (!string.IsNullOrWhiteSpace(b.NextAction))
                {
                    writer.WriteElementString("next_action", b.NextAction);
                }

                writer.WriteEndElement(); // </blocker>
            }

            writer.WriteEndElement(); // </blockers>
            writer.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public string ToHumanString()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Iteration: {IterationId} (revision {TasksRevision})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Total Blockers: {TotalCount} (Due/Overdue: {DueCount})");
        sb.AppendLine();

        if (Blockers.Count == 0)
        {
            sb.AppendLine("  (No active blockers found matching criteria)");
            return sb.ToString();
        }

        foreach (var b in Blockers)
        {
            var badge = b.IsDue ? "[OVERDUE]" : (b.ReviewAt != "none" ? "[UPCOMING]" : "[UNDATED]");
            var modeStr = b.IsRecordOnly ? " [record-only]" : string.Empty;
            var sourceStr = b.IsDerived ? "Derived Dependency" : $"Finding: {b.FindingId}{modeStr}";
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {badge,-10} Task: {b.TaskId} ({b.TaskStatus}) - {sourceStr}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    Kind: {b.Kind} | Owner: {b.Owner} | Review At: {b.ReviewAt} ({b.DueStatus})");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    Summary: {b.Summary}");
            if (!string.IsNullOrWhiteSpace(b.Condition))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    Condition: {b.Condition}");
            }
            if (!string.IsNullOrWhiteSpace(b.NextAction))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    Next Action: {b.NextAction}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public string Format(OutputFormat format) =>
        format == OutputFormat.Xml ? ToXmlString() : ToHumanString();
}
