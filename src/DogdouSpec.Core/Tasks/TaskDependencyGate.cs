using System.Globalization;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Tasks;

/// <summary>
/// Authoritative gate for evaluating task dependency satisfaction and scope correctness.
/// </summary>
public static class TaskDependencyGate
{
    private static readonly HashSet<string> TerminalStatuses = new(StringComparer.Ordinal)
    {
        "done",
        "transferred",
        "superseded",
        "cancelled"
    };

    public static bool IsTerminalStatus(string? status) =>
        !string.IsNullOrEmpty(status) && TerminalStatuses.Contains(status);

    /// <summary>
    /// Evaluates whether all declared depends-on references of a task are resolved and satisfied in live project state.
    /// Fails closed if any reference is dangling, ambiguous, targets a non-task, violates scope, or targets a non-terminal task.
    /// </summary>
    public static (bool IsSatisfied, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<TransactionReadPrecondition> ReadPreconditions) EvaluateTaskDependencies(
        string workspaceRoot,
        string taskId,
        XElement taskElement,
        string containingDocRelativePath,
        ProjectSemanticIndex? index = null)
    {
        var diagnostics = new List<Diagnostic>();
        var readPreconditionRevisions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var depElem = taskElement.Element("dependencies");
        if (depElem == null)
        {
            return (true, Array.Empty<Diagnostic>(), Array.Empty<TransactionReadPrecondition>());
        }

        var depRefs = depElem.Elements("ref")
            .Where(r => string.Equals(r.Attribute("relation")?.Value, "depends-on", StringComparison.Ordinal))
            .ToList();

        if (depRefs.Count == 0)
        {
            return (true, Array.Empty<Diagnostic>(), Array.Empty<TransactionReadPrecondition>());
        }

        var containingNormPath = containingDocRelativePath.Replace('\\', '/');
        string? containingIterId = null;
        var segs = containingNormPath.Split('/');
        if (segs.Length > 1)
        {
            containingIterId = segs[0];
        }

        if (index == null)
        {
            var (enumSuccess, allDocs, enumDiags) = WorkspaceDiscovery.EnumerateDocuments(workspaceRoot);
            if (!enumSuccess || enumDiags.Count > 0)
            {
                return (false, enumDiags, Array.Empty<TransactionReadPrecondition>());
            }

            var parsedDocs = new List<(ManagedDocument Document, XDocument XDoc)>();
            foreach (var doc in allDocs)
            {
                try
                {
                    using var stream = File.OpenRead(doc.FullPath);
                    using var reader = SecureXmlReaderFactory.CreateReader(stream);
                    var xDoc = XDocument.Load(reader, LoadOptions.SetLineInfo);
                    parsedDocs.Add((doc, xDoc));
                }
                catch (Exception ex)
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.XmlParseError,
                        $"Failed to parse XML document '{doc.RelativePath}' during dependency evaluation: {ex.Message}",
                        doc.RelativePath));
                    return (false, diagnostics, Array.Empty<TransactionReadPrecondition>());
                }
            }

            index = ProjectSemanticIndex.Build(parsedDocs);
        }

        foreach (var depRef in depRefs)
        {
            var target = depRef.Attribute("target")?.Value;
            var scope = depRef.Attribute("scope")?.Value;
            var lineInfo = (System.Xml.IXmlLineInfo)depRef;

            if (!string.Equals(scope, "document", StringComparison.Ordinal) &&
                !string.Equals(scope, "iteration", StringComparison.Ordinal) &&
                !string.Equals(scope, "project", StringComparison.Ordinal))
            {
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    $"Task '{taskId}' contains dependency '{target ?? string.Empty}' with unsupported scope '{scope ?? string.Empty}'.",
                    containingNormPath,
                    lineInfo.HasLineInfo() ? lineInfo.LineNumber : null,
                    lineInfo.HasLineInfo() ? lineInfo.LinePosition : null));
                continue;
            }

            if (string.IsNullOrWhiteSpace(target))
            {
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    $"Task '{taskId}' contains a dependency reference with missing or empty target attribute.",
                    containingNormPath,
                    lineInfo.HasLineInfo() ? lineInfo.LineNumber : null,
                    lineInfo.HasLineInfo() ? lineInfo.LinePosition : null));
                continue;
            }

            if (!ProjectSemanticIndex.IsValidTimeFirstId(target))
            {
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.InvalidIdGrammar,
                    $"Dependency target identifier '{target}' in task '{taskId}' does not conform to the time-first ID grammar.",
                    containingNormPath,
                    lineInfo.HasLineInfo() ? lineInfo.LineNumber : null,
                    lineInfo.HasLineInfo() ? lineInfo.LinePosition : null));
                continue;
            }

            if (!index.ObjectsById.TryGetValue(target, out var targetObjects) || targetObjects.Count == 0)
            {
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.DanglingReference,
                    $"Cannot execute task '{taskId}': prerequisite task '{target}' could not be resolved in the workspace.",
                    containingNormPath,
                    lineInfo.HasLineInfo() ? lineInfo.LineNumber : null,
                    lineInfo.HasLineInfo() ? lineInfo.LinePosition : null));
                continue;
            }

            if (targetObjects.Count > 1)
            {
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.AmbiguousReference,
                    $"Cannot execute task '{taskId}': prerequisite task '{target}' is ambiguous and matches {targetObjects.Count} elements across the workspace.",
                    containingNormPath,
                    lineInfo.HasLineInfo() ? lineInfo.LineNumber : null,
                    lineInfo.HasLineInfo() ? lineInfo.LinePosition : null));
                continue;
            }

            var targetObj = targetObjects[0];

            if (!string.Equals(targetObj.ElementName, "task", StringComparison.Ordinal))
            {
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.InvalidReferenceTargetType,
                    $"Cannot execute task '{taskId}': dependency target '{target}' is a <{targetObj.ElementName}> in '{targetObj.Document.RelativePath}', but must be a <task>.",
                    containingNormPath,
                    lineInfo.HasLineInfo() ? lineInfo.LineNumber : null,
                    lineInfo.HasLineInfo() ? lineInfo.LinePosition : null));
                continue;
            }

            var targetDocRel = targetObj.Document.RelativePath.Replace('\\', '/');
            var isSameDoc = string.Equals(targetDocRel, containingNormPath, StringComparison.OrdinalIgnoreCase);
            var isSameIter = containingIterId != null &&
                             targetObj.Document.IterationId != null &&
                             string.Equals(targetObj.Document.IterationId, containingIterId, StringComparison.Ordinal);

            var expectedScope = isSameDoc ? "document" : isSameIter ? "iteration" : "project";
            if (!string.Equals(scope, expectedScope, StringComparison.Ordinal))
            {
                var scopeRank = scope == "document" ? 0 : scope == "iteration" ? 1 : 2;
                var expectedRank = expectedScope == "document" ? 0 : expectedScope == "iteration" ? 1 : 2;
                var code = scopeRank < expectedRank
                    ? DiagnosticCodes.ReferenceScopeViolation
                    : DiagnosticCodes.ReferenceScopeNotNarrowest;
                diagnostics.Add(Diagnostic.Error(
                    code,
                    $"Task '{taskId}' dependency '{target}' declares scope='{scope}', but the narrowest valid scope is '{expectedScope}' for target document '{targetDocRel}'.",
                    containingNormPath,
                    lineInfo.HasLineInfo() ? lineInfo.LineNumber : null,
                    lineInfo.HasLineInfo() ? lineInfo.LinePosition : null));
                continue;
            }

            var targetStatus = targetObj.Element.Attribute("status")?.Value ?? "pending";
            if (!IsTerminalStatus(targetStatus))
            {
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.TaskTransitionConflict,
                    $"Cannot execute task '{taskId}': prerequisite task '{target}' has non-terminal status '{targetStatus}'. Prerequisite tasks must be in status 'done', 'transferred', 'superseded', or 'cancelled'.",
                    containingNormPath,
                    lineInfo.HasLineInfo() ? lineInfo.LineNumber : null,
                    lineInfo.HasLineInfo() ? lineInfo.LinePosition : null));
                continue;
            }

            if (!isSameDoc)
            {
                var targetDocRoot = targetObj.Element.Document?.Root;
                var targetRevStr = targetDocRoot?.Attribute("revision")?.Value;
                if (!int.TryParse(targetRevStr, CultureInfo.InvariantCulture, out var targetRev) || targetRev <= 0)
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.XmlParseError,
                        $"Cannot execute task '{taskId}': dependency document '{targetDocRel}' has a missing, non-positive, or malformed revision.",
                        targetDocRel));
                    continue;
                }

                if (readPreconditionRevisions.TryGetValue(targetDocRel, out var existingRevision) && existingRevision != targetRev)
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.RevisionConflict,
                        $"Cannot execute task '{taskId}': dependency document '{targetDocRel}' resolved with conflicting revisions {existingRevision} and {targetRev}.",
                        targetDocRel));
                    continue;
                }

                readPreconditionRevisions[targetDocRel] = targetRev;
            }
        }

        var isSatisfied = diagnostics.Count == 0;
        var readPreconditions = readPreconditionRevisions
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new TransactionReadPrecondition(pair.Key, pair.Value))
            .ToArray();
        return (isSatisfied, diagnostics, readPreconditions);
    }
}
