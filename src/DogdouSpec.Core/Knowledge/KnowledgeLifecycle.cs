using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Serialization;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;
using DogdouSpec.Core.XPath;

namespace DogdouSpec.Core.Knowledge;

public sealed record KnowledgeCreateInput(
    string Id,
    string OperationId,
    string Actor,
    DateTimeOffset OccurredAt,
    string Topic,
    string Summary,
    string Statement,
    string Rationale,
    IReadOnlyList<string> SourceIterations,
    IReadOnlyList<string> SourceTasks);

public static class KnowledgeLifecycle
{
    private const string KnowledgeDocument = "knowledge.xml";
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
        { "proposed", "verified", "retired", "rejected" };
    private static readonly Regex TokenValueRegex = new(@"^[a-zA-Z0-9][a-zA-Z0-9.-]*$", RegexOptions.Compiled);

    public static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Add(
        string workspaceRoot,
        int expectedRevision,
        KnowledgeCreateInput input,
        bool dryRun = false)
    {
        var inputDiagnostics = ValidateCommon(expectedRevision, input.Id, input.OperationId, input.Actor);
        if (inputDiagnostics.Count > 0)
        {
            return (false, null, inputDiagnostics);
        }

        if (string.IsNullOrWhiteSpace(input.Topic) || string.IsNullOrWhiteSpace(input.Summary) ||
            string.IsNullOrWhiteSpace(input.Statement) || string.IsNullOrWhiteSpace(input.Rationale))
        {
            return Failure(DiagnosticCodes.InvalidArgument,
                "--topic, --summary, --statement, and --rationale must be non-empty.");
        }

        if (!TokenValueRegex.IsMatch(input.Topic))
        {
            return Failure(DiagnosticCodes.InvalidArgument,
                "--topic must match the token grammar [a-zA-Z0-9][a-zA-Z0-9.-]*.");
        }

        if (input.SourceIterations.Count + input.SourceTasks.Count == 0)
        {
            return Failure(DiagnosticCodes.InvalidArgument,
                "At least one --source-iteration or --source-task is required.");
        }

        if (input.SourceIterations.Concat(input.SourceTasks).Distinct(StringComparer.Ordinal).Count() !=
            input.SourceIterations.Count + input.SourceTasks.Count)
        {
            return Failure(DiagnosticCodes.InvalidArgument, "Knowledge source references must be unique.");
        }

        var (loaded, knowledge, actualRevision, loadDiagnostics) = LoadKnowledge(workspaceRoot);
        if (!loaded || knowledge == null)
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

        var requestFingerprint = Fingerprint(new XElement("knowledge-add",
            new XAttribute("id", input.Id),
            new XAttribute("operation_id", input.OperationId),
            new XAttribute("actor", input.Actor),
            new XAttribute("occurred_at", FormatTime(input.OccurredAt)),
            new XAttribute("topic", input.Topic),
            new XElement("summary", input.Summary),
            new XElement("statement", input.Statement),
            new XElement("rationale", input.Rationale),
            new XElement("sources",
                input.SourceIterations.Select(id => new XElement("iteration", id)),
                input.SourceTasks.Select(id => new XElement("task", id)))));

        var replay = CheckReplay(knowledge, input.Id, input.OperationId, "add", requestFingerprint, expectedRevision, actualRevision);
        if (replay.Handled)
        {
            return replay.Success
                ? (true, ReplayEnvelope("knowledge add", actualRevision), Array.Empty<Diagnostic>())
                : (false, null, replay.Diagnostics);
        }

        if (expectedRevision != actualRevision)
        {
            return RevisionFailure(expectedRevision, actualRevision);
        }

        if (knowledge.Root?.Elements("entry").Any(e => string.Equals((string?)e.Attribute("id"), input.Id, StringComparison.Ordinal)) == true)
        {
            return Failure(DiagnosticCodes.DuplicateId, $"Knowledge entry '{input.Id}' already exists.");
        }

        var referenceDiagnostics = ValidateReferenceTargetsAndOperationId(
            workspaceRoot,
            input.OperationId,
            input.SourceIterations.Select(id => (id, "iteration"))
                .Concat(input.SourceTasks.Select(id => (id, "task"))));
        if (referenceDiagnostics.Count > 0)
        {
            return (false, null, referenceDiagnostics);
        }

        var at = FormatTime(input.OccurredAt);
        var entry = new XElement("entry",
            new XAttribute("id", input.Id),
            new XAttribute("status", "proposed"),
            new XAttribute("created_at", at),
            new XAttribute("updated_at", at),
            new XElement("index",
                new XElement("summary", input.Summary),
                new XElement("term", new XAttribute("key", "topic"), new XAttribute("value", input.Topic))),
            new XElement("statement", input.Statement),
            new XElement("rationale", input.Rationale),
            new XElement("sources",
                input.SourceIterations.Select(id => ProjectRef(id, "derived-from")),
                input.SourceTasks.Select(id => ProjectRef(id, "derived-from"))),
            new XElement("records", Receipt(input.OperationId, input.Actor, at, "add", requestFingerprint,
                "discussion", "informational", "Knowledge entry created through the public lifecycle helper.")));

        knowledge.Root!.Add(entry);
        return Commit(workspaceRoot, "knowledge add", knowledge, actualRevision, dryRun);
    }

    public static (bool Success, KnowledgeListResult? Result, IReadOnlyList<Diagnostic> Diagnostics) List(
        string workspaceRoot, string? status = null, string? topic = null)
    {
        if (!string.IsNullOrWhiteSpace(status) && !AllowedStatuses.Contains(status))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "--status must be proposed, verified, retired, or rejected.") });
        }

        var (loaded, knowledge, revision, diagnostics) = LoadKnowledge(workspaceRoot);
        if (!loaded || knowledge == null)
        {
            return (false, null, diagnostics);
        }

        var summaries = knowledge.Root?.Elements("entry")
            .Select(entry =>
            {
                var entryTopic = entry.Element("index")?.Elements("term")
                    .FirstOrDefault(t => string.Equals((string?)t.Attribute("key"), "topic", StringComparison.Ordinal))?
                    .Attribute("value")?.Value ?? string.Empty;

                return new KnowledgeItemSummary(
                    (string?)entry.Attribute("id") ?? string.Empty,
                    (string?)entry.Attribute("status") ?? string.Empty,
                    entry.Element("index")?.Element("summary")?.Value ?? string.Empty,
                    entryTopic);
            })
            .Where(entry => string.IsNullOrWhiteSpace(status) || string.Equals(entry.Status, status, StringComparison.Ordinal))
            .Where(entry => string.IsNullOrWhiteSpace(topic) || string.Equals(entry.Topic, topic, StringComparison.Ordinal))
            .OrderBy(entry => entry.Id, StringComparer.Ordinal)
            .ToList() ?? new List<KnowledgeItemSummary>();

        return (true, new KnowledgeListResult(revision, summaries), Array.Empty<Diagnostic>());
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

    private static (bool Success, XDocument? Document, int Revision, IReadOnlyList<Diagnostic> Diagnostics) LoadKnowledge(string workspaceRoot)
    {
        var (workspaceSafe, workspaceError) = PathSecurity.VerifyWorkspaceDirectorySecurity(workspaceRoot);
        if (!workspaceSafe || workspaceError != null)
        {
            return (false, null, 0, new[]
            {
                workspaceError ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, "Workspace directory security verification failed.")
            });
        }
        var path = Path.Combine(workspaceRoot, KnowledgeDocument);
        var (contained, containmentError) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, path);
        if (!contained || containmentError != null)
        {
            return (false, null, 0, new[] { containmentError! });
        }
        if (!File.Exists(path))
        {
            return (false, null, 0, new[] { Diagnostic.Error(DiagnosticCodes.DocumentNotFound, "knowledge.xml does not exist.", KnowledgeDocument) });
        }
        if (new FileInfo(path).Length > XPathQueryLimits.MaxDocumentBytes)
        {
            return (false, null, 0, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, "knowledge.xml exceeds the maximum managed document size.", KnowledgeDocument) });
        }
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = SecureXmlReaderFactory.CreateReader(stream);
            var document = XDocument.Load(reader, LoadOptions.SetLineInfo);
            if (!string.Equals(document.Root?.Name.LocalName, "knowledge", StringComparison.Ordinal))
            {
                return (false, null, 0, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, "knowledge.xml root element must be <knowledge>.", KnowledgeDocument) });
            }
            if (!int.TryParse(document.Root?.Attribute("revision")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision <= 0)
            {
                return (false, null, 0, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, "knowledge.xml has an invalid revision.", KnowledgeDocument) });
            }
            return (true, document, revision, Array.Empty<Diagnostic>());
        }
        catch (Exception ex)
        {
            return (false, null, 0, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Unable to read knowledge.xml: {ex.Message}", KnowledgeDocument) });
        }
    }

    private static IReadOnlyList<Diagnostic> ValidateReferenceTargetsAndOperationId(
        string workspaceRoot, string operationId, IEnumerable<(string Id, string ElementName)> requested)
    {
        var requestList = requested.ToList();
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
            return new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Unable to build knowledge reference index: {ex.Message}") };
        }
        var index = ProjectSemanticIndex.Build(loaded);
        var diagnostics = new List<Diagnostic>();

        if (index.OperationReceiptsById.ContainsKey(operationId))
        {
            diagnostics.Add(Diagnostic.Error(DiagnosticCodes.IdempotencyConflict,
                $"Operation ID '{operationId}' was already used with different semantics or revision context.", KnowledgeDocument));
        }

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
        XDocument knowledge, string entryId, string operationId, string action, string fingerprint,
        int expectedRevision, int actualRevision)
    {
        var receipts = knowledge.Descendants("record")
            .Where(r => string.Equals((string?)r.Attribute("operation_id"), operationId, StringComparison.Ordinal))
            .ToList();
        if (receipts.Count == 0)
        {
            return (false, false, Array.Empty<Diagnostic>());
        }
        var targetReceipt = receipts.FirstOrDefault(r =>
            string.Equals((string?)r.Ancestors("entry").FirstOrDefault()?.Attribute("id"), entryId, StringComparison.Ordinal));
        var storedAction = targetReceipt?.Element("index")?.Elements("term")
            .SingleOrDefault(t => string.Equals((string?)t.Attribute("key"), "action", StringComparison.Ordinal))?.Attribute("value")?.Value;
        var storedFingerprint = targetReceipt?.Element("index")?.Elements("term")
            .SingleOrDefault(t => string.Equals((string?)t.Attribute("key"), "request-sha256", StringComparison.Ordinal))?.Attribute("value")?.Value;
        var revisionAccepted = expectedRevision == actualRevision || expectedRevision == actualRevision - 1;
        if (receipts.Count != 1 || targetReceipt == null || !string.Equals(storedAction, action, StringComparison.Ordinal) ||
            !string.Equals(storedFingerprint, fingerprint, StringComparison.Ordinal) || !revisionAccepted)
        {
            return (true, false, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict,
                $"Operation ID '{operationId}' was already used with different knowledge semantics or revision context.", KnowledgeDocument) });
        }
        return (true, true, Array.Empty<Diagnostic>());
    }

    private static XElement Receipt(string operationId, string actor, string at, string action, string fingerprint,
        string kind, string status, string summary) =>
        new("record",
            new XAttribute("id", operationId + "-receipt"),
            new XAttribute("kind", kind),
            new XAttribute("status", status),
            new XAttribute("created_at", at),
            new XAttribute("actor", actor),
            new XAttribute("operation_id", operationId),
            new XElement("index",
                new XElement("summary", $"Knowledge {action} receipt."),
                new XElement("term", new XAttribute("key", "action"), new XAttribute("value", action)),
                new XElement("term", new XAttribute("key", "request-sha256"), new XAttribute("value", fingerprint))),
            new XElement("summary", summary));

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
        new(command, new[] { new MutatedDocument(KnowledgeDocument, revision) }, alreadyApplied: true);

    private static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Commit(
        string workspaceRoot, string command, XDocument knowledge, int actualRevision, bool dryRun = false)
    {
        knowledge.Root!.SetAttributeValue("revision", actualRevision + 1);
        var content = Serialize(knowledge);
        var operation = new TransactionDocumentOperation(KnowledgeDocument, content, actualRevision, actualRevision + 1);
        return WorkspaceTransactionCommitter.Commit(workspaceRoot, command, new[] { operation }, dryRun: dryRun);
    }

    private static string Serialize(XDocument document) =>
        ManagedDocumentSerializer.Serialize(document);

    private static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Failure(string code, string message) =>
        (false, null, new[] { Diagnostic.Error(code, message, KnowledgeDocument) });

    private static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) RevisionFailure(int expected, int actual) =>
        (false, null, new[] { new Diagnostic(DiagnosticCodes.RevisionConflict, "error",
            $"Expected revision {expected} does not match actual revision {actual} for document '{KnowledgeDocument}'.",
            KnowledgeDocument, ExpectedRevision: expected, ActualRevision: actual) });
}