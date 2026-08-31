using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using DogdouSpec.Core.Formatting;

namespace DogdouSpec.Core.Reporting;

public sealed class IterationSummaryResult
{
    private const char FilledBlock = '\u2588'; // █
    private const char EmptyBlock = '\u2591';  // ░

    private const string EmojiRocket = "\U0001F680";
    private const string EmojiCheck = "\u2705";
    private const string EmojiCycle = "\U0001F504";
    private const string EmojiHourglass = "\u23F3";
    private const string EmojiCircle = "\u26AA";
    private const string EmojiLock = "\U0001F512";
    private const string EmojiWarning = "\u26A0\uFE0F";
    private const string EmojiBulb = "\U0001F4A1";
    private const string EmojiClipboard = "\U0001F4CB";
    private const string EmojiProhibited = "\U0001F6AB";

    public IterationSummary Summary { get; }

    public IterationSummaryResult(IterationSummary summary)
    {
        Summary = summary ?? throw new ArgumentNullException(nameof(summary));
    }

    public static string RenderProgressBar(double percentage, int barLength = 10)
    {
        var filledCount = (int)Math.Round((percentage / 100.0) * barLength);
        filledCount = Math.Clamp(filledCount, 0, barLength);
        var emptyCount = barLength - filledCount;
        return "[" + new string(FilledBlock, filledCount) + new string(EmptyBlock, emptyCount) + "]";
    }

    public static string RenderAsciiProgressBar(double percentage, int barLength = 10)
    {
        var filledCount = (int)Math.Round((percentage / 100.0) * barLength);
        filledCount = Math.Clamp(filledCount, 0, barLength);
        var emptyCount = barLength - filledCount;
        return "[" + new string('#', filledCount) + new string('-', emptyCount) + "]";
    }

    public string ToMarkdownString()
    {
        var sb = new StringBuilder();
        var s = Summary;

        sb.AppendLine(CultureInfo.InvariantCulture, $"### {EmojiRocket} Iteration Progress: `{s.IterationId}` ({s.Kind})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Status:** `{s.Status}` | **Revisions:** spec: r{s.SpecRevision}, tasks: r{s.TasksRevision}");
        if (!string.IsNullOrWhiteSpace(s.Title))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"**Title:** {s.Title}");
        }
        sb.AppendLine();

        var activeTotal = s.TotalTasks - s.InactiveTasks;
        var bar = RenderProgressBar(s.ProgressPercentage, 10);
        sb.AppendLine("```text");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Progress: {bar} {s.ProgressPercentage:F1}% ({s.DoneTasks}/{activeTotal} tasks completed)");
        sb.AppendLine("```");
        sb.AppendLine();

        sb.AppendLine($"#### {EmojiClipboard} Task Breakdown");

        var doneTasks = s.Tasks.Where(t => string.Equals(t.Status, "done", StringComparison.OrdinalIgnoreCase)).ToList();
        if (doneTasks.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {EmojiCheck} **Done ({doneTasks.Count})**");
            foreach (var task in doneTasks)
            {
                var covers = task.CoveredCriteria.Count > 0
                    ? $" (covers: {string.Join(", ", task.CoveredCriteria.Select(c => $"`{c}`"))})"
                    : string.Empty;
                sb.AppendLine(CultureInfo.InvariantCulture, $"  - `{task.Id}`: {task.Title}{covers}");
            }
        }

        var activeTasks = s.Tasks.Where(t => string.Equals(t.Status, "in-progress", StringComparison.OrdinalIgnoreCase) || string.Equals(t.Status, "verification", StringComparison.OrdinalIgnoreCase)).ToList();
        if (activeTasks.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {EmojiCycle} **In Progress / Verification ({activeTasks.Count})**");
            foreach (var task in activeTasks)
            {
                var agentStr = !string.IsNullOrEmpty(task.Agent) ? $", agent: `{task.Agent}`" : string.Empty;
                sb.AppendLine(CultureInfo.InvariantCulture, $"  - `{task.Id}` (`{task.Status}`{agentStr}): {task.Title}");
            }
        }

        var pendingTasks = s.Tasks.Where(t =>
            string.Equals(t.Status, "pending", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t.Status, "blocked", StringComparison.OrdinalIgnoreCase) ||
            (!IterationSummaryGenerator.KnownActiveStatuses.Contains(t.Status) && !IterationSummaryGenerator.KnownInactiveStatuses.Contains(t.Status))).ToList();
        if (pendingTasks.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {EmojiHourglass} **Pending / Next ({pendingTasks.Count})**");
            foreach (var task in pendingTasks)
            {
                var blockedSuffix = task.IsBlocked ? $" {EmojiWarning} *(Blocked: {task.BlockedReason})*" : string.Empty;
                sb.AppendLine(CultureInfo.InvariantCulture, $"  - `{task.Id}`: {task.Title}{blockedSuffix}");
            }
        }

        var inactiveTasks = s.Tasks.Where(t => IterationSummaryGenerator.KnownInactiveStatuses.Contains(t.Status)).ToList();
        if (inactiveTasks.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {EmojiCircle} **Inactive / Disposed ({inactiveTasks.Count})**");
            foreach (var task in inactiveTasks)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  - `{task.Id}` (`{task.Status}`): {task.Title}");
            }
        }

        if (s.Tasks.Count == 0)
        {
            sb.AppendLine("- *(No tasks declared yet)*");
        }

        sb.AppendLine();
        sb.AppendLine($"#### {EmojiProhibited} Blockers & Pending Gates");

        var hasBlockers = s.Blockers.Count > 0;
        var hasGates = s.PendingGates.Count > 0;

        if (!hasBlockers && !hasGates)
        {
            sb.AppendLine("*No dependency blockers or pending product gates.*");
        }
        else
        {
            if (hasBlockers)
            {
                foreach (var b in s.Blockers)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"- {EmojiLock} **Task Dependency:** `{b.TaskId}` is blocked by `{b.DependencyTaskId}` (status: `{b.DependencyStatus}`)");
                }
            }

            if (hasGates)
            {
                foreach (var g in s.PendingGates)
                {
                    var typeLabel = g.Kind switch
                    {
                        "requirement" => "Proposed Requirement",
                        "decision" => "Proposed Decision",
                        "acceptance" => "Pending Acceptance Criterion",
                        _ => g.Kind
                    };
                    sb.AppendLine(CultureInfo.InvariantCulture, $"- {EmojiWarning} **{typeLabel}:** `{g.Id}` ({g.Title}) requires owner confirmation.");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"> {EmojiBulb} **Next Action:** {s.RecommendedNextAction}");

        return sb.ToString();
    }

    public string ToJsonString()
    {
        var s = Summary;
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("iteration", s.IterationId);
            writer.WriteString("kind", s.Kind);
            writer.WriteString("status", s.Status);
            writer.WriteNumber("spec_revision", s.SpecRevision);
            writer.WriteNumber("tasks_revision", s.TasksRevision);
            writer.WriteString("title", s.Title);
            writer.WriteString("summary", s.Summary);

            writer.WriteStartObject("progress");
            writer.WriteNumber("percentage", s.ProgressPercentage);
            writer.WriteNumber("total", s.TotalTasks);
            writer.WriteNumber("done", s.DoneTasks);
            writer.WriteNumber("in_progress", s.InProgressTasks);
            writer.WriteNumber("verification", s.VerificationTasks);
            writer.WriteNumber("pending", s.PendingTasks);
            writer.WriteNumber("inactive", s.InactiveTasks);
            writer.WriteEndObject();

            writer.WriteString("recommended_next_action", s.RecommendedNextAction);

            writer.WriteStartArray("tasks");
            foreach (var t in s.Tasks)
            {
                writer.WriteStartObject();
                writer.WriteString("id", t.Id);
                writer.WriteString("title", t.Title);
                writer.WriteString("status", t.Status);
                if (!string.IsNullOrEmpty(t.Agent))
                {
                    writer.WriteString("agent", t.Agent);
                }
                else
                {
                    writer.WriteNull("agent");
                }
                writer.WriteBoolean("is_blocked", t.IsBlocked);
                if (!string.IsNullOrEmpty(t.BlockedReason))
                {
                    writer.WriteString("blocked_reason", t.BlockedReason);
                }
                else
                {
                    writer.WriteNull("blocked_reason");
                }

                if (t.CoveredCriteria.Count > 0)
                {
                    writer.WriteStartArray("covered_criteria");
                    foreach (var c in t.CoveredCriteria)
                    {
                        writer.WriteStringValue(c);
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("blockers");
            foreach (var b in s.Blockers)
            {
                writer.WriteStartObject();
                writer.WriteString("task_id", b.TaskId);
                writer.WriteString("task_title", b.TaskTitle);
                writer.WriteString("dependency_id", b.DependencyTaskId);
                writer.WriteString("dependency_status", b.DependencyStatus);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("pending_gates");
            foreach (var g in s.PendingGates)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", g.Kind);
                writer.WriteString("id", g.Id);
                writer.WriteString("title", g.Title);
                writer.WriteString("status", g.Status);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    public string ToXmlString()
    {
        var s = Summary;
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
            writer.WriteStartElement("iteration-summary");
            writer.WriteAttributeString("iteration", s.IterationId);
            writer.WriteAttributeString("kind", s.Kind);
            writer.WriteAttributeString("status", s.Status);
            writer.WriteAttributeString("spec_revision", s.SpecRevision.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("tasks_revision", s.TasksRevision.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("progress_percentage", s.ProgressPercentage.ToString("F1", CultureInfo.InvariantCulture));

            if (!string.IsNullOrEmpty(s.Title))
            {
                writer.WriteElementString("title", s.Title);
            }
            if (!string.IsNullOrEmpty(s.Summary))
            {
                writer.WriteElementString("summary", s.Summary);
            }

            writer.WriteStartElement("progress");
            writer.WriteAttributeString("total", s.TotalTasks.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("done", s.DoneTasks.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("in_progress", s.InProgressTasks.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("verification", s.VerificationTasks.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("pending", s.PendingTasks.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("inactive", s.InactiveTasks.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement(); // </progress>

            writer.WriteStartElement("tasks");
            foreach (var t in s.Tasks)
            {
                writer.WriteStartElement("task");
                writer.WriteAttributeString("id", t.Id);
                writer.WriteAttributeString("status", t.Status);
                if (!string.IsNullOrEmpty(t.Agent)) writer.WriteAttributeString("agent", t.Agent);
                if (t.IsBlocked) writer.WriteAttributeString("blocked", "true");
                writer.WriteElementString("title", t.Title);
                if (t.CoveredCriteria.Count > 0)
                {
                    writer.WriteStartElement("covered-criteria");
                    foreach (var c in t.CoveredCriteria) writer.WriteElementString("criterion", c);
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
            writer.WriteEndElement(); // </tasks>

            if (s.Blockers.Count > 0)
            {
                writer.WriteStartElement("blockers");
                foreach (var b in s.Blockers)
                {
                    writer.WriteStartElement("blocker");
                    writer.WriteAttributeString("task", b.TaskId);
                    writer.WriteAttributeString("dependency", b.DependencyTaskId);
                    writer.WriteAttributeString("dependency_status", b.DependencyStatus);
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }

            if (s.PendingGates.Count > 0)
            {
                writer.WriteStartElement("gates");
                foreach (var g in s.PendingGates)
                {
                    writer.WriteStartElement("gate");
                    writer.WriteAttributeString("kind", g.Kind);
                    writer.WriteAttributeString("id", g.Id);
                    writer.WriteAttributeString("status", g.Status);
                    writer.WriteElementString("title", g.Title);
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }

            writer.WriteElementString("next-action", s.RecommendedNextAction);

            writer.WriteEndElement(); // </iteration-summary>
            writer.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    public string ToHumanString()
    {
        var sb = new StringBuilder();
        var s = Summary;

        sb.AppendLine(CultureInfo.InvariantCulture, $"Iteration: {s.IterationId} ({s.Kind}, Status: {s.Status})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Revisions: spec: r{s.SpecRevision}, tasks: r{s.TasksRevision}");
        if (!string.IsNullOrWhiteSpace(s.Title))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Title: {s.Title}");
        }

        var activeTotal = s.TotalTasks - s.InactiveTasks;
        var bar = RenderAsciiProgressBar(s.ProgressPercentage, 10);
        sb.AppendLine(CultureInfo.InvariantCulture, $"Progress:  {bar} {s.ProgressPercentage:F1}% ({s.DoneTasks}/{activeTotal} tasks completed)");
        sb.AppendLine();

        sb.AppendLine("Task Breakdown:");
        foreach (var task in s.Tasks)
        {
            var agentStr = !string.IsNullOrEmpty(task.Agent) ? $" [agent: {task.Agent}]" : string.Empty;
            var blockedStr = task.IsBlocked ? $" (BLOCKED: {task.BlockedReason})" : string.Empty;
            sb.AppendLine(CultureInfo.InvariantCulture, $"  - [{task.Status}] {task.Id}: {task.Title}{agentStr}{blockedStr}");
        }

        if (s.Blockers.Count > 0 || s.PendingGates.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Blockers & Gates:");
            foreach (var b in s.Blockers)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  - Blocked: {b.TaskId} depends on {b.DependencyTaskId} ({b.DependencyStatus})");
            }
            foreach (var g in s.PendingGates)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  - Gate ({g.Kind}): {g.Id} - {g.Title} ({g.Status})");
            }
        }

        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Next Action: {s.RecommendedNextAction}");

        return sb.ToString();
    }

    public string Format(OutputFormat format) => format switch
    {
        OutputFormat.Xml => ToXmlString(),
        OutputFormat.Json => ToJsonString(),
        OutputFormat.Markdown => ToMarkdownString(),
        _ => ToHumanString()
    };
}
