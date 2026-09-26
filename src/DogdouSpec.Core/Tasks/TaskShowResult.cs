using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DogdouSpec.Core.Formatting;

namespace DogdouSpec.Core.Tasks;

public sealed class TaskShowResult
{
    public string IterationId { get; }
    public string TaskId { get; }
    public XElement TaskElement { get; }

    public TaskShowResult(
        string iterationId,
        string taskId,
        XElement taskElement)
    {
        IterationId = iterationId;
        TaskId = taskId;
        TaskElement = taskElement;
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
            TaskElement.WriteTo(writer);
            writer.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    public string ToHumanString()
    {
        var sb = new StringBuilder();
        var status = TaskElement.Attribute("status")?.Value ?? "unknown";
        var agent = TaskElement.Attribute("agent")?.Value;
        var createdAt = TaskElement.Attribute("created_at")?.Value;
        var updatedAt = TaskElement.Attribute("updated_at")?.Value;
        var startedAt = TaskElement.Attribute("started_at")?.Value;
        var completedAt = TaskElement.Attribute("completed_at")?.Value;

        sb.AppendLine(CultureInfo.InvariantCulture, $"Task: {TaskId} (Iteration: {IterationId})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Status: {status.ToUpperInvariant()}");
        if (!string.IsNullOrWhiteSpace(agent))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Agent: {agent}");
        }
        if (!string.IsNullOrWhiteSpace(createdAt)) sb.AppendLine(CultureInfo.InvariantCulture, $"Created At: {createdAt}");
        if (!string.IsNullOrWhiteSpace(startedAt)) sb.AppendLine(CultureInfo.InvariantCulture, $"Started At: {startedAt}");
        if (!string.IsNullOrWhiteSpace(completedAt)) sb.AppendLine(CultureInfo.InvariantCulture, $"Completed At: {completedAt}");
        if (!string.IsNullOrWhiteSpace(updatedAt)) sb.AppendLine(CultureInfo.InvariantCulture, $"Updated At: {updatedAt}");

        var title = TaskElement.Element("title")?.Value;
        if (!string.IsNullOrWhiteSpace(title))
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Title: {title}");
        }

        var objective = TaskElement.Element("objective")?.Value;
        if (!string.IsNullOrWhiteSpace(objective))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Objective: {objective}");
        }

        var rationale = TaskElement.Element("rationale")?.Value;
        if (!string.IsNullOrWhiteSpace(rationale))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Rationale: {rationale}");
        }

        // Scope
        var scopeRepo = TaskElement.Element("scope")?.Element("repository");
        if (scopeRepo != null)
        {
            sb.AppendLine();
            sb.AppendLine("Scope:");
            var repoPath = scopeRepo.Attribute("path")?.Value ?? ".";
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Repository Root: {repoPath}");
            var includes = scopeRepo.Elements("include").Select(i => i.Attribute("path")?.Value).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            if (includes.Count > 0)
            {
                sb.AppendLine("  Includes:");
                foreach (var inc in includes) sb.AppendLine(CultureInfo.InvariantCulture, $"    + {inc}");
            }
            var excludes = scopeRepo.Elements("exclude").Select(e => e.Attribute("path")?.Value).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            if (excludes.Count > 0)
            {
                sb.AppendLine("  Excludes:");
                foreach (var exc in excludes) sb.AppendLine(CultureInfo.InvariantCulture, $"    - {exc}");
            }
        }

        // Acceptance Criteria
        var criteria = TaskElement.Element("acceptance")?.Elements("criterion").ToList();
        if (criteria is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Acceptance Criteria:");
            foreach (var crit in criteria)
            {
                var cId = crit.Attribute("id")?.Value ?? string.Empty;
                var cStatus = crit.Attribute("status")?.Value ?? crit.Attribute("result")?.Value ?? "pending";
                sb.AppendLine(CultureInfo.InvariantCulture, $"  [{cStatus.ToUpperInvariant()}] {cId}: {crit.Value.Trim()}");
            }
        }

        // Dependencies
        var deps = TaskElement.Element("dependencies")?.Elements("ref").ToList();
        if (deps is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Dependencies:");
            foreach (var dep in deps)
            {
                var target = dep.Attribute("target")?.Value;
                var rel = dep.Attribute("relation")?.Value ?? "depends-on";
                sb.AppendLine(CultureInfo.InvariantCulture, $"  - {target} ({rel})");
            }
        }

        // Records
        var records = TaskElement.Element("records")?.Elements("record").ToList();
        if (records is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Records ({records.Count}):");
            foreach (var rec in records)
            {
                var rId = rec.Attribute("id")?.Value;
                var rKind = rec.Attribute("kind")?.Value ?? "record";
                var rStatus = rec.Attribute("status")?.Value ?? "informational";
                var rActor = rec.Attribute("actor")?.Value ?? "unknown";
                var rTime = rec.Attribute("created_at")?.Value;
                var rSum = rec.Element("summary")?.Value ?? rec.Attribute("summary")?.Value ?? string.Empty;
                sb.AppendLine(CultureInfo.InvariantCulture, $"  [{rKind.ToUpperInvariant()}] {rId} by {rActor} at {rTime} ({rStatus})");
                if (!string.IsNullOrWhiteSpace(rSum))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    Summary: {rSum}");
                }
            }
        }

        return sb.ToString();
    }

    public string Format(OutputFormat format) =>
        format == OutputFormat.Xml ? ToXmlString() : ToHumanString();
}
