using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Serialization;
using DogdouSpec.Core.Time;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;
using DogdouSpec.Core.XPath;

namespace DogdouSpec.Core.Tasks;

public sealed record QuickTaskInput(
    string Title, IReadOnlyList<string> Scopes, string DoneWhen, string Why,
    IReadOnlyList<string> Origins, IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> Terms, string? IterationId, int? ExpectedRevision,
    bool Start, bool DryRun, string? TaskId, string? OperationId,
    string? Agent = null, bool ReviewRequired = false);

public sealed record QuickTaskResult(string IterationId, int ExpectedRevision, string RequestXml, XElement Task);

/// <summary>Compact command input is expanded into the existing task-add representation before it is committed.</summary>
public static class TaskQuick
{
    public static (bool Success, QuickTaskResult? Result, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Create(
        string workspaceRoot, QuickTaskInput input, IClock? clock = null)
    {
        clock ??= SystemClock.Instance;
        if (Encoding.UTF8.GetByteCount(input.Title + input.DoneWhen + input.Why + string.Concat(input.Scopes) + string.Concat(input.Terms)) > XPathQueryLimits.MaxDocumentBytes)
            return (false, null, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, "task quick input exceeds the maximum XML document size.") });
        if (string.IsNullOrWhiteSpace(input.Title) || input.Scopes.Count == 0 || string.IsNullOrWhiteSpace(input.DoneWhen) || string.IsNullOrWhiteSpace(input.Why))
            return (false, null, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "--title, at least one --scope, --done-when, and --why are required.") });
        if (!string.IsNullOrWhiteSpace(input.OperationId) && (input.OperationId.Length < 16 ||
            !DateTimeOffset.TryParseExact(input.OperationId[..16], "yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _)))
            return (false, null, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "--operation-id must begin with a UTC YYYYMMDDTHHmmssZ timestamp so retries are deterministic.") });
        if (string.IsNullOrWhiteSpace(input.TaskId) != string.IsNullOrWhiteSpace(input.OperationId))
            return (false, null, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "--id and --operation-id must be supplied together for a replayable invocation.") });
        if (input.ReviewRequired && string.IsNullOrWhiteSpace(input.Agent))
            return (false, null, null, new[] { Diagnostic.Error(DiagnosticCodes.TaskReviewImplementerUnknown, "--review-required must be paired with --agent implementer attribution.") });

        var (iterationOk, iterationId, iterationDiag) = ResolveIteration(workspaceRoot, input.IterationId);
        if (!iterationOk) return (false, null, null, new[] { iterationDiag! });
        var tasksRelative = $"{iterationId}/tasks.xml";
        var specRelative = $"{iterationId}/spec.xml";
        var tasksPath = Path.Combine(workspaceRoot, tasksRelative.Replace('/', Path.DirectorySeparatorChar));
        var specPath = Path.Combine(workspaceRoot, specRelative.Replace('/', Path.DirectorySeparatorChar));
        var (tasksContained, tasksContainmentError) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, tasksPath);
        if (!tasksContained || tasksContainmentError != null) return (false, null, null, new[] { tasksContainmentError! });
        var (specContained, specContainmentError) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, specPath);
        if (!specContained || specContainmentError != null) return (false, null, null, new[] { specContainmentError! });
        if (!File.Exists(tasksPath)) return (false, null, null, new[] { Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Iteration '{iterationId}' has no tasks.xml.") });
        if (!File.Exists(specPath)) return (false, null, null, new[] { Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Iteration '{iterationId}' has no spec.xml.") });
        if (new FileInfo(tasksPath).Length > XPathQueryLimits.MaxDocumentBytes || new FileInfo(specPath).Length > XPathQueryLimits.MaxDocumentBytes)
            return (false, null, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, "Quick task target document exceeds the maximum XML document size.") });
        XDocument tasks;
        try { using var stream = File.OpenRead(tasksPath); using var reader = SecureXmlReaderFactory.CreateReader(stream); tasks = XDocument.Load(reader); }
        catch (Exception ex) { return (false, null, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Unable to read tasks.xml: {ex.Message}") }); }
        XDocument spec;
        try { using var stream = File.OpenRead(specPath); using var reader = SecureXmlReaderFactory.CreateReader(stream); spec = XDocument.Load(reader); }
        catch (Exception ex) { return (false, null, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Unable to read spec.xml: {ex.Message}") }); }
        if (!int.TryParse(tasks.Root?.Attribute("revision")?.Value, CultureInfo.InvariantCulture, out var actualRevision) || actualRevision <= 0)
            return (false, null, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, "Target tasks.xml has an invalid revision.") });
        var expectedRevision = input.ExpectedRevision ?? actualRevision;
        var knownReplay = !string.IsNullOrWhiteSpace(input.TaskId) && !string.IsNullOrWhiteSpace(input.OperationId) &&
            tasks.Root!.Elements("task").Any(t => string.Equals(t.Attribute("id")?.Value, input.TaskId, StringComparison.Ordinal) && t.Element("records")?.Elements("record").Any(r => string.Equals(r.Attribute("id")?.Value, input.OperationId + "-receipt", StringComparison.Ordinal)) == true);
        if (input.ExpectedRevision.HasValue && input.ExpectedRevision.Value != actualRevision && !(knownReplay && !input.DryRun && input.ExpectedRevision.Value == actualRevision - 1))
            return (false, null, null, new[] { new Diagnostic(DiagnosticCodes.RevisionConflict, "error", $"Expected revision {input.ExpectedRevision} does not match actual revision {actualRevision}.", $"{iterationId}/tasks.xml", ExpectedRevision: input.ExpectedRevision, ActualRevision: actualRevision) });

        var timestamp = ResolveTimestamp(input.OperationId, clock);
        var at = timestamp.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var idTimestamp = timestamp.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var suffix = Slug(input.Title);
        var taskId = input.TaskId ?? $"{idTimestamp}-task-{suffix}";
        var operationId = input.OperationId ?? $"{idTimestamp}-quick-{suffix}";
        if (!ProjectSemanticIndex.IsValidTimeFirstId(taskId) || !ProjectSemanticIndex.IsValidTimeFirstId(operationId))
            return (false, null, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, "--id and --operation-id must use the time-first ID grammar.") });
        if (input.Origins.Distinct(StringComparer.Ordinal).Count() != input.Origins.Count || input.Dependencies.Distinct(StringComparer.Ordinal).Count() != input.Dependencies.Count)
            return (false, null, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "--origin and --depends-on values must be unique.") });

        var origins = input.Origins.Count == 0
            ? new[] { new XElement("ref", new XAttribute("scope", "iteration"), new XAttribute("target", iterationId!), new XAttribute("relation", "supports")) }
            : input.Origins.Select(id => new XElement("ref", new XAttribute("scope", "iteration"), new XAttribute("target", id), new XAttribute("relation", "implements"))).ToArray();
        var requirementDefinitions = spec.Root?.Element("product")?.Element("requirements")?.Elements("requirement")
            .Where(r => !string.IsNullOrWhiteSpace(r.Attribute("id")?.Value)).ToList() ?? new List<XElement>();
        if (requirementDefinitions.GroupBy(r => r.Attribute("id")!.Value, StringComparer.Ordinal).Any(g => g.Count() != 1))
            return (false, null, null, new[] { Diagnostic.Error(DiagnosticCodes.DuplicateId, "Iteration contains duplicate requirement IDs.", specRelative) });
        var requirementStatus = requirementDefinitions.ToDictionary(r => r.Attribute("id")!.Value, r => r.Attribute("status")?.Value ?? string.Empty, StringComparer.Ordinal);
        if (input.Origins.Any(id => !requirementStatus.ContainsKey(id)))
            return (false, null, null, new[] { Diagnostic.Error(DiagnosticCodes.DanglingReference, "Every --origin must identify a requirement in the selected iteration.", specRelative) });
        if (input.Start && !string.Equals(spec.Root?.Attribute("status")?.Value, "active", StringComparison.Ordinal))
            return (false, null, null, new[] { Diagnostic.Error(DiagnosticCodes.IterationReplanningExecutionFrozen, "Quick --start requires an active iteration.", specRelative) });
        if (input.Start && input.Origins.Any(id => !string.Equals(requirementStatus[id], "approved", StringComparison.Ordinal)))
            return (false, null, null, new[] { Diagnostic.Error(DiagnosticCodes.OwnerDecisionRequired, "Quick --start requires every origin requirement to be approved.", specRelative) });
        var terms = new List<XElement> { new("term", new XAttribute("key", "kind"), new XAttribute("value", "quick")) };
        foreach (var text in input.Terms)
        {
            var split = text.IndexOf('=');
            if (split <= 0 || split == text.Length - 1) return (false, null, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Each --term must be key=value.") });
            terms.Add(new XElement("term", new XAttribute("key", text[..split]), new XAttribute("value", text[(split + 1)..])));
        }
        var task = new XElement("task",
            new XAttribute("id", taskId), new XAttribute("status", input.Start ? "in-progress" : "pending"),
            new XAttribute("created_at", at), new XAttribute("updated_at", at),
            string.IsNullOrWhiteSpace(input.Agent) ? null : new XAttribute("agent", input.Agent),
            input.Start ? new XAttribute("started_at", at) : null,
            new XElement("index", new XElement("summary", input.Title), terms),
            new XElement("title", input.Title), new XElement("objective", input.DoneWhen), new XElement("rationale", input.Why),
            new XElement("scope", new XElement("repository", new XAttribute("path", "."), input.Scopes.Select(s => new XElement("include", new XAttribute("path", s))))),
            new XElement("origin", origins),
            input.Dependencies.Count == 0 ? null : new XElement("dependencies", input.Dependencies.Select(id => new XElement("ref", new XAttribute("scope", "document"), new XAttribute("target", id), new XAttribute("relation", "depends-on")))),
            new XElement("constraints"), new XElement("acceptance", new XElement("criterion", new XAttribute("id", taskId + "-done"), new XAttribute("status", "pending"), input.DoneWhen)),
            new XElement("context", new XElement("summary", input.Why)),
            input.ReviewRequired ? new XElement("review", new XAttribute("required", "true")) : null,
            new XElement("records", input.Start ? new XElement("record", new XAttribute("id", operationId + "-start"), new XAttribute("kind", "start"), new XAttribute("status", "informational"), new XAttribute("created_at", at), new XAttribute("actor", "quick-task"), new XElement("summary", "Quick task created and started atomically.")) : null));
        StatusTermHelper.SynchronizeStatusTerm(task, input.Start ? "in-progress" : "pending");
        var request = new XElement("task-add", new XAttribute("id", operationId), new XAttribute("actor", "quick-task"), new XAttribute("occurred_at", at), task);
        var requestXml = CanonicalXmlSerializer.Serialize(request);
        if (Encoding.UTF8.GetByteCount(requestXml) > XPathQueryLimits.MaxDocumentBytes)
            return (false, null, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, "Generated task quick request exceeds the maximum XML document size.") });
        IReadOnlyList<TransactionReadPrecondition> dependencyReadPreconditions = Array.Empty<TransactionReadPrecondition>();
        if (input.Dependencies.Count > 0)
        {
            var (depsOk, depDiags, depReadPreconditions) = TaskDependencyGate.EvaluateTaskDependencies(workspaceRoot, taskId, task, tasksRelative);
            if (input.Start)
            {
                if (!depsOk || depDiags.Count > 0)
                    return (false, null, null, depDiags);
                dependencyReadPreconditions = depReadPreconditions;
            }
            else
            {
                var nonStatusErrors = depDiags.Where(d => d.Code != DiagnosticCodes.TaskTransitionConflict).ToList();
                if (nonStatusErrors.Count > 0)
                    return (false, null, null, nonStatusErrors);
            }
        }
        var result = new QuickTaskResult(iterationId!, expectedRevision, requestXml, task);
        var (success, envelope, diagnostics) = TaskAdder.AddQuick(
            workspaceRoot, iterationId!, expectedRevision, requestXml, input.Start, input.DryRun,
            readPreconditions: dependencyReadPreconditions);
        return (success, result, envelope, diagnostics);
    }

    private static (bool, string?, Diagnostic?) ResolveIteration(string root, string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var (valid, normalized, error) = PathSecurity.ValidateIterationId(requested);
            if (!valid || error != null) return (false, null, error ?? Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid iteration ID '{requested}'."));
            requested = normalized;
            var spec = Path.Combine(root, requested, "spec.xml");
            if (!File.Exists(spec)) return (false, null, Diagnostic.Error(DiagnosticCodes.IterationNotFound, $"Iteration '{requested}' does not exist."));
            return (true, requested, null);
        }
        var candidates = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var spec = Path.Combine(dir, "spec.xml");
            if (!File.Exists(spec)) continue;
            var (contained, containmentError) = PathSecurity.CheckContainmentAndReparsePoints(root, spec);
            if (!contained || containmentError != null) return (false, null, containmentError);
            if (new FileInfo(spec).Length > XPathQueryLimits.MaxDocumentBytes) return (false, null, Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"Iteration spec '{Path.GetFileName(dir)}/spec.xml' exceeds the maximum XML document size."));
            try { using var stream = File.OpenRead(spec); using var reader = SecureXmlReaderFactory.CreateReader(stream); var doc = XDocument.Load(reader); if (doc.Root?.Attribute("status")?.Value == "active") candidates.Add(doc.Root.Attribute("id")?.Value ?? Path.GetFileName(dir)); } catch (Exception ex) { return (false, null, Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Unable to inspect iteration '{Path.GetFileName(dir)}': {ex.Message}")); }
        }
        if (candidates.Count == 1) return (true, candidates[0], null);
        return (false, null, Diagnostic.Error(DiagnosticCodes.CardinalityConflict, candidates.Count == 0 ? "task quick requires --iteration because no active iteration exists." : "task quick requires --iteration because multiple active iterations exist."));
    }

    private static string Slug(string text)
    {
        var value = new string(text.ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' || c is >= '0' and <= '9' ? c : '-').ToArray()).Trim('-');
        return string.IsNullOrEmpty(value) ? "work" : value.Length > 32 ? value[..32].TrimEnd('-') : value;
    }

    private static DateTimeOffset ResolveTimestamp(string? operationId, IClock clock)
    {
        if (!string.IsNullOrWhiteSpace(operationId) && operationId.Length >= 16 &&
            DateTimeOffset.TryParseExact(operationId[..16], "yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed.ToUniversalTime();
        return clock.UtcNow.ToUniversalTime();
    }

    private static bool IsTerminal(string? status) => status is "done" or "transferred" or "superseded" or "cancelled";
}
