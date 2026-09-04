using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Serialization;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;
using DogdouSpec.Core.XPath;

namespace DogdouSpec.Core.Backlog;

public sealed record BacklogCreateInput(
    string Id,
    string OperationId,
    string Actor,
    DateTimeOffset OccurredAt,
    string Kind,
    string? Severity,
    string Summary,
    string Statement,
    string Rationale,
    string Impact,
    IReadOnlyList<string> SourceIterations,
    IReadOnlyList<string> SourceTasks,
    string? TargetIteration,
    string? ReviewCondition);

public sealed record BacklogTransitionInput(
    string Id,
    string OperationId,
    string Actor,
    DateTimeOffset OccurredAt,
    string? ResolvingTask);

public static class BacklogLifecycle
{
    private const string BacklogDocument = "backlog.xml";
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
        { "open", "scheduled", "completed", "cancelled" };
    private static readonly HashSet<string> AllowedSeverities = new(StringComparer.Ordinal)
        { "p0", "p1", "p2", "p3" };

    public static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Add(
        string workspaceRoot,
        int expectedRevision,
        BacklogCreateInput input,
        bool dryRun = false)
    {
        var inputDiagnostics = ValidateCommon(expectedRevision, input.Id, input.OperationId, input.Actor);
        if (inputDiagnostics.Count > 0)
        {
            return (false, null, inputDiagnostics);
        }

        if (string.IsNullOrWhiteSpace(input.Kind) || string.IsNullOrWhiteSpace(input.Summary) ||
            string.IsNullOrWhiteSpace(input.Statement) || string.IsNullOrWhiteSpace(input.Rationale) ||
            string.IsNullOrWhiteSpace(input.Impact))
        {
            return Failure(DiagnosticCodes.InvalidArgument,
                "--kind, --summary, --statement, --rationale, and --impact must be non-empty.");
        }

        if (input.SourceIterations.Count + input.SourceTasks.Count == 0)
        {
            return Failure(DiagnosticCodes.InvalidArgument,
                "At least one --source-iteration or --source-task is required.");
        }

        if (input.SourceIterations.Concat(input.SourceTasks).Distinct(StringComparer.Ordinal).Count() !=
            input.SourceIterations.Count + input.SourceTasks.Count)
        {
            return Failure(DiagnosticCodes.InvalidArgument, "Backlog source references must be unique.");
        }

        var hasTarget = !string.IsNullOrWhiteSpace(input.TargetIteration);
        var hasReview = !string.IsNullOrWhiteSpace(input.ReviewCondition);
        if (hasTarget == hasReview)
        {
            return Failure(DiagnosticCodes.InvalidArgument,
                "Specify exactly one of --target-iteration or --review-condition.");
        }

        if (string.Equals(input.Kind, "defect", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(input.Severity) || !AllowedSeverities.Contains(input.Severity))
            {
                return Failure(DiagnosticCodes.InvalidArgument,
                    "Defect backlog items require --severity p0, p1, p2, or p3.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(input.Severity) && !AllowedSeverities.Contains(input.Severity))
        {
            return Failure(DiagnosticCodes.InvalidArgument, "--severity must be p0, p1, p2, or p3 when supplied.");
        }

        var (loaded, backlog, actualRevision, loadDiagnostics) = LoadBacklog(workspaceRoot);
        if (!loaded || backlog == null)
        {
            return (false, null, loadDiagnostics);
        }
        if (dryRun)
        {
            var dryRunBlocker = WorkspaceTransactionCommitter.GetDryRunBlocker(workspaceRoot);
            if (dryRunBlocker != null)
            {
                return (false, null, new[] { dryRunBlocker });
            }
        }

        var requestFingerprint = Fingerprint(new XElement("backlog-add",
            new XAttribute("id", input.Id),
            new XAttribute("operation_id", input.OperationId),
            new XAttribute("actor", input.Actor),
            new XAttribute("occurred_at", FormatTime(input.OccurredAt)),
            new XAttribute("kind", input.Kind),
            string.IsNullOrWhiteSpace(input.Severity) ? null : new XAttribute("severity", input.Severity),
            new XElement("summary", input.Summary),
            new XElement("statement", input.Statement),
            new XElement("rationale", input.Rationale),
            new XElement("impact", input.Impact),
            new XElement("sources",
                input.SourceIterations.Select(id => new XElement("iteration", id)),
                input.SourceTasks.Select(id => new XElement("task", id))),
            hasTarget ? new XElement("target-iteration", input.TargetIteration) : new XElement("review-condition", input.ReviewCondition)));

        var replay = CheckReplay(backlog, input.Id, input.OperationId, "add", requestFingerprint, expectedRevision, actualRevision);
        if (replay.Handled)
        {
            return replay.Success
                ? (true, ReplayEnvelope("backlog add", actualRevision), Array.Empty<Diagnostic>())
                : (false, null, replay.Diagnostics);
        }

        if (expectedRevision != actualRevision)
        {
            return RevisionFailure(expectedRevision, actualRevision);
        }

        var items = backlog.Root?.Element("items");
        if (items == null)
        {
            return Failure(DiagnosticCodes.XmlParseError, "backlog.xml does not contain /backlog/items.");
        }
        if (items.Elements("item").Any(e => string.Equals((string?)e.Attribute("id"), input.Id, StringComparison.Ordinal)))
        {
            return Failure(DiagnosticCodes.DuplicateId, $"Backlog item '{input.Id}' already exists.");
        }

        var referenceDiagnostics = ValidateReferenceTargets(
            workspaceRoot,
            input.SourceIterations.Select(id => (id, "iteration"))
                .Concat(input.SourceTasks.Select(id => (id, "task")))
                .Concat(hasTarget ? new[] { (input.TargetIteration!, "iteration") } : Array.Empty<(string, string)>()));
        if (referenceDiagnostics.Count > 0)
        {
            return (false, null, referenceDiagnostics);
        }

        var at = FormatTime(input.OccurredAt);
        var terms = new List<XElement>
        {
            new("term", new XAttribute("key", "kind"), new XAttribute("value", input.Kind))
        };
        if (!string.IsNullOrWhiteSpace(input.Severity))
        {
            terms.Add(new XElement("term", new XAttribute("key", "severity"), new XAttribute("value", input.Severity)));
        }

        var item = new XElement("item",
            new XAttribute("id", input.Id),
            new XAttribute("status", "open"),
            new XAttribute("created_at", at),
            new XAttribute("updated_at", at),
            new XElement("index", new XElement("summary", input.Summary), terms),
            new XElement("statement", input.Statement),
            new XElement("rationale", input.Rationale),
            new XElement("impact", input.Impact),
            new XElement("source",
                input.SourceIterations.Select(id => ProjectRef(id, "deferred-from")),
                input.SourceTasks.Select(id => ProjectRef(id, "deferred-from"))),
            hasTarget
                ? new XElement("target", ProjectRef(input.TargetIteration!, "target-iteration"))
                : new XElement("review_condition", input.ReviewCondition),
            new XElement("records", Receipt(input.OperationId, input.Actor, at, "add", requestFingerprint,
                "discussion", "informational", "Backlog item created through the public lifecycle helper.", null)));
        items.Add(item);
        return Commit(workspaceRoot, "backlog add", backlog, actualRevision, dryRun);
    }

    public static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Schedule(
        string workspaceRoot, int expectedRevision, BacklogTransitionInput input, bool dryRun = false) =>
        Transition(workspaceRoot, expectedRevision, input, "schedule", "scheduled", dryRun);

    public static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Complete(
        string workspaceRoot, int expectedRevision, BacklogTransitionInput input, bool dryRun = false) =>
        Transition(workspaceRoot, expectedRevision, input, "complete", "completed", dryRun);

    public static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Cancel(
        string workspaceRoot, int expectedRevision, BacklogTransitionInput input, bool dryRun = false) =>
        Transition(workspaceRoot, expectedRevision, input, "cancel", "cancelled", dryRun);

    public static (bool Success, BacklogListResult? Result, IReadOnlyList<Diagnostic> Diagnostics) List(
        string workspaceRoot, string? status = null, string? kind = null, string? severity = null)
    {
        if (!string.IsNullOrWhiteSpace(status) && !AllowedStatuses.Contains(status))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "--status must be open, scheduled, completed, or cancelled.") });
        }
        if (!string.IsNullOrWhiteSpace(severity) && !AllowedSeverities.Contains(severity))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "--severity must be p0, p1, p2, or p3.") });
        }

        var (loaded, backlog, revision, diagnostics) = LoadBacklog(workspaceRoot);
        if (!loaded || backlog == null)
        {
            return (false, null, diagnostics);
        }
        var summaries = backlog.Root?.Element("items")?.Elements("item")
            .Select(item =>
            {
                var terms = item.Element("index")?.Elements("term")
                    .Where(t => t.Attribute("key") != null)
                    .GroupBy(t => (string)t.Attribute("key")!, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => (string?)g.First().Attribute("value") ?? string.Empty, StringComparer.Ordinal)
                    ?? new Dictionary<string, string>(StringComparer.Ordinal);
                return new BacklogItemSummary(
                    (string?)item.Attribute("id") ?? string.Empty,
                    (string?)item.Attribute("status") ?? string.Empty,
                    item.Element("index")?.Element("summary")?.Value ?? string.Empty,
                    terms.GetValueOrDefault("kind", string.Empty),
                    terms.GetValueOrDefault("severity"));
            })
            .Where(item => string.IsNullOrWhiteSpace(status) || string.Equals(item.Status, status, StringComparison.Ordinal))
            .Where(item => string.IsNullOrWhiteSpace(kind) || string.Equals(item.Kind, kind, StringComparison.Ordinal))
            .Where(item => string.IsNullOrWhiteSpace(severity) || string.Equals(item.Severity, severity, StringComparison.Ordinal))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToList() ?? new List<BacklogItemSummary>();
        return (true, new BacklogListResult(revision, summaries), Array.Empty<Diagnostic>());
    }

    private static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Transition(
        string workspaceRoot, int expectedRevision, BacklogTransitionInput input, string action, string newStatus, bool dryRun = false)
    {
        var inputDiagnostics = ValidateCommon(expectedRevision, input.Id, input.OperationId, input.Actor);
        if (inputDiagnostics.Count > 0)
        {
            return (false, null, inputDiagnostics);
        }
        var (loaded, backlog, actualRevision, loadDiagnostics) = LoadBacklog(workspaceRoot);
        if (!loaded || backlog == null)
        {
            return (false, null, loadDiagnostics);
        }
        if (dryRun)
        {
            var dryRunBlocker = WorkspaceTransactionCommitter.GetDryRunBlocker(workspaceRoot);
            if (dryRunBlocker != null)
            {
                return (false, null, new[] { dryRunBlocker });
            }
        }

        var requestFingerprint = Fingerprint(new XElement("backlog-transition",
            new XAttribute("id", input.Id),
            new XAttribute("operation_id", input.OperationId),
            new XAttribute("actor", input.Actor),
            new XAttribute("occurred_at", FormatTime(input.OccurredAt)),
            new XAttribute("action", action),
            string.IsNullOrWhiteSpace(input.ResolvingTask) ? null : new XAttribute("resolving_task", input.ResolvingTask)));
        var replay = CheckReplay(backlog, input.Id, input.OperationId, action, requestFingerprint, expectedRevision, actualRevision);
        if (replay.Handled)
        {
            return replay.Success
                ? (true, ReplayEnvelope("backlog " + action, actualRevision), Array.Empty<Diagnostic>())
                : (false, null, replay.Diagnostics);
        }
        if (expectedRevision != actualRevision)
        {
            return RevisionFailure(expectedRevision, actualRevision);
        }

        var item = backlog.Root?.Element("items")?.Elements("item")
            .SingleOrDefault(e => string.Equals((string?)e.Attribute("id"), input.Id, StringComparison.Ordinal));
        if (item == null)
        {
            return Failure(DiagnosticCodes.DanglingReference, $"Backlog item '{input.Id}' does not exist.");
        }
        var oldStatus = (string?)item.Attribute("status") ?? string.Empty;
        if (oldStatus is "completed" or "cancelled")
        {
            return Failure(DiagnosticCodes.TaskImmutable, $"Backlog item '{input.Id}' is terminal with status '{oldStatus}'.");
        }
        if (action == "schedule" && oldStatus != "open")
        {
            return Failure(DiagnosticCodes.TaskTransitionConflict,
                $"Backlog item '{input.Id}' cannot transition from '{oldStatus}' to 'scheduled'.");
        }
        if (!string.IsNullOrWhiteSpace(input.ResolvingTask))
        {
            var refs = ValidateReferenceTargets(workspaceRoot, new[] { (input.ResolvingTask!, "task") });
            if (refs.Count > 0)
            {
                return (false, null, refs);
            }
        }

        var at = FormatTime(input.OccurredAt);
        item.SetAttributeValue("status", newStatus);
        item.SetAttributeValue("updated_at", at);
        var records = item.Element("records");
        if (records == null)
        {
            records = new XElement("records");
            item.Add(records);
        }
        records.Add(Receipt(input.OperationId, input.Actor, at, action, requestFingerprint,
            "resolution", action is "complete" or "cancel" ? "resolved" : "informational",
            $"Backlog item {action} transition applied through the public lifecycle helper.", input.ResolvingTask));
        return Commit(workspaceRoot, "backlog " + action, backlog, actualRevision, dryRun);
    }

    private static List<Diagnostic> ValidateCommon(int expectedRevision, string id, string operationId, string actor)
    {
        var diagnostics = new List<Diagnostic>();
        if (expectedRevision <= 0)
        {
            diagnostics.Add(Diagnostic.Error(DiagnosticCodes.InvalidArgument, "--expected-revision must be a positive integer."));
        }
        if (!ProjectSemanticIndex.IsValidTimeFirstId(id) || !ProjectSemanticIndex.IsValidTimeFirstId(operationId))
        {
            diagnostics.Add(Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, "--id and --operation-id must use the time-first ID grammar."));
        }
        if (string.IsNullOrWhiteSpace(actor))
        {
            diagnostics.Add(Diagnostic.Error(DiagnosticCodes.InvalidArgument, "--actor must be non-empty."));
        }
        return diagnostics;
    }

    private static (bool Success, XDocument? Document, int Revision, IReadOnlyList<Diagnostic> Diagnostics) LoadBacklog(string workspaceRoot)
    {
        var (workspaceSafe, workspaceError) = PathSecurity.VerifyWorkspaceDirectorySecurity(workspaceRoot);
        if (!workspaceSafe || workspaceError != null)
        {
            return (false, null, 0, new[]
            {
                workspaceError ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, "Workspace directory security verification failed.")
            });
        }
        var path = Path.Combine(workspaceRoot, BacklogDocument);
        var (contained, containmentError) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, path);
        if (!contained || containmentError != null)
        {
            return (false, null, 0, new[] { containmentError! });
        }
        if (!File.Exists(path))
        {
            return (false, null, 0, new[] { Diagnostic.Error(DiagnosticCodes.DocumentNotFound, "backlog.xml does not exist.", BacklogDocument) });
        }
        if (new FileInfo(path).Length > XPathQueryLimits.MaxDocumentBytes)
        {
            return (false, null, 0, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, "backlog.xml exceeds the maximum managed document size.", BacklogDocument) });
        }
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = SecureXmlReaderFactory.CreateReader(stream);
            var document = XDocument.Load(reader, LoadOptions.SetLineInfo);
            if (!int.TryParse(document.Root?.Attribute("revision")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision <= 0)
            {
                return (false, null, 0, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, "backlog.xml has an invalid revision.", BacklogDocument) });
            }
            return (true, document, revision, Array.Empty<Diagnostic>());
        }
        catch (Exception ex)
        {
            return (false, null, 0, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Unable to read backlog.xml: {ex.Message}", BacklogDocument) });
        }
    }

    private static IReadOnlyList<Diagnostic> ValidateReferenceTargets(
        string workspaceRoot, IEnumerable<(string Id, string ElementName)> requested)
    {
        var requestList = requested.ToList();
        if (requestList.Count == 0)
        {
            return Array.Empty<Diagnostic>();
        }
        var (success, documents, discoveryDiagnostics) = WorkspaceDiscovery.EnumerateDocuments(workspaceRoot);
        if (!success)
        {
            return discoveryDiagnostics;
        }
        var loaded = new List<(ManagedDocument Document, XDocument XDoc)>();
        try
        {
            foreach (var document in documents)
            {
                using var stream = File.OpenRead(document.FullPath);
                using var reader = SecureXmlReaderFactory.CreateReader(stream);
                loaded.Add((document, XDocument.Load(reader, LoadOptions.SetLineInfo)));
            }
        }
        catch (Exception ex)
        {
            return new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Unable to build backlog reference index: {ex.Message}") };
        }
        var index = ProjectSemanticIndex.Build(loaded);
        var diagnostics = new List<Diagnostic>();
        foreach (var (id, elementName) in requestList)
        {
            if (!ProjectSemanticIndex.IsValidTimeFirstId(id))
            {
                diagnostics.Add(Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, $"Reference target '{id}' is not a valid time-first ID."));
                continue;
            }
            if (!index.ObjectsById.TryGetValue(id, out var objects) || objects.Count == 0)
            {
                diagnostics.Add(Diagnostic.Error(DiagnosticCodes.DanglingReference, $"Reference target '{id}' does not exist."));
            }
            else if (objects.Count != 1)
            {
                diagnostics.Add(Diagnostic.Error(DiagnosticCodes.AmbiguousReference, $"Reference target '{id}' is ambiguous."));
            }
            else if (!string.Equals(objects[0].ElementName, elementName, StringComparison.Ordinal))
            {
                diagnostics.Add(Diagnostic.Error(DiagnosticCodes.InvalidReferenceTargetType,
                    $"Reference target '{id}' must identify <{elementName}>, but identifies <{objects[0].ElementName}>."));
            }
        }
        return diagnostics;
    }

    private static (bool Handled, bool Success, IReadOnlyList<Diagnostic> Diagnostics) CheckReplay(
        XDocument backlog, string itemId, string operationId, string action, string fingerprint,
        int expectedRevision, int actualRevision)
    {
        var receipts = backlog.Descendants("record")
            .Where(r => string.Equals((string?)r.Attribute("operation_id"), operationId, StringComparison.Ordinal))
            .ToList();
        if (receipts.Count == 0)
        {
            return (false, false, Array.Empty<Diagnostic>());
        }
        var targetReceipt = receipts.FirstOrDefault(r =>
            string.Equals((string?)r.Ancestors("item").FirstOrDefault()?.Attribute("id"), itemId, StringComparison.Ordinal));
        var storedAction = targetReceipt?.Element("index")?.Elements("term")
            .SingleOrDefault(t => string.Equals((string?)t.Attribute("key"), "action", StringComparison.Ordinal))?.Attribute("value")?.Value;
        var storedFingerprint = targetReceipt?.Element("index")?.Elements("term")
            .SingleOrDefault(t => string.Equals((string?)t.Attribute("key"), "request-sha256", StringComparison.Ordinal))?.Attribute("value")?.Value;
        var revisionAccepted = expectedRevision == actualRevision || expectedRevision == actualRevision - 1;
        if (receipts.Count != 1 || targetReceipt == null || !string.Equals(storedAction, action, StringComparison.Ordinal) ||
            !string.Equals(storedFingerprint, fingerprint, StringComparison.Ordinal) || !revisionAccepted)
        {
            return (true, false, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict,
                $"Operation ID '{operationId}' was already used with different backlog semantics or revision context.", BacklogDocument) });
        }
        return (true, true, Array.Empty<Diagnostic>());
    }

    private static XElement Receipt(string operationId, string actor, string at, string action, string fingerprint,
        string kind, string status, string summary, string? resolvingTask) =>
        new("record",
            new XAttribute("id", operationId + "-receipt"),
            new XAttribute("kind", kind),
            new XAttribute("status", status),
            new XAttribute("created_at", at),
            new XAttribute("actor", actor),
            new XAttribute("operation_id", operationId),
            new XElement("index",
                new XElement("summary", $"Backlog {action} receipt."),
                new XElement("term", new XAttribute("key", "action"), new XAttribute("value", action)),
                new XElement("term", new XAttribute("key", "request-sha256"), new XAttribute("value", fingerprint))),
            new XElement("summary", summary),
            string.IsNullOrWhiteSpace(resolvingTask)
                ? null
                : new XElement("sources", ProjectRef(resolvingTask, "resolved-by")));

    private static XElement ProjectRef(string target, string relation) =>
        new("ref", new XAttribute("scope", "project"), new XAttribute("target", target), new XAttribute("relation", relation));

    private static string Fingerprint(XElement element)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(element.ToString(SaveOptions.DisableFormatting)));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string FormatTime(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static MutationEnvelope ReplayEnvelope(string command, int revision) =>
        new(command, new[] { new MutatedDocument(BacklogDocument, revision) }, alreadyApplied: true);

    private static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Commit(
        string workspaceRoot, string command, XDocument backlog, int actualRevision, bool dryRun = false)
    {
        backlog.Root!.SetAttributeValue("revision", actualRevision + 1);
        var content = Serialize(backlog);
        var operation = new TransactionDocumentOperation(BacklogDocument, content, actualRevision, actualRevision + 1);
        return WorkspaceTransactionCommitter.Commit(workspaceRoot, command, new[] { operation }, dryRun: dryRun);
    }

    private static string Serialize(XDocument document) =>
        ManagedDocumentSerializer.Serialize(document);

    private static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Failure(string code, string message) =>
        (false, null, new[] { Diagnostic.Error(code, message, BacklogDocument) });

    private static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) RevisionFailure(int expected, int actual) =>
        (false, null, new[] { new Diagnostic(DiagnosticCodes.RevisionConflict, "error",
            $"Expected revision {expected} does not match actual revision {actual} for document '{BacklogDocument}'.",
            BacklogDocument, ExpectedRevision: expected, ActualRevision: actual) });
}
