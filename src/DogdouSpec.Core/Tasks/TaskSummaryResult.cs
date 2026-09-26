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

    public int Inactive => Transferred + Superseded + Cancelled;
    public int Eligible => Total - Inactive;
    public double CompletionPercentage => Eligible > 0 ? (Done * 100.0 / Eligible) : 0.0;

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
            writer.WriteAttributeString("eligible", Eligible.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("completion_percentage", CompletionPercentage.ToString("F1", CultureInfo.InvariantCulture));
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
        if (Transferred > 0) sb.AppendLine(CultureInfo.InvariantCulture, $"  - Transferred:  {Transferred} (terminal disposition)");
        if (Superseded > 0) sb.AppendLine(CultureInfo.InvariantCulture, $"  - Superseded:   {Superseded} (terminal disposition)");
        if (Cancelled > 0) sb.AppendLine(CultureInfo.InvariantCulture, $"  - Cancelled:    {Cancelled} (terminal disposition)");

        sb.AppendLine(CultureInfo.InvariantCulture, $"  Eligible tasks: {Eligible}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Completion:     {CompletionPercentage:F1}%");

        return sb.ToString();
    }

    public string Format(OutputFormat format) =>
        format == OutputFormat.Xml ? ToXmlString() : ToHumanString();
}
