using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Resources;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Serialization;
using DogdouSpec.Core.Time;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;
using DogdouSpec.Core.XPath;

namespace DogdouSpec.Core.Iterations;

/// <summary>
/// Authoritative execution engine for iteration confirmation operations.
/// Validates confirmation requests against requests.xsd, checks exact spec and tasks revisions,
/// validates timestamps and prevents backdating, applies explicit product decisions,
/// enforces gating constraints on activation and completion, appends protected confirmation provenance,
/// and atomically commits mutations exclusively to spec.xml (preserving tasks.xml byte-identically).
/// Supports durable idempotency via persisted confirmation ID and deep state verification.
/// </summary>
public static class IterationConfirmer
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly Regex UtcIso8601Regex = new(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$", RegexOptions.Compiled);

    public static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Confirm(
        string workspaceRoot,
        string requestXml,
        IClock? clock = null,
        IFaultInjector? faultInjector = null,
        string version = "1.0")
    {
        clock ??= SystemClock.Instance;

        // 1. Validate basic inputs and request size bounds
        if (string.IsNullOrWhiteSpace(requestXml))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Iteration confirmation request XML cannot be empty.") });
        }

        if (Encoding.UTF8.GetByteCount(requestXml) > XPathQueryLimits.MaxDocumentBytes)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"Iteration confirmation XML request exceeds maximum allowed size of {XPathQueryLimits.MaxDocumentBytes} bytes.") });
        }

        // 2. Validate workspace security
        var (isWsSafe, wsErr) = PathSecurity.VerifyWorkspaceDirectorySecurity(workspaceRoot);
        if (!isWsSafe || wsErr != null)
        {
            return (false, null, new[] { wsErr ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, "Workspace directory security verification failed.") });
        }

        // 3. Schema validation against requests.xsd
        var schemaSet = EmbeddedResources.GetCompiledSchemaSet("requests", version);
        var schemaDiagnostics = new List<Diagnostic>();
        var settings = SecureXmlReaderFactory.CreateSecureSettings(
            schemaSet: schemaSet,
            validationEventHandler: (sender, args) =>
            {
                var line = args.Exception?.LineNumber;
                var col = args.Exception?.LinePosition;
                var code = DiagnosticCodes.SchemaValidationError;

                var diag = args.Severity == XmlSeverityType.Error
                    ? Diagnostic.Error(code, args.Message, null, line, col)
                    : Diagnostic.Warning(code, args.Message, null, line, col);

                schemaDiagnostics.Add(diag);
            });

        XDocument requestDoc;
        try
        {
            using var sr = new StringReader(requestXml);
            using var reader = SecureXmlReaderFactory.CreateReader(sr, settings);
            requestDoc = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (XmlException xmlEx)
        {
            var code = xmlEx.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase)
                ? DiagnosticCodes.DtdProhibited
                : DiagnosticCodes.XmlParseError;
            return (false, null, new[] { Diagnostic.Error(code, $"Failed to parse iteration-confirmation XML request: {xmlEx.Message}", null, xmlEx.LineNumber, xmlEx.LinePosition) });
        }
        catch (Exception ex)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Failed to parse iteration-confirmation XML request: {ex.Message}") });
        }

        if (schemaDiagnostics.Any(d => d.Severity == "error"))
        {
            return (false, null, schemaDiagnostics);
        }

        var reqRoot = requestDoc.Root;
        if (reqRoot == null || !string.Equals(reqRoot.Name.LocalName, "iteration-confirmation", StringComparison.Ordinal))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Request root element must be <iteration-confirmation>.") });
        }

        // 4. Extract and validate request fields
        var id = reqRoot.Attribute("id")?.Value ?? string.Empty;
        if (!ProjectSemanticIndex.IsValidTimeFirstId(id))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, $"Confirmation identifier '{id}' does not conform to the time-first ID grammar.") });
        }

        var iterationId = reqRoot.Attribute("iteration")?.Value ?? string.Empty;
        var (isIterValid, normIterId, iterErr) = WorkspaceDiscovery.ValidateIterationId(iterationId);
        if (!isIterValid || iterErr != null)
        {
            return (false, null, new[] { iterErr ?? Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid iteration identifier '{iterationId}'.") });
        }

        var action = reqRoot.Attribute("action")?.Value ?? string.Empty;
        var allowedActions = new[] { "activate", "accept-design-change", "continue", "replan", "complete", "cancel", "supersede" };
        if (!allowedActions.Contains(action, StringComparer.Ordinal))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid confirmation action '{action}'. Allowed actions: {string.Join(", ", allowedActions)}.") });
        }

        var expectedSpecRevStr = reqRoot.Attribute("expected_spec_revision")?.Value;
        if (string.IsNullOrWhiteSpace(expectedSpecRevStr) || !int.TryParse(expectedSpecRevStr, CultureInfo.InvariantCulture, out var expectedSpecRev) || expectedSpecRev <= 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "expected_spec_revision must be a positive integer.") });
        }

        int? expectedTasksRev = null;
        var expectedTasksRevStr = reqRoot.Attribute("expected_tasks_revision")?.Value;
        if (!string.IsNullOrWhiteSpace(expectedTasksRevStr))
        {
            if (!int.TryParse(expectedTasksRevStr, CultureInfo.InvariantCulture, out var tasksRevVal) || tasksRevVal <= 0)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "expected_tasks_revision must be a positive integer when specified.") });
            }
            expectedTasksRev = tasksRevVal;
        }

        // For action='complete', expected_tasks_revision is required
        if (string.Equals(action, "complete", StringComparison.Ordinal) && !expectedTasksRev.HasValue)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "expected_tasks_revision is required when confirming iteration completion.") });
        }

        var actor = reqRoot.Attribute("actor")?.Value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(actor))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "actor attribute cannot be empty.") });
        }

        var decidedAtRaw = reqRoot.Attribute("decided_at")?.Value ?? string.Empty;
        if (!UtcIso8601Regex.IsMatch(decidedAtRaw) || !DateTimeOffset.TryParseExact(decidedAtRaw, "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture, DateTimeStyles.None, out var decidedAt))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"decided_at timestamp '{decidedAtRaw}' must be in canonical UTC ISO 8601 format (yyyy-MM-ddTHH:mm:ssZ).") });
        }

        var summary = reqRoot.Element("summary")?.Value;
        if (string.IsNullOrWhiteSpace(summary))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Confirmation summary cannot be empty.") });
        }

        var rationale = reqRoot.Element("rationale")?.Value;

        var newDesignDecisionEl = reqRoot.Element("new_design_decision");
        var requirementsEl = reqRoot.Element("requirements");
        var questionsEl = reqRoot.Element("questions");
        var designEl = reqRoot.Element("design");
        var acceptanceEl = reqRoot.Element("acceptance");

        // 5. Target document paths, containment, and size bounds
        var normSpecDocPath = $"{normIterId}/spec.xml";
        var normTasksDocPath = $"{normIterId}/tasks.xml";

        var (isSpecRelValid, _, specRelErr) = PathSecurity.ValidateRelativeDocumentPath(normSpecDocPath);
        if (!isSpecRelValid || specRelErr != null)
        {
            return (false, null, new[] { specRelErr ?? Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid document path '{normSpecDocPath}'.") });
        }

        var (isTasksRelValid, _, tasksRelErr) = PathSecurity.ValidateRelativeDocumentPath(normTasksDocPath);
        if (!isTasksRelValid || tasksRelErr != null)
        {
            return (false, null, new[] { tasksRelErr ?? Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid document path '{normTasksDocPath}'.") });
        }

        var fullSpecPath = Path.Combine(workspaceRoot, normSpecDocPath.Replace('/', Path.DirectorySeparatorChar));
        var (isSpecContained, specContErr) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, fullSpecPath);
        if (!isSpecContained || specContErr != null)
        {
            return (false, null, new[] { specContErr ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, $"Target path escapes workspace: '{normSpecDocPath}'.") });
        }

        var fullTasksPath = Path.Combine(workspaceRoot, normTasksDocPath.Replace('/', Path.DirectorySeparatorChar));
        var (isTasksContained, tasksContErr) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, fullTasksPath);
        if (!isTasksContained || tasksContErr != null)
        {
            return (false, null, new[] { tasksContErr ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, $"Target path escapes workspace: '{normTasksDocPath}'.") });
        }

        if (!File.Exists(fullSpecPath))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Document '{normSpecDocPath}' does not exist in workspace.", normSpecDocPath) });
        }

        if (!File.Exists(fullTasksPath))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Document '{normTasksDocPath}' does not exist in workspace.", normTasksDocPath) });
        }

        var specFileInfo = new FileInfo(fullSpecPath);
        if (specFileInfo.Length > XPathQueryLimits.MaxDocumentBytes)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"Document '{normSpecDocPath}' exceeds maximum allowed size of {XPathQueryLimits.MaxDocumentBytes} bytes.", normSpecDocPath) });
        }

        var tasksFileInfo = new FileInfo(fullTasksPath);
        if (tasksFileInfo.Length > XPathQueryLimits.MaxDocumentBytes)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"Document '{normTasksDocPath}' exceeds maximum allowed size of {XPathQueryLimits.MaxDocumentBytes} bytes.", normTasksDocPath) });
        }

        // 6. Read current spec.xml and tasks.xml
        XDocument currentSpecDoc;
        XDocument currentTasksDoc;
        try
        {
            using var specStream = File.OpenRead(fullSpecPath);
            using var specReader = SecureXmlReaderFactory.CreateReader(specStream);
            currentSpecDoc = XDocument.Load(specReader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);

            using var tasksStream = File.OpenRead(fullTasksPath);
            using var tasksReader = SecureXmlReaderFactory.CreateReader(tasksStream);
            currentTasksDoc = XDocument.Load(tasksReader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (Exception ex)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Failed to load XML documents: {ex.Message}", normSpecDocPath) });
        }

        var specRoot = currentSpecDoc.Root;
        var tasksRoot = currentTasksDoc.Root;
        if (specRoot == null || tasksRoot == null)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, "Document root is null.", normSpecDocPath) });
        }

        var actualSpecRevStr = specRoot.Attribute("revision")?.Value;
        if (string.IsNullOrWhiteSpace(actualSpecRevStr) || !int.TryParse(actualSpecRevStr, CultureInfo.InvariantCulture, out var actualSpecRev) || actualSpecRev <= 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Invalid or missing root revision in '{normSpecDocPath}'.", normSpecDocPath) });
        }

        var actualTasksRevStr = tasksRoot.Attribute("revision")?.Value;
        if (string.IsNullOrWhiteSpace(actualTasksRevStr) || !int.TryParse(actualTasksRevStr, CultureInfo.InvariantCulture, out var actualTasksRev) || actualTasksRev <= 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Invalid or missing root revision in '{normTasksDocPath}'.", normTasksDocPath) });
        }

        var currentStatus = specRoot.Attribute("status")?.Value ?? "draft";
        var currentKind = specRoot.Attribute("kind")?.Value ?? "feature";
        var specUpdatedAtRaw = specRoot.Attribute("updated_at")?.Value;

        DateTimeOffset specUpdatedAt = DateTimeOffset.MinValue;
        if (!string.IsNullOrWhiteSpace(specUpdatedAtRaw))
        {
            DateTimeOffset.TryParseExact(specUpdatedAtRaw, "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture, DateTimeStyles.None, out specUpdatedAt);
        }

        // 7. Duplicate target validation in request (prevents unhandled exceptions)
        if (requirementsEl != null)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in requirementsEl.Elements("requirement"))
            {
                var target = r.Attribute("target")?.Value ?? string.Empty;
                if (!seen.Add(target))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DuplicateConfirmationTarget, $"Duplicate confirmation target '{target}' in <requirements>.", normSpecDocPath) });
                }
            }
        }

        if (questionsEl != null)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var q in questionsEl.Elements("question"))
            {
                var target = q.Attribute("target")?.Value ?? string.Empty;
                if (!seen.Add(target))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DuplicateConfirmationTarget, $"Duplicate confirmation target '{target}' in <questions>.", normSpecDocPath) });
                }
            }
        }

        if (designEl != null)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in designEl.Elements("decision"))
            {
                var target = d.Attribute("target")?.Value ?? string.Empty;
                if (!seen.Add(target))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DuplicateConfirmationTarget, $"Duplicate confirmation target '{target}' in <design>.", normSpecDocPath) });
                }
            }
        }

        if (acceptanceEl != null)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in acceptanceEl.Elements("criterion"))
            {
                var target = a.Attribute("target")?.Value ?? string.Empty;
                if (!seen.Add(target))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DuplicateConfirmationTarget, $"Duplicate confirmation target '{target}' in <acceptance>.", normSpecDocPath) });
                }
            }
        }

        // 8. Durable Idempotency Check (verifies persisted confirmation, canonical content, and live document state)
        var existingConfs = specRoot.Element("confirmations")?.Elements("confirmation").ToList() ?? new List<XElement>();
        var existingConfWithId = existingConfs.FirstOrDefault(c => string.Equals(c.Attribute("id")?.Value, id, StringComparison.Ordinal));

        if (existingConfWithId != null)
        {
            // 8.1 Verify that the document timestamp has not drifted due to subsequent confirmations
            if (!string.Equals(specUpdatedAtRaw, decidedAtRaw, StringComparison.Ordinal))
            {
                return (false, null, new[] { Diagnostic.Error(
                    DiagnosticCodes.IdempotencyConflict,
                    $"Confirmation with ID '{id}' was previously applied at '{decidedAtRaw}', but the document updated_at timestamp is now '{specUpdatedAtRaw}'. Replay conflicts with subsequent document modifications.") });
            }

            // 8.2 Check expected revision match against pre-commit or current revision
            var isExpectedRevMatch = (expectedSpecRev == actualSpecRev) || (expectedSpecRev == actualSpecRev - 1);
            if (!isExpectedRevMatch)
            {
                return (false, null, new[] { new Diagnostic(
                    DiagnosticCodes.RevisionConflict,
                    "error",
                    $"Expected spec revision {expectedSpecRev} does not match actual revision {actualSpecRev} or pre-commit revision {actualSpecRev - 1}.",
                    Document: normSpecDocPath,
                    ExpectedRevision: expectedSpecRev,
                    ActualRevision: actualSpecRev) });
            }

            if (expectedTasksRev.HasValue && expectedTasksRev.Value != actualTasksRev)
            {
                return (false, null, new[] { new Diagnostic(
                    DiagnosticCodes.RevisionConflict,
                    "error",
                    $"Expected tasks revision {expectedTasksRev.Value} does not match actual tasks revision {actualTasksRev}.",
                    Document: normTasksDocPath,
                    ExpectedRevision: expectedTasksRev.Value,
                    ActualRevision: actualTasksRev) });
            }

            // 8.3 Check exact match of confirmation attributes & child elements
            bool match = true;
            if (!string.Equals(existingConfWithId.Attribute("action")?.Value, action, StringComparison.Ordinal) ||
                !string.Equals(existingConfWithId.Attribute("actor")?.Value, actor, StringComparison.Ordinal) ||
                !string.Equals(existingConfWithId.Attribute("decided_at")?.Value, decidedAtRaw, StringComparison.Ordinal) ||
                !string.Equals(existingConfWithId.Attribute("decision")?.Value, "accepted", StringComparison.Ordinal))
            {
                match = false;
            }

            var existingSummary = existingConfWithId.Element("summary")?.Value?.Trim();
            if (!string.Equals(existingSummary, summary?.Trim(), StringComparison.Ordinal))
            {
                match = false;
            }

            var existingRationale = existingConfWithId.Element("rationale")?.Value?.Trim();
            if (!string.Equals(existingRationale ?? string.Empty, rationale?.Trim() ?? string.Empty, StringComparison.Ordinal))
            {
                match = false;
            }

            var reqList = requirementsEl?.Elements("requirement") ?? Enumerable.Empty<XElement>();
            var existingReqList = existingConfWithId.Element("requirements")?.Elements("requirement") ?? Enumerable.Empty<XElement>();
            if (!AreTargetDecisionElementsMatching(reqList, existingReqList))
            {
                match = false;
            }

            var qList = questionsEl?.Elements("question") ?? Enumerable.Empty<XElement>();
            var existingQList = existingConfWithId.Element("questions")?.Elements("question") ?? Enumerable.Empty<XElement>();
            if (!AreTargetDecisionElementsMatching(qList, existingQList))
            {
                match = false;
            }

            var dList = designEl?.Elements("decision") ?? Enumerable.Empty<XElement>();
            var existingDList = existingConfWithId.Element("design")?.Elements("decision") ?? Enumerable.Empty<XElement>();
            if (!AreTargetDecisionElementsMatching(dList, existingDList))
            {
                match = false;
            }

            var aList = acceptanceEl?.Elements("criterion") ?? Enumerable.Empty<XElement>();
            var existingAList = existingConfWithId.Element("acceptance")?.Elements("criterion") ?? Enumerable.Empty<XElement>();
            if (!AreTargetDecisionElementsMatching(aList, existingAList))
            {
                match = false;
            }

            // 8.4 Check new_design_decision presence and canonical content
            if (newDesignDecisionEl != null)
            {
                var newId = newDesignDecisionEl.Attribute("id")?.Value;
                var embeddedStatus = newDesignDecisionEl.Attribute("status")?.Value;
                if (!string.Equals(embeddedStatus, "proposed", StringComparison.Ordinal))
                {
                    match = false;
                }

                var specDecision = specRoot.Element("design")?.Element("decisions")?.Elements("decision")
                    .FirstOrDefault(d => string.Equals(d.Attribute("id")?.Value, newId, StringComparison.Ordinal));
                if (!IsNewDesignDecisionMatching(newDesignDecisionEl, specDecision))
                {
                    match = false;
                }
            }

            // 8.5 Verify current live state matches all requested decisions (no state drift)
            foreach (var reqEl in reqList)
            {
                var target = reqEl.Attribute("target")?.Value;
                var expectedDec = reqEl.Attribute("decision")?.Value;
                var liveReq = specRoot.Element("product")?.Element("requirements")?.Elements("requirement")
                    .FirstOrDefault(r => string.Equals(r.Attribute("id")?.Value, target, StringComparison.Ordinal));
                if (liveReq == null || !string.Equals(liveReq.Attribute("status")?.Value, expectedDec, StringComparison.Ordinal))
                {
                    match = false;
                }
            }

            foreach (var qEl in qList)
            {
                var target = qEl.Attribute("target")?.Value;
                var expectedDec = qEl.Attribute("decision")?.Value;
                var liveQ = specRoot.Element("research")?.Element("questions")?.Elements("question")
                    .FirstOrDefault(q => string.Equals(q.Attribute("id")?.Value, target, StringComparison.Ordinal));
                if (liveQ == null || !string.Equals(liveQ.Attribute("status")?.Value, expectedDec, StringComparison.Ordinal))
                {
                    match = false;
                }
            }

            foreach (var dEl in dList)
            {
                var target = dEl.Attribute("target")?.Value;
                var expectedDec = dEl.Attribute("decision")?.Value;
                var liveD = specRoot.Element("design")?.Element("decisions")?.Elements("decision")
                    .FirstOrDefault(d => string.Equals(d.Attribute("id")?.Value, target, StringComparison.Ordinal));
                if (liveD == null || !string.Equals(liveD.Attribute("status")?.Value, expectedDec, StringComparison.Ordinal))
                {
                    match = false;
                }
            }

            foreach (var aEl in aList)
            {
                var target = aEl.Attribute("target")?.Value;
                var expectedDec = aEl.Attribute("decision")?.Value;
                var liveA = (specRoot.Element("product")?.Element("acceptance")?.Elements("criterion") ??
                             specRoot.Element("research")?.Element("acceptance")?.Elements("criterion") ??
                             Enumerable.Empty<XElement>())
                             .FirstOrDefault(c => string.Equals(c.Attribute("id")?.Value, target, StringComparison.Ordinal));
                if (liveA == null || !string.Equals(liveA.Attribute("decision")?.Value, expectedDec, StringComparison.Ordinal))
                {
                    match = false;
                }
            }

            // 8.6 Check lifecycle state outcome matches
            string expectedStatus = action switch
            {
                "activate" => "active",
                "continue" => "active",
                "replan" => "replanning",
                "complete" => "completed",
                "cancel" => "cancelled",
                "supersede" => "superseded",
                "accept-design-change" => currentStatus,
                _ => currentStatus
            };

            if (!string.Equals(currentStatus, expectedStatus, StringComparison.Ordinal))
            {
                match = false;
            }

            if (string.Equals(action, "complete", StringComparison.Ordinal))
            {
                var completedAtAttr = specRoot.Attribute("completed_at")?.Value;
                if (!string.Equals(completedAtAttr, decidedAtRaw, StringComparison.Ordinal))
                {
                    match = false;
                }
            }

            if (match)
            {
                var alreadyAppliedEnvelope = new MutationEnvelope(
                    "iteration confirm",
                    new[] { new MutatedDocument(normSpecDocPath, actualSpecRev, actualSpecRev) },
                    alreadyApplied: true);

                return (true, alreadyAppliedEnvelope, Array.Empty<Diagnostic>());
            }
            else
            {
                return (false, null, new[] { Diagnostic.Error(
                    DiagnosticCodes.IdempotencyConflict,
                    $"Confirmation with ID '{id}' already exists with different parameters, timestamps, or current state has drifted.") });
            }
        }

        // 9. Preconditions for new confirmation
        // 9.1 Revision check
        if (expectedSpecRev != actualSpecRev)
        {
            return (false, null, new[] { new Diagnostic(
                DiagnosticCodes.RevisionConflict,
                "error",
                $"Expected spec revision {expectedSpecRev} does not match actual revision {actualSpecRev} for document '{normSpecDocPath}'.",
                Document: normSpecDocPath,
                ExpectedRevision: expectedSpecRev,
                ActualRevision: actualSpecRev) });
        }

        if (expectedTasksRev.HasValue && expectedTasksRev.Value != actualTasksRev)
        {
            return (false, null, new[] { new Diagnostic(
                DiagnosticCodes.RevisionConflict,
                "error",
                $"Expected tasks revision {expectedTasksRev.Value} does not match actual tasks revision {actualTasksRev} for document '{normTasksDocPath}'.",
                Document: normTasksDocPath,
                ExpectedRevision: expectedTasksRev.Value,
                ActualRevision: actualTasksRev) });
        }

        // 9.2 Timestamps: no backdating
        if (specUpdatedAt != DateTimeOffset.MinValue && decidedAt < specUpdatedAt)
        {
            return (false, null, new[] { Diagnostic.Error(
                DiagnosticCodes.InvalidArgument,
                $"decided_at timestamp '{decidedAtRaw}' is earlier than iteration updated_at timestamp '{specUpdatedAtRaw}'. Backdating is not permitted.") });
        }

        foreach (var conf in existingConfs)
        {
            var confDecidedAtRaw = conf.Attribute("decided_at")?.Value;
            if (!string.IsNullOrWhiteSpace(confDecidedAtRaw) &&
                DateTimeOffset.TryParseExact(confDecidedAtRaw, "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture, DateTimeStyles.None, out var confDecidedAt))
            {
                if (decidedAt < confDecidedAt)
                {
                    return (false, null, new[] { Diagnostic.Error(
                        DiagnosticCodes.InvalidArgument,
                        $"decided_at timestamp '{decidedAtRaw}' is earlier than existing confirmation timestamp '{confDecidedAtRaw}'. Backdating is not permitted.") });
                }
            }
        }

        // 9.3 Lifecycle State Machine Transitions
        string targetStatus;
        switch (action)
        {
            case "activate":
                if (!string.Equals(currentStatus, "draft", StringComparison.Ordinal))
                {
                    return (false, null, new[] { Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Cannot activate iteration in status '{currentStatus}'. Activation requires status 'draft'.") });
                }
                targetStatus = "active";
                break;

            case "continue":
                if (!string.Equals(currentStatus, "replanning", StringComparison.Ordinal))
                {
                    return (false, null, new[] { Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Cannot continue iteration in status '{currentStatus}'. Continue requires status 'replanning'.") });
                }
                targetStatus = "active";
                break;

            case "replan":
                if (!string.Equals(currentStatus, "active", StringComparison.Ordinal))
                {
                    return (false, null, new[] { Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Cannot replan iteration in status '{currentStatus}'. Replan requires status 'active'.") });
                }
                targetStatus = "replanning";
                break;

            case "complete":
                if (!string.Equals(currentStatus, "active", StringComparison.Ordinal))
                {
                    return (false, null, new[] { Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Cannot complete iteration in status '{currentStatus}'. Complete requires status 'active'.") });
                }
                targetStatus = "completed";
                break;

            case "cancel":
                if (!new[] { "draft", "active", "replanning" }.Contains(currentStatus, StringComparer.Ordinal))
                {
                    return (false, null, new[] { Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Cannot cancel iteration in status '{currentStatus}'. Cancel requires status 'draft', 'active', or 'replanning'.") });
                }
                targetStatus = "cancelled";
                break;

            case "supersede":
                if (!new[] { "draft", "active", "replanning" }.Contains(currentStatus, StringComparer.Ordinal))
                {
                    return (false, null, new[] { Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Cannot supersede iteration in status '{currentStatus}'. Supersede requires status 'draft', 'active', or 'replanning'.") });
                }
                targetStatus = "superseded";
                break;

            case "accept-design-change":
                if (!new[] { "active", "replanning" }.Contains(currentStatus, StringComparer.Ordinal))
                {
                    return (false, null, new[] { Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Cannot accept design change in status '{currentStatus}'. accept-design-change requires status 'active' or 'replanning'.") });
                }
                targetStatus = currentStatus;
                break;

            default:
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Unsupported action '{action}'.") });
        }

        // 9.4 Validate new_design_decision if present
        string? newDesignDecisionId = null;
        if (newDesignDecisionEl != null)
        {
            newDesignDecisionId = newDesignDecisionEl.Attribute("id")?.Value;
            if (string.IsNullOrWhiteSpace(newDesignDecisionId) || !ProjectSemanticIndex.IsValidTimeFirstId(newDesignDecisionId))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, $"new_design_decision identifier '{newDesignDecisionId}' is invalid.") });
            }

            var embeddedStatus = newDesignDecisionEl.Attribute("status")?.Value;
            if (!string.Equals(embeddedStatus, "proposed", StringComparison.Ordinal))
            {
                return (false, null, new[] { Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    $"new_design_decision must have embedded status='proposed', but got '{embeddedStatus}'. Final disposition must be specified via an explicit target in <design>.") });
            }

            // Check if already in specDoc
            var existingDecision = specRoot.Element("design")?.Element("decisions")?.Elements("decision")
                .FirstOrDefault(d => string.Equals(d.Attribute("id")?.Value, newDesignDecisionId, StringComparison.Ordinal));
            if (existingDecision != null)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DuplicateId, $"Design decision with identifier '{newDesignDecisionId}' already exists in '{normSpecDocPath}'.") });
            }
        }

        // 9.5 Validate accept-design-change constraints
        if (action == "accept-design-change")
        {
            if (requirementsEl != null && requirementsEl.Elements("requirement").Any())
            {
                return (false, null, new[] { Diagnostic.Error(
                    DiagnosticCodes.OwnerDecisionRequired,
                    "Action 'accept-design-change' only applies to design decisions (<design>); requirements cannot be decided under accept-design-change. Allowed target for this action: <design>.") });
            }

            if (questionsEl != null && questionsEl.Elements("question").Any())
            {
                return (false, null, new[] { Diagnostic.Error(
                    DiagnosticCodes.OwnerDecisionRequired,
                    "Action 'accept-design-change' only applies to design decisions (<design>); research questions cannot be decided under accept-design-change. Allowed target for this action: <design>.") });
            }

            if (acceptanceEl != null && acceptanceEl.Elements("criterion").Any())
            {
                return (false, null, new[] { Diagnostic.Error(
                    DiagnosticCodes.OwnerDecisionRequired,
                    "Action 'accept-design-change' only applies to design decisions (<design>); acceptance criteria cannot be decided under accept-design-change. Allowed target for this action: <design>.") });
            }

            if (designEl == null || !designEl.Elements("decision").Any())
            {
                return (false, null, new[] { Diagnostic.Error(
                    DiagnosticCodes.OwnerDecisionRequired,
                    "accept-design-change requires at least one design decision to be targeted in <design>.") });
            }

            if (newDesignDecisionEl != null)
            {
                var isNewTargeted = designEl.Elements("decision")
                    .Any(d => string.Equals(d.Attribute("target")?.Value, newDesignDecisionId, StringComparison.Ordinal));
                if (!isNewTargeted)
                {
                    return (false, null, new[] { Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"new_design_decision '{newDesignDecisionId}' was introduced in accept-design-change but is not targeted in <design>.") });
                }
            }
        }

        // 9.6 Validate targets and decisions
        var reqDecisions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (requirementsEl != null)
        {
            var targetElements = requirementsEl.Elements("requirement").Concat(requirementsEl.Elements("target"));
            foreach (var reqEl in targetElements)
            {
                var target = reqEl.Attribute("target")?.Value ?? string.Empty;
                var dec = reqEl.Attribute("decision")?.Value ?? string.Empty;
                var allowedReqDecs = new[] { "approved", "superseded", "withdrawn" };
                if (!allowedReqDecs.Contains(dec, StringComparer.Ordinal))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid requirement decision '{dec}' for target '{target}'. Allowed requirement decisions: {string.Join(", ", allowedReqDecs)}.") });
                }

                var targetEl = specRoot.Element("product")?.Element("requirements")?.Elements("requirement")
                    .FirstOrDefault(r => string.Equals(r.Attribute("id")?.Value, target, StringComparison.Ordinal));
                if (targetEl == null)
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DanglingReference, $"Target requirement '{target}' does not exist in '{normSpecDocPath}'.", normSpecDocPath) });
                }

                reqDecisions[target] = dec;
            }
        }

        var qDecisions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (questionsEl != null)
        {
            var targetElements = questionsEl.Elements("question").Concat(questionsEl.Elements("target"));
            foreach (var qEl in targetElements)
            {
                var target = qEl.Attribute("target")?.Value ?? string.Empty;
                var dec = qEl.Attribute("decision")?.Value ?? string.Empty;
                var allowedQDecs = new[] { "answered", "deferred", "withdrawn" };
                if (!allowedQDecs.Contains(dec, StringComparer.Ordinal))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid question decision '{dec}' for target '{target}'. Allowed question decisions: {string.Join(", ", allowedQDecs)}.") });
                }

                var targetEl = specRoot.Element("research")?.Element("questions")?.Elements("question")
                    .FirstOrDefault(q => string.Equals(q.Attribute("id")?.Value, target, StringComparison.Ordinal));
                if (targetEl == null)
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DanglingReference, $"Target question '{target}' does not exist in '{normSpecDocPath}'.", normSpecDocPath) });
                }

                qDecisions[target] = dec;
            }
        }

        var dDecisions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (designEl != null)
        {
            var targetElements = designEl.Elements("decision").Concat(designEl.Elements("target"));
            foreach (var dEl in targetElements)
            {
                var target = dEl.Attribute("target")?.Value ?? string.Empty;
                var dec = dEl.Attribute("decision")?.Value ?? string.Empty;
                var allowedDDecs = new[] { "accepted", "rejected", "superseded" };
                if (!allowedDDecs.Contains(dec, StringComparer.Ordinal))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid design decision disposition '{dec}' for target '{target}'. Allowed design decision dispositions: {string.Join(", ", allowedDDecs)}.") });
                }

                var isNewTarget = newDesignDecisionId != null && string.Equals(target, newDesignDecisionId, StringComparison.Ordinal);
                var targetEl = specRoot.Element("design")?.Element("decisions")?.Elements("decision")
                    .FirstOrDefault(d => string.Equals(d.Attribute("id")?.Value, target, StringComparison.Ordinal));
                if (targetEl == null && !isNewTarget)
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DanglingReference, $"Target design decision '{target}' does not exist in '{normSpecDocPath}'.", normSpecDocPath) });
                }

                dDecisions[target] = dec;
            }
        }

        var critDecisions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (acceptanceEl != null)
        {
            var targetElements = acceptanceEl.Elements("criterion").Concat(acceptanceEl.Elements("target"));
            foreach (var aEl in targetElements)
            {
                var target = aEl.Attribute("target")?.Value ?? string.Empty;
                var dec = aEl.Attribute("decision")?.Value ?? string.Empty;
                var allowedCritDecs = new[] { "accepted", "rejected", "waived" };
                if (!allowedCritDecs.Contains(dec, StringComparer.Ordinal))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid acceptance criterion decision '{dec}' for target '{target}'. Allowed acceptance criterion decisions: {string.Join(", ", allowedCritDecs)}.") });
                }

                var targetEl = (specRoot.Element("product")?.Element("acceptance")?.Elements("criterion") ??
                               specRoot.Element("research")?.Element("acceptance")?.Elements("criterion") ??
                               Enumerable.Empty<XElement>())
                               .FirstOrDefault(c => string.Equals(c.Attribute("id")?.Value, target, StringComparison.Ordinal));
                if (targetEl == null)
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DanglingReference, $"Target acceptance criterion '{target}' does not exist in '{normSpecDocPath}'.", normSpecDocPath) });
                }

                if (string.Equals(dec, "waived", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(rationale))
                {
                    return (false, null, new[] { Diagnostic.Error(
                        DiagnosticCodes.WaiverRationaleMissing,
                        $"Acceptance criterion '{target}' is waived, but the confirmation request does not contain a non-blank rationale.",
                        normSpecDocPath) });
                }

                critDecisions[target] = dec;
            }
        }

        // 9.7 Activation and Continue Gate Constraints
        if (action == "activate" || action == "continue")
        {
            var reqs = specRoot.Element("product")?.Element("requirements")?.Elements("requirement") ?? Enumerable.Empty<XElement>();
            foreach (var req in reqs)
            {
                var reqId = req.Attribute("id")?.Value ?? string.Empty;
                var finalStatus = reqDecisions.TryGetValue(reqId, out var dec) ? dec : (req.Attribute("status")?.Value ?? "proposed");
                if (string.Equals(finalStatus, "proposed", StringComparison.Ordinal))
                {
                    return (false, null, new[] { Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Activation/continue cannot leave proposed requirements. Requirement '{reqId}' is still in 'proposed' status.") });
                }
            }

            var decs = specRoot.Element("design")?.Element("decisions")?.Elements("decision") ?? Enumerable.Empty<XElement>();
            foreach (var d in decs)
            {
                var decId = d.Attribute("id")?.Value ?? string.Empty;
                var finalStatus = dDecisions.TryGetValue(decId, out var dec) ? dec : (d.Attribute("status")?.Value ?? "proposed");
                if (string.Equals(finalStatus, "proposed", StringComparison.Ordinal))
                {
                    return (false, null, new[] { Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Activation/continue cannot leave proposed design decisions. Design decision '{decId}' is still in 'proposed' status.") });
                }
            }

            if (newDesignDecisionEl != null)
            {
                var finalStatus = dDecisions.TryGetValue(newDesignDecisionId!, out var dec)
                    ? dec
                    : "proposed";
                if (string.Equals(finalStatus, "proposed", StringComparison.Ordinal))
                {
                    return (false, null, new[] { Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Activation/continue cannot leave proposed design decisions. New design decision '{newDesignDecisionId}' is in 'proposed' status.") });
                }
            }

            var allCriteria = (specRoot.Element("product")?.Element("acceptance")?.Elements("criterion") ??
                               specRoot.Element("research")?.Element("acceptance")?.Elements("criterion") ??
                               Enumerable.Empty<XElement>()).ToList();
            if (allCriteria.Count == 0)
            {
                return (false, null, new[] { Diagnostic.Error(
                    DiagnosticCodes.CriterionUndefined,
                    "Iteration activation requires at least one defined acceptance criterion. No criteria found.",
                    normSpecDocPath) });
            }

            foreach (var crit in allCriteria)
            {
                var critId = crit.Attribute("id")?.Value ?? string.Empty;
                var (isValid, reason) = IterationCriterionPolicy.Validate(crit.Value, critId);
                if (!isValid)
                {
                    return (false, null, new[] { Diagnostic.Error(
                        DiagnosticCodes.CriterionUndefined,
                        reason ?? $"Acceptance criterion '{critId}' is undefined or placeholder.",
                        normSpecDocPath) });
                }
            }
        }

        if (action == "continue")
        {
            var tasks = tasksRoot.Descendants("task").ToList();
            foreach (var task in tasks)
            {
                var records = task.Element("records")?.Elements("record") ?? Enumerable.Empty<XElement>();
                var activeFinding = records.FirstOrDefault(r =>
                    string.Equals(r.Attribute("kind")?.Value, "finding", StringComparison.Ordinal) &&
                    string.Equals(r.Attribute("status")?.Value, "active", StringComparison.Ordinal));
                if (activeFinding != null)
                {
                    return (false, null, new[] { Diagnostic.Error(
                        DiagnosticCodes.IterationCompletionPredicateFailed,
                        $"Iteration cannot continue while active change findings remain in task '{task.Attribute("id")?.Value}'. Active findings must be resolved or superseded before continuing.",
                        normTasksDocPath) });
                }
            }

            var requirements = (specRoot.Element("product")?.Element("requirements")?.Elements("requirement") ?? Enumerable.Empty<XElement>()).ToList();
            var specReqStatuses = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var req in requirements)
            {
                var reqId = req.Attribute("id")?.Value ?? string.Empty;
                var finalStatus = reqDecisions.TryGetValue(reqId, out var dec) ? dec : (req.Attribute("status")?.Value ?? "proposed");
                specReqStatuses[reqId] = finalStatus;
            }

            // A replacement is explicit provenance, not an inference from
            // similarly worded requirements. Each superseded requirement needs
            // a finally approved successor and every supersedes edge must point
            // to an extant requirement that is itself finally superseded.
            foreach (var requirement in requirements)
            {
                var requirementId = requirement.Attribute("id")?.Value ?? string.Empty;
                var finalStatus = specReqStatuses[requirementId];
                var successorRefs = requirement.Element("sources")?.Elements("ref")
                    .Where(r => string.Equals(r.Attribute("relation")?.Value, "supersedes", StringComparison.Ordinal))
                    .ToList() ?? new List<XElement>();

                foreach (var source in successorRefs)
                {
                    var oldId = source.Attribute("target")?.Value ?? string.Empty;
                    if (!specReqStatuses.TryGetValue(oldId, out var oldFinalStatus) ||
                        !string.Equals(oldFinalStatus, "superseded", StringComparison.Ordinal))
                    {
                        return (false, null, new[] { Diagnostic.Error(
                            DiagnosticCodes.RequirementSuccessorMissing,
                            $"Requirement '{requirementId}' declares supersedes provenance for '{oldId}', but that target does not exist or is not finally superseded.",
                            normSpecDocPath) });
                    }
                }

                if (string.Equals(finalStatus, "superseded", StringComparison.Ordinal))
                {
                    var hasApprovedSuccessor = requirements.Any(candidate =>
                        string.Equals(specReqStatuses[candidate.Attribute("id")?.Value ?? string.Empty], "approved", StringComparison.Ordinal) &&
                        (candidate.Element("sources")?.Elements("ref") ?? Enumerable.Empty<XElement>()).Any(r =>
                            string.Equals(r.Attribute("relation")?.Value, "supersedes", StringComparison.Ordinal) &&
                            string.Equals(r.Attribute("target")?.Value, requirementId, StringComparison.Ordinal)));
                    if (!hasApprovedSuccessor)
                    {
                        return (false, null, new[] { Diagnostic.Error(
                            DiagnosticCodes.RequirementSuccessorMissing,
                            $"Superseded requirement '{requirementId}' has no finally approved successor requirement with sources/ref relation='supersedes'.",
                            normSpecDocPath) });
                    }
                }
            }

            foreach (var task in tasks)
            {
                var taskStatus = task.Attribute("status")?.Value ?? "pending";
                var isTerminal = string.Equals(taskStatus, "done", StringComparison.Ordinal) ||
                                 string.Equals(taskStatus, "transferred", StringComparison.Ordinal) ||
                                 string.Equals(taskStatus, "superseded", StringComparison.Ordinal) ||
                                 string.Equals(taskStatus, "cancelled", StringComparison.Ordinal);

                if (!isTerminal)
                {
                    var originRefs = task.Element("origin")?.Elements("ref").ToList() ?? new List<XElement>();
                    foreach (var oRef in originRefs)
                    {
                        if (string.Equals(oRef.Attribute("relation")?.Value, "supports", StringComparison.Ordinal) &&
                            string.Equals(oRef.Attribute("target")?.Value, normIterId, StringComparison.Ordinal))
                        {
                            continue;
                        }
                        var targetReqId = oRef.Attribute("target")?.Value ?? string.Empty;
                        if (!specReqStatuses.TryGetValue(targetReqId, out var reqStatus) ||
                            !string.Equals(reqStatus, "approved", StringComparison.Ordinal))
                        {
                            return (false, null, new[] { Diagnostic.Error(
                                DiagnosticCodes.RequirementSuccessorMissing,
                                $"Non-terminal task '{task.Attribute("id")?.Value}' has origin '{targetReqId}' which is not finally approved. Every origin must be approved before continuing iteration.",
                                normTasksDocPath) });
                        }
                    }
                }
            }

            // A newly approved successor created from a proposal needs a live
            // implementation path before the owner resumes execution.
            foreach (var requirement in requirements)
            {
                var requirementId = requirement.Attribute("id")?.Value ?? string.Empty;
                var isApproved = string.Equals(specReqStatuses[requirementId], "approved", StringComparison.Ordinal);
                var isSuccessor = (requirement.Element("sources")?.Elements("ref") ?? Enumerable.Empty<XElement>())
                    .Any(r => string.Equals(r.Attribute("relation")?.Value, "supersedes", StringComparison.Ordinal));
                if (isApproved && isSuccessor)
                {
                    var covered = tasks.Any(task =>
                    {
                        var status = task.Attribute("status")?.Value ?? "pending";
                        var terminal = status is "done" or "transferred" or "superseded" or "cancelled";
                        return !terminal && (task.Element("origin")?.Elements("ref") ?? Enumerable.Empty<XElement>())
                            .Any(r => string.Equals(r.Attribute("target")?.Value, requirementId, StringComparison.Ordinal));
                    });
                    if (!covered)
                    {
                        return (false, null, new[] { Diagnostic.Error(
                            DiagnosticCodes.RequirementSuccessorMissing,
                            $"Approved successor requirement '{requirementId}' has no non-terminal implementation task coverage.",
                            normTasksDocPath) });
                    }
                }
            }
        }

        // 9.8 Completion Gate Constraints
        if (action == "complete")
        {
            // All tasks terminal
            var tasks = tasksRoot.Descendants("task").ToList();
            var nonTerminalTasks = tasks.Where(t =>
            {
                var taskStatus = t.Attribute("status")?.Value;
                return !string.Equals(taskStatus, "done", StringComparison.Ordinal) &&
                       !string.Equals(taskStatus, "transferred", StringComparison.Ordinal) &&
                       !string.Equals(taskStatus, "superseded", StringComparison.Ordinal) &&
                       !string.Equals(taskStatus, "cancelled", StringComparison.Ordinal);
            }).ToList();

            if (nonTerminalTasks.Count > 0)
            {
                return (false, null, new[] { Diagnostic.Error(
                    DiagnosticCodes.IterationCompletionPredicateFailed,
                    $"Iteration completion requires all tasks to be terminal, but task '{nonTerminalTasks[0].Attribute("id")?.Value}' has status '{nonTerminalTasks[0].Attribute("status")?.Value}'.") });
            }

            // Done tasks terminal predicates
            var doneTasks = tasks.Where(t => string.Equals(t.Attribute("status")?.Value, "done", StringComparison.Ordinal)).ToList();
            foreach (var task in doneTasks)
            {
                var taskId = task.Attribute("id")?.Value;
                if (string.IsNullOrWhiteSpace(task.Attribute("completed_at")?.Value))
                {
                    return (false, null, new[] { Diagnostic.Error(
                        DiagnosticCodes.IterationCompletionPredicateFailed,
                        $"Task '{taskId}' is done but missing completed_at timestamp.") });
                }

                var records = task.Element("records")?.Elements("record") ?? Enumerable.Empty<XElement>();
                var hasCompletionRec = records.Any(r => string.Equals(r.Attribute("kind")?.Value, "completion", StringComparison.Ordinal));
                if (!hasCompletionRec)
                {
                    return (false, null, new[] { Diagnostic.Error(
                        DiagnosticCodes.IterationCompletionPredicateFailed,
                        $"Task '{taskId}' is done but lacks a completion record.") });
                }

                var hasActiveFinding = records.Any(r =>
                    string.Equals(r.Attribute("kind")?.Value, "finding", StringComparison.Ordinal) &&
                    string.Equals(r.Attribute("status")?.Value, "active", StringComparison.Ordinal));
                if (hasActiveFinding)
                {
                    return (false, null, new[] { Diagnostic.Error(
                        DiagnosticCodes.IterationCompletionPredicateFailed,
                        $"Task '{taskId}' has an active finding record blocking completion.") });
                }

                var criteria = task.Element("acceptance")?.Elements("criterion") ?? Enumerable.Empty<XElement>();
                var verifyingRecords = records.Where(r =>
                {
                    var kindAttr = r.Attribute("kind")?.Value;
                    return string.Equals(kindAttr, "verification", StringComparison.Ordinal) ||
                           string.Equals(kindAttr, "completion", StringComparison.Ordinal);
                }).ToList();

                foreach (var crit in criteria)
                {
                    var critId = crit.Attribute("id")?.Value;
                    var critResult = crit.Attribute("result")?.Value ?? crit.Attribute("status")?.Value ?? "pending";
                    if (!string.Equals(critResult, "passed", StringComparison.Ordinal) &&
                        !string.Equals(critResult, "not-applicable", StringComparison.Ordinal))
                    {
                        return (false, null, new[] { Diagnostic.Error(
                            DiagnosticCodes.IterationCompletionPredicateFailed,
                            $"Task '{taskId}' criterion '{critId}' has non-terminal result '{critResult}'.") });
                    }

                    var isCovered = verifyingRecords.Any(r =>
                        r.Element("covers")?.Elements("ref")
                            .Any(rf => string.Equals(rf.Attribute("target")?.Value, critId, StringComparison.Ordinal)) == true);
                    if (!isCovered)
                    {
                        return (false, null, new[] { Diagnostic.Error(
                            DiagnosticCodes.IterationCompletionPredicateFailed,
                            $"Task '{taskId}' criterion '{critId}' is not covered by verification or completion records.") });
                    }
                }
            }

            // Check all acceptance criteria decisions
            var allCriteria = (specRoot.Element("product")?.Element("acceptance")?.Elements("criterion") ??
                               specRoot.Element("research")?.Element("acceptance")?.Elements("criterion") ??
                               Enumerable.Empty<XElement>()).ToList();
            if (allCriteria.Count == 0)
            {
                return (false, null, new[] { Diagnostic.Error(
                    DiagnosticCodes.CriterionUndefined,
                    "Iteration completion requires at least one defined acceptance criterion. No criteria found.",
                    normSpecDocPath) });
            }

            foreach (var crit in allCriteria)
            {
                var critId = crit.Attribute("id")?.Value ?? string.Empty;
                var (isValid, reason) = IterationCriterionPolicy.Validate(crit.Value, critId);
                if (!isValid)
                {
                    return (false, null, new[] { Diagnostic.Error(
                        DiagnosticCodes.CriterionUndefined,
                        $"Iteration completion rejected: {reason}",
                        normSpecDocPath) });
                }

                var finalDecision = critDecisions.TryGetValue(critId, out var dec) ? dec : (crit.Attribute("decision")?.Value ?? "pending");
                if (!string.Equals(finalDecision, "accepted", StringComparison.Ordinal) &&
                    !string.Equals(finalDecision, "waived", StringComparison.Ordinal))
                {
                    return (false, null, new[] { Diagnostic.Error(
                        DiagnosticCodes.IterationCompletionPredicateFailed,
                        $"Iteration completion requires all acceptance criteria to be 'accepted' or 'waived', but criterion '{critId}' has decision '{finalDecision}'.") });
                }
            }

            // If research: all questions answered, deferred, or withdrawn
            if (string.Equals(currentKind, "research", StringComparison.Ordinal))
            {
                var questions = specRoot.Element("research")?.Element("questions")?.Elements("question") ?? Enumerable.Empty<XElement>();
                foreach (var q in questions)
                {
                    var qId = q.Attribute("id")?.Value ?? string.Empty;
                    var finalStatus = qDecisions.TryGetValue(qId, out var dec) ? dec : (q.Attribute("status")?.Value ?? "open");
                    if (string.Equals(finalStatus, "open", StringComparison.Ordinal))
                    {
                        return (false, null, new[] { Diagnostic.Error(
                            DiagnosticCodes.IterationCompletionPredicateFailed,
                            $"Research iteration completion requires all research questions to be answered, deferred, or withdrawn, but question '{qId}' is 'open'.") });
                    }
                }
            }

            // Completion rejects any remaining proposed requirement or proposed design decision
            var compReqs = specRoot.Element("product")?.Element("requirements")?.Elements("requirement") ?? Enumerable.Empty<XElement>();
            foreach (var req in compReqs)
            {
                var reqId = req.Attribute("id")?.Value ?? string.Empty;
                var finalStatus = reqDecisions.TryGetValue(reqId, out var dec) ? dec : (req.Attribute("status")?.Value ?? "proposed");
                if (string.Equals(finalStatus, "proposed", StringComparison.Ordinal))
                {
                    return (false, null, new[] { Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Iteration completion cannot leave proposed requirements. Requirement '{reqId}' is still in 'proposed' status.") });
                }
            }

            var compDecs = specRoot.Element("design")?.Element("decisions")?.Elements("decision") ?? Enumerable.Empty<XElement>();
            foreach (var d in compDecs)
            {
                var decId = d.Attribute("id")?.Value ?? string.Empty;
                var finalStatus = dDecisions.TryGetValue(decId, out var dec) ? dec : (d.Attribute("status")?.Value ?? "proposed");
                if (string.Equals(finalStatus, "proposed", StringComparison.Ordinal))
                {
                    return (false, null, new[] { Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Iteration completion cannot leave proposed design decisions. Design decision '{decId}' is still in 'proposed' status.") });
                }
            }

            if (newDesignDecisionEl != null)
            {
                var finalStatus = dDecisions.TryGetValue(newDesignDecisionId!, out var dec) ? dec : "proposed";
                if (string.Equals(finalStatus, "proposed", StringComparison.Ordinal))
                {
                    return (false, null, new[] { Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Iteration completion cannot leave proposed design decisions. New design decision '{newDesignDecisionId}' is in 'proposed' status.") });
                }
            }
        }

        // 10. Clone and mutate working spec document
        var workingSpecDoc = new XDocument(currentSpecDoc);
        var workingSpecRoot = workingSpecDoc.Root!;

        var newSpecRev = actualSpecRev + 1;
        workingSpecRoot.SetAttributeValue("revision", newSpecRev.ToString(CultureInfo.InvariantCulture));
        workingSpecRoot.SetAttributeValue("updated_at", decidedAtRaw);
        workingSpecRoot.SetAttributeValue("status", targetStatus);

        // Update status term in <index> if present
        DogdouSpec.Core.Tasks.StatusTermHelper.SynchronizeStatusTerm(workingSpecRoot, targetStatus);

        if (string.Equals(action, "complete", StringComparison.Ordinal))
        {
            workingSpecRoot.SetAttributeValue("completed_at", decidedAtRaw);
        }

        // Apply new design decision if present
        if (newDesignDecisionEl != null)
        {
            var designContainer = workingSpecRoot.Element("design");
            if (designContainer == null)
            {
                // Create <design> in schema order (after product or research, before confirmations)
                designContainer = new XElement("design",
                    new XElement("overview", "Design decisions"),
                    new XElement("boundaries"),
                    new XElement("decisions"));

                var confirmationsEl = workingSpecRoot.Element("confirmations");
                if (confirmationsEl != null)
                {
                    confirmationsEl.AddBeforeSelf(designContainer);
                }
                else
                {
                    workingSpecRoot.Add(designContainer);
                }
            }

            var decisionsContainer = designContainer.Element("decisions");
            if (decisionsContainer == null)
            {
                decisionsContainer = new XElement("decisions");
                designContainer.Add(decisionsContainer);
            }

            var finalDecisionStatus = dDecisions.TryGetValue(newDesignDecisionId!, out var dStatus) ? dStatus : "proposed";
            var decisionToAppend = new XElement("decision",
                new XAttribute("id", newDesignDecisionId!),
                new XAttribute("status", finalDecisionStatus),
                newDesignDecisionEl.Elements());

            DogdouSpec.Core.Tasks.StatusTermHelper.SynchronizeStatusTerm(decisionToAppend, finalDecisionStatus);
            decisionsContainer.Add(decisionToAppend);
        }

        // Apply explicit decisions to working document
        var workingReqs = workingSpecRoot.Element("product")?.Element("requirements")?.Elements("requirement") ?? Enumerable.Empty<XElement>();
        foreach (var (target, dec) in reqDecisions)
        {
            var reqEl = workingReqs.FirstOrDefault(r => string.Equals(r.Attribute("id")?.Value, target, StringComparison.Ordinal));
            if (reqEl != null)
            {
                reqEl.SetAttributeValue("status", dec);
                DogdouSpec.Core.Tasks.StatusTermHelper.SynchronizeStatusTerm(reqEl, dec);
            }
        }

        var workingQs = workingSpecRoot.Element("research")?.Element("questions")?.Elements("question") ?? Enumerable.Empty<XElement>();
        foreach (var (target, dec) in qDecisions)
        {
            var qEl = workingQs.FirstOrDefault(q => string.Equals(q.Attribute("id")?.Value, target, StringComparison.Ordinal));
            if (qEl != null)
            {
                qEl.SetAttributeValue("status", dec);
                DogdouSpec.Core.Tasks.StatusTermHelper.SynchronizeStatusTerm(qEl, dec);
            }
        }

        var workingDecs = workingSpecRoot.Element("design")?.Element("decisions")?.Elements("decision") ?? Enumerable.Empty<XElement>();
        foreach (var (target, dec) in dDecisions)
        {
            var dEl = workingDecs.FirstOrDefault(d => string.Equals(d.Attribute("id")?.Value, target, StringComparison.Ordinal));
            if (dEl != null)
            {
                dEl.SetAttributeValue("status", dec);
                DogdouSpec.Core.Tasks.StatusTermHelper.SynchronizeStatusTerm(dEl, dec);
            }
        }

        var workingCrits = (workingSpecRoot.Element("product")?.Element("acceptance")?.Elements("criterion") ??
                            workingSpecRoot.Element("research")?.Element("acceptance")?.Elements("criterion") ??
                            Enumerable.Empty<XElement>());
        foreach (var (target, dec) in critDecisions)
        {
            var cEl = workingCrits.FirstOrDefault(c => string.Equals(c.Attribute("id")?.Value, target, StringComparison.Ordinal));
            cEl?.SetAttributeValue("decision", dec);
        }

        // Append confirmation provenance entry (conforming to ConfirmationType in spec.xsd)
        var confsContainer = workingSpecRoot.Element("confirmations");
        if (confsContainer == null)
        {
            confsContainer = new XElement("confirmations");
            workingSpecRoot.Add(confsContainer);
        }

        var newConfEl = new XElement("confirmation",
            new XAttribute("id", id),
            new XAttribute("action", action),
            new XAttribute("decision", "accepted"),
            new XAttribute("actor", actor),
            new XAttribute("decided_at", decidedAtRaw),
            new XElement("summary", summary));

        if (!string.IsNullOrWhiteSpace(rationale))
        {
            newConfEl.Add(new XElement("rationale", rationale));
        }

        if (requirementsEl != null && requirementsEl.HasElements)
        {
            newConfEl.Add(new XElement(requirementsEl));
        }

        if (questionsEl != null && questionsEl.HasElements)
        {
            newConfEl.Add(new XElement(questionsEl));
        }

        if (designEl != null && designEl.HasElements)
        {
            newConfEl.Add(new XElement(designEl));
        }

        if (acceptanceEl != null && acceptanceEl.HasElements)
        {
            newConfEl.Add(new XElement(acceptanceEl));
        }

        confsContainer.Add(newConfEl);

        // Serialize mutated spec document
        var mutatedSpecXml = ManagedDocumentSerializer.Serialize(workingSpecDoc);

        // 11. Prospective validation & atomic commit via WorkspaceTransactionCommitter
        var op = new TransactionDocumentOperation(
            normSpecDocPath,
            mutatedSpecXml,
            actualSpecRev,
            newSpecRev);

        return WorkspaceTransactionCommitter.Commit(
            workspaceRoot,
            "iteration confirm",
            new[] { op },
            clock: clock,
            faultInjector: faultInjector,
            version: version,
            correlationId: id,
            readPreconditions: action == "continue"
                ? new[] { new TransactionReadPrecondition(normTasksDocPath, actualTasksRev) }
                : null);
    }

    private static bool AreTargetDecisionElementsMatching(IEnumerable<XElement> list1, IEnumerable<XElement> list2)
    {
        var l1 = list1.ToList();
        var l2 = list2.ToList();

        if (l1.Count != l2.Count) return false;

        var dict1 = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var el in l1)
        {
            var target = el.Attribute("target")?.Value ?? string.Empty;
            var dec = el.Attribute("decision")?.Value ?? string.Empty;
            if (dict1.ContainsKey(target)) return false;
            dict1[target] = dec;
        }

        var dict2 = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var el in l2)
        {
            var target = el.Attribute("target")?.Value ?? string.Empty;
            var dec = el.Attribute("decision")?.Value ?? string.Empty;
            if (dict2.ContainsKey(target)) return false;
            dict2[target] = dec;
        }

        if (dict1.Count != dict2.Count) return false;

        foreach (var (key, val) in dict1)
        {
            if (!dict2.TryGetValue(key, out var val2) || !string.Equals(val, val2, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsNewDesignDecisionMatching(XElement requestNewDecision, XElement? specDecision)
    {
        if (specDecision == null) return false;

        var reqId = requestNewDecision.Attribute("id")?.Value;
        var specId = specDecision.Attribute("id")?.Value;
        if (!string.Equals(reqId, specId, StringComparison.Ordinal)) return false;

        // Compare index summary
        var reqSummary = requestNewDecision.Element("index")?.Element("summary")?.Value?.Trim();
        var specSummary = specDecision.Element("index")?.Element("summary")?.Value?.Trim();
        if (!string.Equals(reqSummary, specSummary, StringComparison.Ordinal)) return false;

        // Compare index terms
        var reqTerms = requestNewDecision.Element("index")?.Elements("term")
            .Select(t => (Key: t.Attribute("key")?.Value ?? "", Val: t.Attribute("value")?.Value ?? ""))
            .OrderBy(t => t.Key, StringComparer.Ordinal).ToList() ?? new();
        var specTerms = specDecision.Element("index")?.Elements("term")
            .Select(t => (Key: t.Attribute("key")?.Value ?? "", Val: t.Attribute("value")?.Value ?? ""))
            .OrderBy(t => t.Key, StringComparer.Ordinal).ToList() ?? new();

        if (reqTerms.Count != specTerms.Count) return false;
        for (int i = 0; i < reqTerms.Count; i++)
        {
            if (!string.Equals(reqTerms[i].Key, specTerms[i].Key, StringComparison.Ordinal) ||
                !string.Equals(reqTerms[i].Val, specTerms[i].Val, StringComparison.Ordinal))
            {
                return false;
            }
        }

        // Compare rationale
        var reqRationale = requestNewDecision.Element("rationale")?.Value?.Trim();
        var specRationale = specDecision.Element("rationale")?.Value?.Trim();
        if (!string.Equals(reqRationale, specRationale, StringComparison.Ordinal)) return false;

        // Compare sources if present
        var reqSources = requestNewDecision.Element("sources")?.Elements("ref")
            .Select(r => (Scope: r.Attribute("scope")?.Value ?? "", Target: r.Attribute("target")?.Value ?? "", Relation: r.Attribute("relation")?.Value ?? ""))
            .OrderBy(r => r.Target, StringComparer.Ordinal).ToList() ?? new();
        var specSources = specDecision.Element("sources")?.Elements("ref")
            .Select(r => (Scope: r.Attribute("scope")?.Value ?? "", Target: r.Attribute("target")?.Value ?? "", Relation: r.Attribute("relation")?.Value ?? ""))
            .OrderBy(r => r.Target, StringComparer.Ordinal).ToList() ?? new();

        if (reqSources.Count != specSources.Count) return false;
        for (int i = 0; i < reqSources.Count; i++)
        {
            if (!string.Equals(reqSources[i].Scope, specSources[i].Scope, StringComparison.Ordinal) ||
                !string.Equals(reqSources[i].Target, specSources[i].Target, StringComparison.Ordinal) ||
                !string.Equals(reqSources[i].Relation, specSources[i].Relation, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
