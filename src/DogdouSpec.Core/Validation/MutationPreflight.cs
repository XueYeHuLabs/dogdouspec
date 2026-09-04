using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using DogdouSpec.Core.Changes;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Iterations;
using DogdouSpec.Core.Revisions;
using DogdouSpec.Core.Requirements;
using DogdouSpec.Core.Resources;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Tasks;
using DogdouSpec.Core.Time;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Workspace;
using DogdouSpec.Core.XPath;

namespace DogdouSpec.Core.Validation;

public sealed record PreflightResult(
    string RequestType,
    string? IterationId,
    string? TaskId,
    IReadOnlyList<MutatedDocument> MutatedDocuments,
    MutationEnvelope? ProspectiveEnvelope);

/// <summary>
/// Authoritative preflight engine for DogdouSpec mutation requests.
/// Performs requests.xsd schema validation, semantic state checks, and prospective workspace validation
/// with zero disk writes, returning prospective target revisions and documents.
/// </summary>
public static class MutationPreflight
{
    private static readonly HashSet<string> KnownRequestTypes = new(StringComparer.Ordinal)
    {
        "task-add",
        "task-update",
        "task-revise",
        "task-split",
        "task-review",
        "iteration-confirmation",
        "requirement-propose",
        "change-propose",
        "change-apply",
        "transaction"
    };

    public static (bool Success, PreflightResult? Result, IReadOnlyList<Diagnostic> Diagnostics) Preflight(
        string workspaceRoot,
        string requestXml,
        string? iterationId = null,
        string? taskId = null,
        int? expectedRevision = null,
        int? expectedTasksRevision = null,
        string version = "1.0",
        IClock? clock = null)
    {
        clock ??= SystemClock.Instance;

        // 1. Validate basic inputs
        if (string.IsNullOrWhiteSpace(requestXml))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Mutation request XML cannot be empty.") });
        }

        if (Encoding.UTF8.GetByteCount(requestXml) > XPathQueryLimits.MaxDocumentBytes)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"Mutation request exceeds maximum allowed size of {XPathQueryLimits.MaxDocumentBytes} bytes.") });
        }

        // 2. Validate workspace root security
        var (isWsSafe, wsErr) = PathSecurity.VerifyWorkspaceDirectorySecurity(workspaceRoot);
        if (!isWsSafe || wsErr != null)
        {
            return (false, null, new[] { wsErr ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, "Workspace directory security verification failed.") });
        }

        // 3. Parse XML request
        XDocument requestDoc;
        try
        {
            using var sr = new StringReader(requestXml);
            using var reader = SecureXmlReaderFactory.CreateReader(sr);
            requestDoc = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (XmlException xmlEx)
        {
            var code = xmlEx.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase)
                ? DiagnosticCodes.DtdProhibited
                : DiagnosticCodes.XmlParseError;
            return (false, null, new[] { Diagnostic.Error(code, $"Failed to parse mutation request XML: {xmlEx.Message}", null, xmlEx.LineNumber, xmlEx.LinePosition) });
        }
        catch (Exception ex)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Failed to parse mutation request XML: {ex.Message}") });
        }

        var root = requestDoc.Root;
        if (root == null)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, "Request document has no root element.") });
        }

        var rootName = root.Name.LocalName;
        if (!KnownRequestTypes.Contains(rootName))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Unknown or unsupported mutation request element '{rootName}'.") });
        }

        // 4. Reconcile explicit confirmation inputs with authoritative request attributes.
        if (rootName == "iteration-confirmation")
        {
            var (reconciled, reconciledXml, reconciliationError) = IterationConfirmationRequestResolver.Reconcile(
                workspaceRoot,
                requestDoc,
                iterationId,
                expectedRevision,
                expectedTasksRevision);
            if (!reconciled || reconciliationError != null || string.IsNullOrWhiteSpace(reconciledXml))
            {
                return (false, null, new[] { reconciliationError ?? Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "Iteration confirmation request could not be resolved.") });
            }

            using var reconciledReader = SecureXmlReaderFactory.CreateReader(new StringReader(reconciledXml));
            requestDoc = XDocument.Load(reconciledReader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            root = requestDoc.Root!;
        }

        // 5. Schema validation against requests.xsd
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

        try
        {
            using var sr = new StringReader(root.ToString(SaveOptions.DisableFormatting));
            using var reader = SecureXmlReaderFactory.CreateReader(sr, settings);
            while (reader.Read()) { }
        }
        catch (XmlException xmlEx)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.SchemaValidationError, $"Schema validation failed: {xmlEx.Message}") });
        }

        if (schemaDiagnostics.Any(d => d.Severity == "error"))
        {
            return (false, null, schemaDiagnostics);
        }

        // 6. Execute state & prospective checks via dry-run handler invocation
        var effectiveXml = root.ToString(SaveOptions.DisableFormatting);
        switch (rootName)
        {
            case "task-add":
            {
                var iter = iterationId ?? ResolveActiveIterationId(workspaceRoot);
                if (string.IsNullOrWhiteSpace(iter))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Iteration could not be determined for task-add preflight. Specify --iteration.") });
                }

                int rev;
                if (expectedRevision.HasValue)
                {
                    rev = expectedRevision.Value;
                }
                else
                {
                    var (revOk, resolvedRev, revErr) = DocumentRevisionResolver.ReadDocumentRevision(workspaceRoot, $"{iter}/tasks.xml");
                    if (!revOk || revErr != null)
                    {
                        return (false, null, new[] { revErr ?? Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Could not resolve tasks.xml revision for iteration '{iter}'.") });
                    }
                    rev = resolvedRev;
                }

                var (success, envelope, diags) = TaskAdder.Add(workspaceRoot, iter, rev, effectiveXml, dryRun: true, version: version);
                if (!success || envelope == null)
                {
                    return (false, null, diags);
                }
                var effectiveTaskId = root.Element("task")?.Attribute("id")?.Value;
                return (true, new PreflightResult("task-add", iter, effectiveTaskId, envelope.Documents, envelope), diags);
            }

            case "task-update":
            {
                if (string.IsNullOrWhiteSpace(taskId))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "--task argument is required for task-update preflight.") });
                }
                var effectiveTaskId = taskId;

                var iter = iterationId ?? FindIterationForTask(workspaceRoot, effectiveTaskId);
                if (string.IsNullOrWhiteSpace(iter))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Iteration could not be determined for task-update preflight. Specify --iteration.") });
                }

                int rev;
                if (expectedRevision.HasValue)
                {
                    rev = expectedRevision.Value;
                }
                else
                {
                    var (revOk, resolvedRev, revErr) = DocumentRevisionResolver.ReadDocumentRevision(workspaceRoot, $"{iter}/tasks.xml");
                    if (!revOk || revErr != null)
                    {
                        return (false, null, new[] { revErr ?? Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Could not resolve tasks.xml revision for iteration '{iter}'.") });
                    }
                    rev = resolvedRev;
                }

                var (success, envelope, diags) = TaskUpdater.Update(workspaceRoot, iter, effectiveTaskId, rev, effectiveXml, clock, null, version, dryRun: true);
                if (!success || envelope == null)
                {
                    return (false, null, diags);
                }
                return (true, new PreflightResult("task-update", iter, effectiveTaskId, envelope.Documents, envelope), diags);
            }

            case "task-revise":
            {
                if (string.IsNullOrWhiteSpace(taskId))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "--task argument is required for task-revise preflight.") });
                }
                var effectiveTaskId = taskId;

                var iter = iterationId ?? FindIterationForTask(workspaceRoot, effectiveTaskId);
                if (string.IsNullOrWhiteSpace(iter))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Iteration could not be determined for task-revise preflight. Specify --iteration.") });
                }

                int rev;
                if (expectedRevision.HasValue)
                {
                    rev = expectedRevision.Value;
                }
                else
                {
                    var (revOk, resolvedRev, revErr) = DocumentRevisionResolver.ReadDocumentRevision(workspaceRoot, $"{iter}/tasks.xml");
                    if (!revOk || revErr != null)
                    {
                        return (false, null, new[] { revErr ?? Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Could not resolve tasks.xml revision for iteration '{iter}'.") });
                    }
                    rev = resolvedRev;
                }

                var (success, envelope, diags) = TaskReviser.Revise(workspaceRoot, iter, effectiveTaskId, rev, effectiveXml, clock, null, version, dryRun: true);
                if (!success || envelope == null)
                {
                    return (false, null, diags);
                }
                return (true, new PreflightResult("task-revise", iter, effectiveTaskId, envelope.Documents, envelope), diags);
            }

            case "task-split":
            {
                if (string.IsNullOrWhiteSpace(taskId))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "--task argument is required for task-split preflight.") });
                }
                var effectiveTaskId = taskId;

                var iter = iterationId ?? FindIterationForTask(workspaceRoot, effectiveTaskId);
                if (string.IsNullOrWhiteSpace(iter))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Iteration could not be determined for task-split preflight. Specify --iteration.") });
                }

                int rev;
                if (expectedRevision.HasValue)
                {
                    rev = expectedRevision.Value;
                }
                else
                {
                    var (revOk, resolvedRev, revErr) = DocumentRevisionResolver.ReadDocumentRevision(workspaceRoot, $"{iter}/tasks.xml");
                    if (!revOk || revErr != null)
                    {
                        return (false, null, new[] { revErr ?? Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Could not resolve tasks.xml revision for iteration '{iter}'.") });
                    }
                    rev = resolvedRev;
                }

                var (success, envelope, diags) = TaskSplitter.Split(workspaceRoot, iter, effectiveTaskId, rev, effectiveXml, clock, null, version, dryRun: true);
                if (!success || envelope == null)
                {
                    return (false, null, diags);
                }
                return (true, new PreflightResult("task-split", iter, effectiveTaskId, envelope.Documents, envelope), diags);
            }

            case "task-review":
            {
                if (string.IsNullOrWhiteSpace(taskId))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "--task argument is required for task-review preflight.") });
                }
                var effectiveTaskId = taskId;

                var iter = iterationId ?? FindIterationForTask(workspaceRoot, effectiveTaskId);
                if (string.IsNullOrWhiteSpace(iter))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Iteration could not be determined for task-review preflight. Specify --iteration.") });
                }

                int rev;
                if (expectedRevision.HasValue)
                {
                    rev = expectedRevision.Value;
                }
                else
                {
                    var (revOk, resolvedRev, revErr) = DocumentRevisionResolver.ReadDocumentRevision(workspaceRoot, $"{iter}/tasks.xml");
                    if (!revOk || revErr != null)
                    {
                        return (false, null, new[] { revErr ?? Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Could not resolve tasks.xml revision for iteration '{iter}'.") });
                    }
                    rev = resolvedRev;
                }

                var (success, envelope, diags) = TaskReviewer.Submit(workspaceRoot, iter, effectiveTaskId, rev, effectiveXml, version, dryRun: true);
                if (!success || envelope == null)
                {
                    return (false, null, diags);
                }
                return (true, new PreflightResult("task-review", iter, effectiveTaskId, envelope.Documents, envelope), diags);
            }

            case "iteration-confirmation":
            {
                var (success, envelope, diags) = IterationConfirmer.Confirm(workspaceRoot, effectiveXml, clock, null, version, dryRun: true);
                if (!success || envelope == null)
                {
                    return (false, null, diags);
                }
                var iter = root.Attribute("iteration")?.Value;
                return (true, new PreflightResult("iteration-confirmation", iter, null, envelope.Documents, envelope), diags);
            }

            case "requirement-propose":
            {
                var iter = iterationId ?? ResolveActiveIterationId(workspaceRoot);
                if (string.IsNullOrWhiteSpace(iter))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Iteration could not be determined for requirement-propose preflight. Specify --iteration.") });
                }

                int rev;
                if (expectedRevision.HasValue)
                {
                    rev = expectedRevision.Value;
                }
                else
                {
                    var (revOk, resolvedRev, revErr) = DocumentRevisionResolver.ReadDocumentRevision(workspaceRoot, $"{iter}/spec.xml");
                    if (!revOk || revErr != null)
                    {
                        return (false, null, new[] { revErr ?? Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Could not resolve spec.xml revision for iteration '{iter}'.") });
                    }
                    rev = resolvedRev;
                }

                var (success, envelope, diags) = RequirementProposer.Propose(workspaceRoot, iter, rev, effectiveXml, clock, null, version, dryRun: true);
                if (!success || envelope == null)
                {
                    return (false, null, diags);
                }
                return (true, new PreflightResult("requirement-propose", iter, null, envelope.Documents, envelope), diags);
            }

            case "change-propose":
            {
                var iter = iterationId ?? ResolveActiveIterationId(workspaceRoot);
                if (string.IsNullOrWhiteSpace(iter))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Iteration could not be determined for change-propose preflight. Specify --iteration.") });
                }

                int specRev;
                if (expectedRevision.HasValue)
                {
                    specRev = expectedRevision.Value;
                }
                else
                {
                    var (revOk, resolvedRev, revErr) = DocumentRevisionResolver.ReadDocumentRevision(workspaceRoot, $"{iter}/spec.xml");
                    if (!revOk || revErr != null)
                    {
                        return (false, null, new[] { revErr ?? Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Could not resolve spec.xml revision for iteration '{iter}'.") });
                    }
                    specRev = resolvedRev;
                }

                var (tasksOk, tasksRev, tasksErr) = DocumentRevisionResolver.ResolveExpectedRevision(
                    workspaceRoot,
                    $"{iter}/tasks.xml",
                    expectedTasksRevision);
                if (!tasksOk || tasksErr != null)
                {
                    return (false, null, new[] { tasksErr ?? Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Could not resolve tasks.xml revision for iteration '{iter}'.") });
                }

                var (success, envelope, diags) = ChangeProposer.Propose(workspaceRoot, iter, specRev, tasksRev, effectiveXml, clock, null, version, dryRun: true);
                if (!success || envelope == null)
                {
                    return (false, null, diags);
                }
                return (true, new PreflightResult("change-propose", iter, null, envelope.Documents, envelope), diags);
            }

            case "change-apply":
            {
                var iter = iterationId ?? ResolveActiveIterationId(workspaceRoot);
                if (string.IsNullOrWhiteSpace(iter))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Iteration could not be determined for change-apply preflight. Specify --iteration.") });
                }

                int specRev;
                if (expectedRevision.HasValue)
                {
                    specRev = expectedRevision.Value;
                }
                else
                {
                    var (revOk, resolvedRev, revErr) = DocumentRevisionResolver.ReadDocumentRevision(workspaceRoot, $"{iter}/spec.xml");
                    if (!revOk || revErr != null)
                    {
                        return (false, null, new[] { revErr ?? Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Could not resolve spec.xml revision for iteration '{iter}'.") });
                    }
                    specRev = resolvedRev;
                }

                var (tasksOk, tasksRev, tasksErr) = DocumentRevisionResolver.ResolveExpectedRevision(
                    workspaceRoot,
                    $"{iter}/tasks.xml",
                    expectedTasksRevision);
                if (!tasksOk || tasksErr != null)
                {
                    return (false, null, new[] { tasksErr ?? Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Could not resolve tasks.xml revision for iteration '{iter}'.") });
                }

                var (success, envelope, diags) = ChangeApplier.Apply(workspaceRoot, iter, specRev, tasksRev, effectiveXml, clock, null, version, dryRun: true);
                if (!success || envelope == null)
                {
                    return (false, null, diags);
                }
                return (true, new PreflightResult("change-apply", iter, null, envelope.Documents, envelope), diags);
            }

            case "transaction":
            {
                var (success, envelope, diags) = TransactionApplier.Apply(workspaceRoot, effectiveXml, clock, null, version, dryRun: true);
                if (!success || envelope == null)
                {
                    return (false, null, diags);
                }
                return (true, new PreflightResult("transaction", null, null, envelope.Documents, envelope), diags);
            }

            default:
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Unhandled request type '{rootName}'.") });
        }
    }

    private static string? FindIterationForTask(string workspaceRoot, string taskId)
    {
        var (success, result, _) = IterationLister.List(workspaceRoot);
        if (!success || result == null)
        {
            return null;
        }

        var matches = new List<string>();
        foreach (var iter in result.Iterations)
        {
            var tasksPath = Path.Combine(workspaceRoot, iter.Id, "tasks.xml");
            if (!File.Exists(tasksPath))
            {
                continue;
            }

            try
            {
                using var stream = File.OpenRead(tasksPath);
                using var reader = SecureXmlReaderFactory.CreateReader(stream);
                var doc = XDocument.Load(reader);
                if (doc.Root?.Elements("task").Any(t => string.Equals((string?)t.Attribute("id"), taskId, StringComparison.Ordinal)) == true)
                {
                    matches.Add(iter.Id);
                }
            }
            catch
            {
                return null;
            }
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    private static string? ResolveActiveIterationId(string workspaceRoot)
    {
        var (success, result, _) = IterationLister.List(workspaceRoot);
        if (!success || result == null || result.Iterations.Count == 0)
            return null;

        var active = result.Iterations.Where(i => string.Equals(i.Status, "active", StringComparison.OrdinalIgnoreCase)).ToList();
        if (active.Count == 1)
            return active[0].Id;

        var draft = result.Iterations.Where(i => string.Equals(i.Status, "draft", StringComparison.OrdinalIgnoreCase)).ToList();
        if (draft.Count == 1)
            return draft[0].Id;

        if (result.Iterations.Count == 1)
            return result.Iterations[0].Id;

        return null;
    }
}
