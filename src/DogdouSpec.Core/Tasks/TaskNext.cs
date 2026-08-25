using System.Globalization;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;
using DogdouSpec.Core.XPath;

namespace DogdouSpec.Core.Tasks;

/// <summary>
/// Public read-only helper for deriving the next actionable task in an iteration,
/// properly accounting for same-document and cross-iteration dependencies.
/// </summary>
public static class TaskNext
{
    public static (bool Success, TaskNextResult? Result, IReadOnlyList<Diagnostic> Diagnostics) SelectNext(
        string workspaceRoot,
        string? requestedIterationId = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Workspace root cannot be empty.") });
        }

        var (isWsSafe, wsErr) = PathSecurity.VerifyWorkspaceDirectorySecurity(workspaceRoot);
        if (!isWsSafe || wsErr != null)
        {
            return (false, null, new[] { wsErr ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, "Workspace directory security verification failed.") });
        }

        // 1. Resolve target iteration
        var (iterSuccess, targetIterationId, iterDiag) = ResolveTargetIteration(workspaceRoot, requestedIterationId);
        if (!iterSuccess || iterDiag != null)
        {
            return (false, null, new[] { iterDiag! });
        }

        // 2. Build full project semantic index
        var (enumSuccess, allDocs, enumDiags) = WorkspaceDiscovery.EnumerateDocuments(workspaceRoot);
        if (!enumSuccess || enumDiags.Count > 0)
        {
            return (false, null, enumDiags);
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
                return (false, null, new[] { Diagnostic.Error(
                    DiagnosticCodes.XmlParseError,
                    $"Failed to parse XML document '{doc.RelativePath}' during task selection: {ex.Message}",
                    doc.RelativePath) });
            }
        }

        var index = ProjectSemanticIndex.Build(parsedDocs);

        // 3. Find target tasks document
        var targetTasksDoc = index.TasksDocuments.FirstOrDefault(td =>
            string.Equals(td.Document.IterationId, targetIterationId, StringComparison.Ordinal));

        if (targetTasksDoc == null)
        {
            return (false, null, new[] { Diagnostic.Error(
                DiagnosticCodes.DocumentNotFound,
                $"tasks.xml not found for iteration '{targetIterationId}'.",
                $"{targetIterationId}/tasks.xml") });
        }

        var revStr = targetTasksDoc.Element.Attribute("revision")?.Value;
        int.TryParse(revStr, CultureInfo.InvariantCulture, out var tasksRevision);

        var tasks = targetTasksDoc.Tasks;
        if (tasks.Count == 0)
        {
            return (true, new TaskNextResult(targetIterationId!, tasksRevision, null, "No tasks found in iteration"), Array.Empty<Diagnostic>());
        }

        // Priority 1: Resume any in-progress or verification task
        var activeTask = tasks.FirstOrDefault(t =>
            string.Equals(t.Status, "in-progress", StringComparison.Ordinal) ||
            string.Equals(t.Status, "verification", StringComparison.Ordinal));

        if (activeTask != null)
        {
            return (true, new TaskNextResult(targetIterationId!, tasksRevision, activeTask, "Active task in-progress or verification"), Array.Empty<Diagnostic>());
        }

        // Priority 2: Select first ready pending task whose dependencies are satisfied
        var pendingTasks = tasks.Where(t => string.Equals(t.Status, "pending", StringComparison.Ordinal)).ToList();

        foreach (var task in pendingTasks)
        {
            var (isSatisfied, dependencyDiagnostics, _) = TaskDependencyGate.EvaluateTaskDependencies(
                workspaceRoot,
                task.Id,
                task.Element,
                targetTasksDoc.Document.RelativePath,
                index);

            var structuralDiagnostics = dependencyDiagnostics
                .Where(diagnostic => !string.Equals(
                    diagnostic.Code,
                    DiagnosticCodes.TaskTransitionConflict,
                    StringComparison.Ordinal))
                .ToArray();
            if (structuralDiagnostics.Length > 0)
            {
                return (false, null, structuralDiagnostics);
            }

            if (isSatisfied)
            {
                return (true, new TaskNextResult(targetIterationId!, tasksRevision, task, "Next ready pending task with satisfied dependencies"), Array.Empty<Diagnostic>());
            }
        }

        // Priority 3: No actionable task
        var reason = pendingTasks.Count > 0
            ? "Remaining pending tasks are blocked by unsatisfied dependencies"
            : "All tasks in iteration are terminal";

        return (true, new TaskNextResult(targetIterationId!, tasksRevision, null, reason), Array.Empty<Diagnostic>());
    }

    private static (bool Success, string? IterationId, Diagnostic? Error) ResolveTargetIteration(
        string workspaceRoot,
        string? requestedIterationId)
    {
        if (!string.IsNullOrWhiteSpace(requestedIterationId))
        {
            var (isValid, normalizedId, idErr) = PathSecurity.ValidateIterationId(requestedIterationId);
            if (!isValid || idErr != null)
            {
                return (false, null, idErr ?? Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid iteration ID '{requestedIterationId}'."));
            }

            var specPath = Path.Combine(workspaceRoot, normalizedId, "spec.xml");
            if (!File.Exists(specPath))
            {
                return (false, null, Diagnostic.Error(DiagnosticCodes.IterationNotFound, $"Iteration '{normalizedId}' does not exist in workspace.", $"{normalizedId}/spec.xml"));
            }

            return (true, normalizedId, null);
        }

        // Auto-discover active iteration
        var activeIterations = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(workspaceRoot))
        {
            var dirName = Path.GetFileName(dir);
            if (dirName.StartsWith('.') || dirName.StartsWith('_'))
            {
                continue;
            }

            var specPath = Path.Combine(dir, "spec.xml");
            if (!File.Exists(specPath))
            {
                continue;
            }

            var (isContained, contErr) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, specPath);
            if (!isContained || contErr != null)
            {
                return (false, null, contErr);
            }

            try
            {
                using var fs = File.OpenRead(specPath);
                using var r = SecureXmlReaderFactory.CreateReader(fs);
                var xDoc = XDocument.Load(r);
                var status = xDoc.Root?.Attribute("status")?.Value;
                if (string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
                {
                    var id = xDoc.Root?.Attribute("id")?.Value ?? dirName;
                    activeIterations.Add(id);
                }
            }
            catch (Exception ex)
            {
                return (false, null, Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Failed to read '{dirName}/spec.xml': {ex.Message}", $"{dirName}/spec.xml"));
            }
        }

        activeIterations.Sort(StringComparer.Ordinal);

        if (activeIterations.Count == 1)
        {
            return (true, activeIterations[0], null);
        }

        if (activeIterations.Count == 0)
        {
            return (false, null, Diagnostic.Error(DiagnosticCodes.CardinalityConflict, "task next requires --iteration because no active iteration was found in workspace."));
        }

        return (false, null, Diagnostic.Error(DiagnosticCodes.CardinalityConflict, $"task next requires --iteration because multiple active iterations exist ({string.Join(", ", activeIterations)})."));
    }
}
