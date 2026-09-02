using System.Globalization;
using System.Text;
using System.Xml;
using DogdouSpec.Core.Formatting;

namespace DogdouSpec.Core.Tasks;

public sealed class TaskSummaryResult
{
    public string IterationId { get; }
    public int TasksRevision { get; }
    public int Total { get; }
    public int Pending { get; }
    public int InProgress { get; }
    public int Verification { get; }
    public int Done { get; }
    public int Blocked { get; }
    public int Transferred { get; }
    public int Superseded { get; }
    public int Cancelled { get; }

    public TaskSummaryResult(
        string iterationId,
        int tasksRevision,
        int total,
        int pending,
        int inProgress,
        int verification,
        int done,
        int blocked,
        int transferred,
        int superseded,
        int cancelled)
    {
        IterationId = iterationId;
        TasksRevision = tasksRevision;
        Total = total;
        Pending = pending;
        InProgress = inProgress;
        Verification = verification;
        Done = done;
        Blocked = blocked;
        Transferred = transferred;
        Superseded = superseded;
        Cancelled = cancelled;
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
            writer.WriteStartElement("task-summary");
            writer.WriteAttributeString("iteration", IterationId);
            writer.WriteAttributeString("tasks_revision", TasksRevision.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("total", Total.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("pending", Pending.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("in_progress", InProgress.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("verification", Verification.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("done", Done.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("blocked", Blocked.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("transferred", Transferred.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("superseded", Superseded.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("cancelled", Cancelled.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    public string ToHumanString()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Task Summary for iteration '{IterationId}' (revision {TasksRevision}):");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Total tasks:    {Total}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  - Done:         {Done}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  - In-Progress:  {InProgress}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  - Verification: {Verification}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  - Pending:      {Pending}");
        if (Blocked > 0) sb.AppendLine(CultureInfo.InvariantCulture, $"  - Blocked:      {Blocked}");
        if (Transferred > 0) sb.AppendLine(CultureInfo.InvariantCulture, $"  - Transferred:  {Transferred}");
        if (Superseded > 0) sb.AppendLine(CultureInfo.InvariantCulture, $"  - Superseded:   {Superseded}");
        if (Cancelled > 0) sb.AppendLine(CultureInfo.InvariantCulture, $"  - Cancelled:    {Cancelled}");

        var pct = Total > 0 ? (Done * 100.0 / Total) : 0.0;
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Completion:     {pct:F1}%");

        return sb.ToString();
    }

    public string Format(OutputFormat format) =>
        format == OutputFormat.Xml ? ToXmlString() : ToHumanString();
}
