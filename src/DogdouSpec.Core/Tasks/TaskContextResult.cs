using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DogdouSpec.Core.Formatting;

namespace DogdouSpec.Core.Tasks;

public sealed record TaskContextRequirement(
    string Id,
    string Status,
    string Summary,
    string Statement,
    string? Rationale,
    IReadOnlyList<(string Id, string Text)> KeyPoints);

public sealed record TaskContextDependency(
    string TargetId,
    string Status,
    string Title,
    bool Satisfied);

public sealed class TaskContextResult
{
    public string IterationId { get; init; } = string.Empty;
    public string TaskId { get; init; } = string.Empty;
    public string TaskStatus { get; init; } = string.Empty;
    public string? Agent { get; init; }
    public int SpecRevision { get; init; }
    public int TasksRevision { get; init; }
    public int MaxBytes { get; init; }
    public int TotalBytes { get; set; }
    public bool Truncated { get; set; }
    public int OmittedRecords { get; set; }
    public string QueryLocator { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;
    public string Objective { get; init; } = string.Empty;
    public string Rationale { get; init; } = string.Empty;
    public XElement? ScopeElement { get; init; }
    public IReadOnlyList<(string Id, string Text)> Constraints { get; init; } = Array.Empty<(string, string)>();
    public IReadOnlyList<TaskContextRequirement> OriginRequirements { get; init; } = Array.Empty<TaskContextRequirement>();
    public IReadOnlyList<(string Id, string Status, string Text)> AcceptanceCriteria { get; init; } = Array.Empty<(string, string, string)>();
    public IReadOnlyList<TaskBlockerItem> ActiveBlockers { get; init; } = Array.Empty<TaskBlockerItem>();
    public IReadOnlyList<XElement> LatestStatusRecords { get; init; } = Array.Empty<XElement>();

    public string ActionCategory { get; init; } = string.Empty;
    public string ReasonCode { get; init; } = string.Empty;
    public string RecommendedAction { get; init; } = string.Empty;
    public IReadOnlyList<string> PermittedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ProhibitedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AuthorityBoundaries { get; init; } = Array.Empty<string>();

    public IReadOnlyList<TaskContextDependency> Dependencies { get; init; } = Array.Empty<TaskContextDependency>();

    public int TotalSupplementalRecords { get; init; }
    public IReadOnlyList<XElement> IncludedSupplementalRecords { get; set; } = Array.Empty<XElement>();

    public IReadOnlyList<(string Path, int Revision)> SourceDocuments { get; init; } = Array.Empty<(string, int)>();

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
            writer.WriteStartElement("task-context");
            writer.WriteAttributeString("iteration", IterationId);
            writer.WriteAttributeString("task", TaskId);
            writer.WriteAttributeString("status", TaskStatus);
            if (!string.IsNullOrEmpty(Agent))
            {
                writer.WriteAttributeString("agent", Agent);
            }
            writer.WriteAttributeString("spec_revision", SpecRevision.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("tasks_revision", TasksRevision.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("max_bytes", MaxBytes.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("total_bytes", TotalBytes.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("truncated", Truncated ? "true" : "false");
            writer.WriteAttributeString("omitted_records", OmittedRecords.ToString(CultureInfo.InvariantCulture));

            // Essential
            writer.WriteStartElement("essential");
            writer.WriteElementString("title", Title);
            writer.WriteElementString("objective", Objective);
            writer.WriteElementString("rationale", Rationale);

            if (ScopeElement != null)
            {
                ScopeElement.WriteTo(writer);
            }
            else
            {
                writer.WriteStartElement("scope");
                writer.WriteEndElement();
            }

            if (Constraints.Count > 0)
            {
                writer.WriteStartElement("constraints");
                foreach (var c in Constraints)
                {
                    writer.WriteStartElement("constraint");
                    writer.WriteAttributeString("id", c.Id);
                    writer.WriteString(c.Text);
                    writer.WriteEndElement();
                }
                writer.WriteEndElement(); // </constraints>
            }

            if (OriginRequirements.Count > 0)
            {
                writer.WriteStartElement("origin-requirements");
                foreach (var req in OriginRequirements)
                {
                    writer.WriteStartElement("requirement");
                    writer.WriteAttributeString("id", req.Id);
                    writer.WriteAttributeString("status", req.Status);
                    writer.WriteElementString("summary", req.Summary);
                    writer.WriteElementString("statement", req.Statement);
                    if (!string.IsNullOrEmpty(req.Rationale))
                    {
                        writer.WriteElementString("rationale", req.Rationale);
                    }
                    if (req.KeyPoints.Count > 0)
                    {
                        writer.WriteStartElement("key_points");
                        foreach (var kp in req.KeyPoints)
                        {
                            writer.WriteStartElement("point");
                            writer.WriteAttributeString("id", kp.Id);
                            writer.WriteString(kp.Text);
                            writer.WriteEndElement();
                        }
                        writer.WriteEndElement(); // </key_points>
                    }
                    writer.WriteEndElement(); // </requirement>
                }
                writer.WriteEndElement(); // </origin-requirements>
            }

            if (AcceptanceCriteria.Count > 0)
            {
                writer.WriteStartElement("acceptance");
                foreach (var ac in AcceptanceCriteria)
                {
                    writer.WriteStartElement("criterion");
                    writer.WriteAttributeString("id", ac.Id);
                    writer.WriteAttributeString("status", ac.Status);
                    writer.WriteString(ac.Text);
                    writer.WriteEndElement();
                }
                writer.WriteEndElement(); // </acceptance>
            }

            writer.WriteStartElement("blockers");
            writer.WriteAttributeString("total", ActiveBlockers.Count.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("due_count", ActiveBlockers.Count(b => b.IsDue).ToString(CultureInfo.InvariantCulture));
            foreach (var b in ActiveBlockers)
            {
                writer.WriteStartElement("blocker");
                if (!string.IsNullOrEmpty(b.FindingId))
                {
                    writer.WriteAttributeString("finding_id", b.FindingId);
                }
                writer.WriteAttributeString("kind", b.Kind);
                writer.WriteAttributeString("owner", b.Owner);
                writer.WriteAttributeString("review_at", b.ReviewAt);
                writer.WriteAttributeString("due_status", b.DueStatus);
                writer.WriteAttributeString("is_due", b.IsDue ? "true" : "false");
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

            if (LatestStatusRecords.Count > 0)
            {
                writer.WriteStartElement("latest-status-records");
                foreach (var rec in LatestStatusRecords)
                {
                    rec.WriteTo(writer);
                }
                writer.WriteEndElement(); // </latest-status-records>
            }

            writer.WriteStartElement("progression-facts");
            writer.WriteElementString("action-category", ActionCategory);
            writer.WriteElementString("reason-code", ReasonCode);
            writer.WriteElementString("recommended-action", RecommendedAction);

            if (PermittedActions.Count > 0)
            {
                writer.WriteStartElement("permitted-actions");
                foreach (var act in PermittedActions)
                {
                    writer.WriteElementString("action", act);
                }
                writer.WriteEndElement();
            }

            if (ProhibitedActions.Count > 0)
            {
                writer.WriteStartElement("prohibited-actions");
                foreach (var act in ProhibitedActions)
                {
                    writer.WriteElementString("action", act);
                }
                writer.WriteEndElement();
            }

            if (AuthorityBoundaries.Count > 0)
            {
                writer.WriteStartElement("authority-boundaries");
                foreach (var auth in AuthorityBoundaries)
                {
                    writer.WriteElementString("boundary", auth);
                }
                writer.WriteEndElement();
            }

            writer.WriteEndElement(); // </progression-facts>
            writer.WriteEndElement(); // </essential>

            // Dependencies
            if (Dependencies.Count > 0)
            {
                writer.WriteStartElement("dependencies");
                foreach (var dep in Dependencies)
                {
                    writer.WriteStartElement("dependency");
                    writer.WriteAttributeString("target", dep.TargetId);
                    writer.WriteAttributeString("status", dep.Status);
                    writer.WriteAttributeString("title", dep.Title);
                    writer.WriteAttributeString("satisfied", dep.Satisfied ? "true" : "false");
                    writer.WriteEndElement();
                }
                writer.WriteEndElement(); // </dependencies>
            }

            // Supplemental
            writer.WriteStartElement("supplemental");
            writer.WriteAttributeString("truncated", Truncated ? "true" : "false");
            writer.WriteAttributeString("total_records", TotalSupplementalRecords.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("included_records", IncludedSupplementalRecords.Count.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("omitted_records", OmittedRecords.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(QueryLocator))
            {
                writer.WriteAttributeString("query_locator", QueryLocator);
            }

            if (IncludedSupplementalRecords.Count > 0)
            {
                writer.WriteStartElement("records");
                foreach (var rec in IncludedSupplementalRecords)
                {
                    rec.WriteTo(writer);
                }
                writer.WriteEndElement(); // </records>
            }
            writer.WriteEndElement(); // </supplemental>

            // Sources
            writer.WriteStartElement("sources");
            foreach (var s in SourceDocuments)
            {
                writer.WriteStartElement("document");
                writer.WriteAttributeString("path", s.Path);
                writer.WriteAttributeString("revision", s.Revision.ToString(CultureInfo.InvariantCulture));
                writer.WriteEndElement();
            }
            writer.WriteEndElement(); // </sources>

            writer.WriteEndElement(); // </task-context>
            writer.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public string ToHumanString()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Task Recovery Context: {TaskId} (Iteration: {IterationId})");
        var agentStr = string.IsNullOrWhiteSpace(Agent) ? string.Empty : $" | Agent: {Agent}";
        sb.AppendLine(CultureInfo.InvariantCulture, $"Status: {TaskStatus.ToUpperInvariant()}{agentStr}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Revisions: tasks.xml (rev {TasksRevision}), spec.xml (rev {SpecRevision})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Size: {TotalBytes:N0} / {MaxBytes:N0} bytes (Truncated: {Truncated})");
        sb.AppendLine();

        sb.AppendLine("=== Essential Context ===");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Title: {Title}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Objective: {Objective}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Rationale: {Rationale}");
        sb.AppendLine();

        if (ScopeElement != null)
        {
            sb.AppendLine("Scope:");
            var repos = ScopeElement.Elements("repository").ToList();
            if (repos.Count == 0)
            {
                sb.AppendLine("  (None)");
            }
            else
            {
                foreach (var repo in repos)
                {
                    var repoPath = (string?)repo.Attribute("path") ?? ".";
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  Repository: {repoPath}");
                    var includes = repo.Elements("include").Select(i => (string?)i.Attribute("path")).Where(p => !string.IsNullOrEmpty(p)).ToList();
                    var excludes = repo.Elements("exclude").Select(e => (string?)e.Attribute("path")).Where(p => !string.IsNullOrEmpty(p)).ToList();

                    if (includes.Count > 0)
                    {
                        sb.AppendLine("    Includes:");
                        foreach (var inc in includes)
                        {
                            sb.AppendLine(CultureInfo.InvariantCulture, $"      + {inc}");
                        }
                    }

                    if (excludes.Count > 0)
                    {
                        sb.AppendLine("    Excludes:");
                        foreach (var exc in excludes)
                        {
                            sb.AppendLine(CultureInfo.InvariantCulture, $"      - {exc}");
                        }
                    }

                    if (includes.Count == 0 && excludes.Count == 0)
                    {
                        sb.AppendLine("    (No include/exclude filters specified)");
                    }
                }
            }
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("Scope: None");
            sb.AppendLine();
        }

        if (Constraints.Count > 0)
        {
            sb.AppendLine("Constraints:");
            foreach (var c in Constraints)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  - [{c.Id}] {c.Text}");
            }
            sb.AppendLine();
        }

        if (OriginRequirements.Count > 0)
        {
            sb.AppendLine("Origin Requirements:");
            foreach (var req in OriginRequirements)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  - [{req.Id}] ({req.Status}) {req.Summary}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    Statement: {req.Statement}");
                if (req.KeyPoints.Count > 0)
                {
                    sb.AppendLine("    Key Points:");
                    foreach (var kp in req.KeyPoints)
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"      * [{kp.Id}] {kp.Text}");
                    }
                }
            }
            sb.AppendLine();
        }

        if (AcceptanceCriteria.Count > 0)
        {
            sb.AppendLine("Acceptance Criteria:");
            foreach (var ac in AcceptanceCriteria)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  - [{ac.Id}] ({ac.Status}) {ac.Text}");
            }
            sb.AppendLine();
        }

        if (ActiveBlockers.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Active Blockers & Findings ({ActiveBlockers.Count}):");
            foreach (var b in ActiveBlockers)
            {
                var dueBadge = b.IsDue ? "[OVERDUE]" : $"[{b.DueStatus.ToUpperInvariant()}]";
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {dueBadge} {b.Summary} (kind: {b.Kind}, owner: {b.Owner}, review: {b.ReviewAt})");
                if (!string.IsNullOrWhiteSpace(b.Condition))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    Condition: {b.Condition}");
                }
                if (!string.IsNullOrWhiteSpace(b.NextAction))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    Next Action: {b.NextAction}");
                }
            }
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("Active Blockers & Findings: None");
            sb.AppendLine();
        }

        if (LatestStatusRecords.Count > 0)
        {
            sb.AppendLine("Latest Status Records:");
            foreach (var rec in LatestStatusRecords)
            {
                var kind = (string?)rec.Attribute("kind") ?? "record";
                var actor = (string?)rec.Attribute("actor") ?? "unknown";
                var createdAt = (string?)rec.Attribute("created_at") ?? string.Empty;
                var summary = rec.Element("summary")?.Value ?? string.Empty;
                sb.AppendLine(CultureInfo.InvariantCulture, $"  - [{kind.ToUpperInvariant()}] ({actor} @ {createdAt}): {summary}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("Progression Assessment:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Category: {ActionCategory} | Reason Code: {ReasonCode}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Recommended Action: {RecommendedAction}");
        if (PermittedActions.Count > 0)
        {
            sb.AppendLine("  Permitted Actions:");
            foreach (var act in PermittedActions)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    * {act}");
            }
        }
        if (ProhibitedActions.Count > 0)
        {
            sb.AppendLine("  Prohibited Actions:");
            foreach (var act in ProhibitedActions)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    * {act}");
            }
        }
        if (AuthorityBoundaries.Count > 0)
        {
            sb.AppendLine("  Authority Boundaries:");
            foreach (var auth in AuthorityBoundaries)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    * {auth}");
            }
        }
        sb.AppendLine();

        if (Dependencies.Count > 0)
        {
            sb.AppendLine("=== Upstream Dependencies ===");
            foreach (var dep in Dependencies)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  - [{dep.Status.ToUpperInvariant()}] {dep.TargetId}: {dep.Title} (Satisfied: {dep.Satisfied})");
            }
            sb.AppendLine();
        }

        sb.AppendLine("=== Supplemental History ===");
        sb.AppendLine(CultureInfo.InvariantCulture, $"({IncludedSupplementalRecords.Count} records included, {OmittedRecords} omitted)");
        if (!string.IsNullOrEmpty(QueryLocator))
        {
            sb.AppendLine("Query locator for full history:");
            sb.AppendLine(QueryLocator);
        }

        return sb.ToString();
    }

    public string Format(OutputFormat format) =>
        format == OutputFormat.Xml ? ToXmlString() : ToHumanString();
}
