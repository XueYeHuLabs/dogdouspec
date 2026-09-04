using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using DogdouSpec.Core.Append;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Resources;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Serialization;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;
using DogdouSpec.Core.XPath;

namespace DogdouSpec.Core.Tasks;

public static class TaskReviewer
{
    public static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Submit(
        string workspaceRoot, string iterationId, string taskId, int expectedRevision, string requestXml,
        string version = "1.0", bool dryRun = false)
    {
        if (expectedRevision <= 0 || string.IsNullOrWhiteSpace(requestXml))
        {
            return Failure(DiagnosticCodes.InvalidArgument, "A positive expected revision and non-empty review request are required.");
        }
        var (workspaceSafe, workspaceError) = PathSecurity.VerifyWorkspaceDirectorySecurity(workspaceRoot);
        if (!workspaceSafe || workspaceError != null)
        {
            return (false, null, new[] { workspaceError! });
        }
        if (dryRun)
        {
            var dryRunBlocker = WorkspaceTransactionCommitter.GetDryRunBlocker(workspaceRoot);
            if (dryRunBlocker != null)
            {
                return (false, null, new[] { dryRunBlocker });
            }
        }
        var (iterationValid, normalizedIteration, iterationError) = PathSecurity.ValidateIterationId(iterationId);
        if (!iterationValid || iterationError != null)
        {
            return (false, null, new[] { iterationError! });
        }
        if (!ProjectSemanticIndex.IsValidTimeFirstId(taskId))
        {
            return Failure(DiagnosticCodes.InvalidIdGrammar, $"Task ID '{taskId}' is not a valid time-first ID.");
        }
        if (Encoding.UTF8.GetByteCount(requestXml) > XPathQueryLimits.MaxDocumentBytes)
        {
            return Failure(DiagnosticCodes.LimitExceeded, "Task review request exceeds the maximum managed document size.");
        }

        var tasksRelative = $"{normalizedIteration}/tasks.xml";
        var specRelative = $"{normalizedIteration}/spec.xml";
        var tasksPath = Path.Combine(workspaceRoot, tasksRelative.Replace('/', Path.DirectorySeparatorChar));
        var specPath = Path.Combine(workspaceRoot, specRelative.Replace('/', Path.DirectorySeparatorChar));
        foreach (var path in new[] { tasksPath, specPath })
        {
            var (contained, containmentError) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, path);
            if (!contained || containmentError != null)
            {
                return (false, null, new[] { containmentError! });
            }
            if (!File.Exists(path))
            {
                return Failure(DiagnosticCodes.DocumentNotFound, $"Required managed document '{path}' does not exist.");
            }
        }

        var (requestSuccess, request, requestDiagnostics) = ParseRequest(requestXml, tasksRelative, version);
        if (!requestSuccess || request?.Root == null)
        {
            return (false, null, requestDiagnostics);
        }
        var root = request.Root;
        var operationId = (string?)root.Attribute("id") ?? string.Empty;
        var actor = (string?)root.Attribute("actor") ?? string.Empty;
        var occurredAtRaw = (string?)root.Attribute("occurred_at");
        var occurredAt = NormalizeTime(occurredAtRaw);
        var submissionRequest = root.Element("submission")!;
        var submissionId = (string?)submissionRequest.Attribute("id") ?? string.Empty;
        var disposition = (string?)submissionRequest.Attribute("disposition") ?? string.Empty;
        var findingId = (string?)submissionRequest.Attribute("finding_id");
        var summary = submissionRequest.Element("summary")?.Value ?? string.Empty;
        var impact = submissionRequest.Element("impact")?.Value;
        var recordId = disposition == "changes-requested" ? findingId! : submissionId + "-record";
        if (occurredAt == null || !occurredAtRaw!.EndsWith('Z') || string.IsNullOrWhiteSpace(summary))
        {
            return Failure(DiagnosticCodes.InvalidArgument, "Review occurred_at and summary must be valid and non-empty.");
        }
        if ((disposition == "changes-requested") != !string.IsNullOrWhiteSpace(findingId) ||
            (disposition == "changes-requested" && string.IsNullOrWhiteSpace(impact)) ||
            (disposition == "approved" && !string.IsNullOrWhiteSpace(impact)))
        {
            return Failure(DiagnosticCodes.InvalidArgument,
                "changes-requested requires finding_id and non-empty impact; approved permits neither finding_id nor impact.");
        }

        XDocument tasks;
        XDocument spec;
        try
        {
            tasks = Load(tasksPath);
            spec = Load(specPath);
        }
        catch (Exception ex)
        {
            return Failure(DiagnosticCodes.XmlParseError, $"Unable to read review documents: {ex.Message}");
        }
        if (!TryRevision(tasks, out var actualRevision) || !TryRevision(spec, out var specRevision))
        {
            return Failure(DiagnosticCodes.XmlParseError, "Review documents have invalid revisions.");
        }
        var taskMatches = tasks.Root?.Elements("task")
            .Where(t => string.Equals((string?)t.Attribute("id"), taskId, StringComparison.Ordinal)).ToList()
            ?? new List<XElement>();
        if (taskMatches.Count != 1)
        {
            return Failure(DiagnosticCodes.CardinalityConflict, $"Task '{taskId}' must resolve exactly once.");
        }
        var task = taskMatches[0];
        var review = task.Element("review");
        if (review == null || !bool.TryParse((string?)review.Attribute("required"), out var required) || !required)
        {
            return Failure(DiagnosticCodes.TaskReviewRequired, $"Task '{taskId}' is not configured with review required=true.");
        }
        var implementer = (string?)task.Attribute("agent");
        if (string.IsNullOrWhiteSpace(implementer))
        {
            return Failure(DiagnosticCodes.TaskReviewImplementerUnknown,
                $"Review-required task '{taskId}' has no declared @agent implementer attribution.");
        }

        var fingerprint = Fingerprint(root);
        var (enumerated, documents, enumerationDiagnostics) = WorkspaceDiscovery.EnumerateDocuments(workspaceRoot);
        if (!enumerated || enumerationDiagnostics.Count > 0)
        {
            return (false, null, enumerationDiagnostics);
        }
        var operationOccurrences = new List<(ManagedDocument Document, XElement Element, string? TaskId)>();
        foreach (var document in documents)
        {
            try
            {
                using var stream = File.OpenRead(document.FullPath);
                using var reader = SecureXmlReaderFactory.CreateReader(stream);
                var loaded = XDocument.Load(reader);
                foreach (var element in loaded.Descendants()
                             .Where(e => string.Equals((string?)e.Attribute("operation_id"), operationId, StringComparison.Ordinal)))
                {
                    operationOccurrences.Add((document, element,
                        element.Ancestors("task").FirstOrDefault()?.Attribute("id")?.Value));
                }
            }
            catch
            {
                // Prospective workspace validation reports malformed managed documents.
            }
        }
        foreach (var occurrence in operationOccurrences)
        {
            if (!string.Equals(occurrence.Document.RelativePath, tasksRelative, StringComparison.Ordinal) ||
                !string.Equals(occurrence.TaskId, taskId, StringComparison.Ordinal) ||
                !string.Equals(occurrence.Element.Name.LocalName, "record", StringComparison.Ordinal) ||
                !string.Equals(occurrence.Element.Parent?.Name.LocalName, "records", StringComparison.Ordinal))
            {
                return Failure(DiagnosticCodes.IdempotencyConflict,
                    $"Review operation '{operationId}' is already used outside the target task record set in '{occurrence.Document.RelativePath}'.");
            }
        }
        var existingReceipts = operationOccurrences.Select(o => o.Element).ToList();
        if (existingReceipts.Count > 0)
        {
            var receipt = existingReceipts.FirstOrDefault();
            var storedFingerprint = Term(receipt, "request-sha256");
            var existingSubmission = review.Elements("submission")
                .FirstOrDefault(s => string.Equals((string?)s.Attribute("id"), submissionId, StringComparison.Ordinal));
            var revisionAccepted = expectedRevision == actualRevision || expectedRevision == actualRevision - 1;
            if (existingReceipts.Count != 1 || receipt == null || existingSubmission == null ||
                !string.Equals(storedFingerprint, fingerprint, StringComparison.Ordinal) ||
                !string.Equals((string?)existingSubmission.Attribute("record"), (string?)receipt.Attribute("id"), StringComparison.Ordinal) ||
                !revisionAccepted)
            {
                return Failure(DiagnosticCodes.IdempotencyConflict,
                    $"Review operation '{operationId}' was already used with different semantics or revision context.");
            }
            return (true, new MutationEnvelope("task review",
                new[] { new MutatedDocument(tasksRelative, actualRevision) }, alreadyApplied: true), Array.Empty<Diagnostic>());
        }
        var newIds = new[] { submissionId, recordId };
        if (newIds.Distinct(StringComparer.Ordinal).Count() != newIds.Length)
        {
            return Failure(DiagnosticCodes.IdempotencyConflict, "Task review submission and record IDs must be distinct.");
        }
        foreach (var newId in newIds)
        {
            foreach (var document in documents)
            {
                try
                {
                    using var stream = File.OpenRead(document.FullPath);
                    using var reader = SecureXmlReaderFactory.CreateReader(stream);
                    var loaded = XDocument.Load(reader);
                    if (loaded.Descendants().Any(e => string.Equals((string?)e.Attribute("id"), newId, StringComparison.Ordinal)))
                    {
                        return Failure(DiagnosticCodes.IdempotencyConflict,
                            $"Review element ID '{newId}' already exists in document '{document.RelativePath}'.");
                    }
                }
                catch
                {
                    // Prospective workspace validation reports malformed managed documents.
                }
            }
        }
        if (expectedRevision != actualRevision)
        {
            return (false, null, new[] { new Diagnostic(DiagnosticCodes.RevisionConflict, "error",
                $"Expected revision {expectedRevision} does not match actual revision {actualRevision}.", tasksRelative,
                ExpectedRevision: expectedRevision, ActualRevision: actualRevision) });
        }
        var reviewTime = DateTimeOffset.Parse(occurredAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        foreach (var attributeName in new[] { "created_at", "updated_at" })
        {
            var taskTimeRaw = (string?)task.Attribute(attributeName);
            if (!string.IsNullOrWhiteSpace(taskTimeRaw) &&
                DateTimeOffset.TryParse(taskTimeRaw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var taskTime) &&
                reviewTime < taskTime)
            {
                return Failure(DiagnosticCodes.InvalidArgument,
                    $"task-review @occurred_at '{occurredAt}' cannot be earlier than task {attributeName} '{taskTimeRaw}'.");
            }
        }
        if (!string.Equals((string?)spec.Root?.Attribute("status"), "active", StringComparison.Ordinal))
        {
            return Failure(DiagnosticCodes.IterationReplanningExecutionFrozen, "Task review requires an active iteration.");
        }
        if (!string.Equals((string?)task.Attribute("status"), "verification", StringComparison.Ordinal))
        {
            return Failure(DiagnosticCodes.TaskReviewStateInvalid, "New review submissions require task status verification.");
        }
        if (disposition == "approved" && string.Equals(actor, implementer, StringComparison.Ordinal))
        {
            return Failure(DiagnosticCodes.TaskReviewActorConflict,
                "Approval actor must differ from the task @agent implementer attribution. This is provenance separation, not authenticated identity proof.");
        }
        if (disposition == "approved" && task.Element("records")?.Elements("record")
                .Any(r => string.Equals((string?)r.Attribute("kind"), "finding", StringComparison.Ordinal) &&
                          string.Equals((string?)r.Attribute("status"), "active", StringComparison.Ordinal)) == true)
        {
            return Failure(DiagnosticCodes.TaskActiveFindingBlocksCompletion,
                "Approval cannot be recorded while the task has active findings.");
        }

        var record = new XElement("record",
            new XAttribute("id", recordId),
            new XAttribute("kind", disposition == "approved" ? "decision" : "finding"),
            new XAttribute("status", disposition == "approved" ? "informational" : "active"),
            new XAttribute("created_at", occurredAt),
            new XAttribute("actor", actor),
            new XAttribute("operation_id", operationId),
            new XElement("index",
                new XElement("summary", "Structured task review submission."),
                new XElement("term", new XAttribute("key", "review-disposition"), new XAttribute("value", disposition)),
                new XElement("term", new XAttribute("key", "request-sha256"), new XAttribute("value", fingerprint))),
            new XElement("summary", summary),
            disposition == "changes-requested" ? new XElement("impact", impact) : null);
        var submission = new XElement("submission",
            new XAttribute("id", submissionId),
            new XAttribute("disposition", disposition),
            new XAttribute("actor", actor),
            new XAttribute("reviewed_at", occurredAt),
            new XAttribute("record", recordId));
        review.Add(submission);
        task.Element("records")!.Add(record);
        task.SetAttributeValue("updated_at", occurredAt);
        if (disposition == "changes-requested")
        {
            task.SetAttributeValue("status", "in-progress");
            StatusTermHelper.SynchronizeStatusTerm(task, "in-progress");
        }
        tasks.Root!.SetAttributeValue("revision", actualRevision + 1);
        var operation = new TransactionDocumentOperation(tasksRelative, Serialize(tasks), actualRevision, actualRevision + 1);
        return WorkspaceTransactionCommitter.Commit(workspaceRoot, "task review", new[] { operation },
            readPreconditions: new[] { new TransactionReadPrecondition(specRelative, specRevision) },
            dryRun: dryRun);
    }

    private static (bool Success, XDocument? Document, IReadOnlyList<Diagnostic> Diagnostics) ParseRequest(
        string xml, string document, string version)
    {
        var diagnostics = new List<Diagnostic>();
        var settings = SecureXmlReaderFactory.CreateSecureSettings(
            EmbeddedResources.GetCompiledSchemaSet("requests", version),
            (_, args) => diagnostics.Add(args.Severity == XmlSeverityType.Error
                ? Diagnostic.Error(DiagnosticCodes.SchemaValidationError, args.Message, document,
                    args.Exception?.LineNumber, args.Exception?.LinePosition)
                : Diagnostic.Warning(DiagnosticCodes.SchemaValidationError, args.Message, document,
                    args.Exception?.LineNumber, args.Exception?.LinePosition)));
        try
        {
            using var stringReader = new StringReader(xml);
            using var reader = SecureXmlReaderFactory.CreateReader(stringReader, settings);
            var parsed = XDocument.Load(reader, LoadOptions.SetLineInfo);
            if (diagnostics.Any(d => d.Severity == "error") || parsed.Root?.Name.LocalName != "task-review")
            {
                return (false, null, diagnostics.Count == 0
                    ? new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Root element must be task-review.") }
                    : diagnostics);
            }
            return (true, parsed, diagnostics);
        }
        catch (Exception ex)
        {
            return (false, null, new[] { Diagnostic.Error(
                ex.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase) ? DiagnosticCodes.DtdProhibited : DiagnosticCodes.XmlParseError,
                $"Unable to parse task review request: {ex.Message}", document) });
        }
    }

    private static XDocument Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = SecureXmlReaderFactory.CreateReader(stream);
        return XDocument.Load(reader, LoadOptions.SetLineInfo);
    }

    private static bool TryRevision(XDocument document, out int revision) =>
        int.TryParse(document.Root?.Attribute("revision")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out revision) && revision > 0;

    private static string? NormalizeTime(string? raw) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
            ? value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
            : null;

    private static string? Term(XElement? record, string key) => record?.Element("index")?.Elements("term")
        .FirstOrDefault(t => string.Equals((string?)t.Attribute("key"), key, StringComparison.Ordinal))?.Attribute("value")?.Value;

    private static string Fingerprint(XElement request)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(GenericAppender.ToCanonicalXmlString(request)));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Serialize(XDocument document) =>
        ManagedDocumentSerializer.Serialize(document);

    private static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Failure(string code, string message) =>
        (false, null, new[] { Diagnostic.Error(code, message) });
}
