using System.Globalization;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;
using DogdouSpec.Core.XPath;

namespace DogdouSpec.Core.Tasks;

public static class TaskList
{
    public static (bool Success, TaskListResult? Result, IReadOnlyList<Diagnostic> Diagnostics) List(
        string workspaceRoot,
        string? requestedIterationId = null,
        string? filterStatus = null)
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

        var (iterSuccess, targetIterationId, iterDiag) = ResolveTargetIteration(workspaceRoot, requestedIterationId);
        if (!iterSuccess || iterDiag != null)
        {
            return (false, null, new[] { iterDiag! });
        }

        var tasksRelativePath = $"{targetIterationId}/tasks.xml";
        var fullTasksPath = Path.Combine(workspaceRoot, tasksRelativePath.Replace('/', Path.DirectorySeparatorChar));

        var (isContained, contErr) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, fullTasksPath);
        if (!isContained || contErr != null)
        {
            return (false, null, new[] { contErr ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, $"Path escapes workspace: '{tasksRelativePath}'.") });
        }

        if (!File.Exists(fullTasksPath))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"tasks.xml not found for iteration '{targetIterationId}'.", tasksRelativePath) });
        }

        XDocument tasksDoc;
        try
        {
            using var stream = File.OpenRead(fullTasksPath);
            using var reader = SecureXmlReaderFactory.CreateReader(stream);
            tasksDoc = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (Exception ex)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Failed to parse '{tasksRelativePath}': {ex.Message}", tasksRelativePath) });
        }

        var revStr = tasksDoc.Root?.Attribute("revision")?.Value;
        int.TryParse(revStr, CultureInfo.InvariantCulture, out var tasksRevision);

        var taskElements = tasksDoc.Root?.Elements("task") ?? Enumerable.Empty<XElement>();
        var items = new List<TaskListItem>();

        foreach (var el in taskElements)
        {
            var id = el.Attribute("id")?.Value ?? string.Empty;
            var status = el.Attribute("status")?.Value ?? string.Empty;
            var agent = el.Attribute("agent")?.Value;
            var startedAt = el.Attribute("started_at")?.Value;
            var completedAt = el.Attribute("completed_at")?.Value;
            var updatedAt = el.Attribute("updated_at")?.Value;

            if (!string.IsNullOrWhiteSpace(filterStatus) &&
                !string.Equals(status, filterStatus.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var title = el.Element("title")?.Value ?? string.Empty;
            var summary = el.Element("index")?.Element("summary")?.Value;

            var deps = el.Element("dependencies")?.Elements("ref")
                .Select(r => r.Attribute("target")?.Value)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t!)
                .ToList();

            items.Add(new TaskListItem(
                id,
                status,
                title,
                summary,
                agent,
                deps,
                startedAt,
                completedAt,
                updatedAt));
        }

        var result = new TaskListResult(targetIterationId!, tasksRevision, items, filterStatus);
        return (true, result, Array.Empty<Diagnostic>());
    }

    private static (bool Success, string? IterationId, Diagnostic? Diagnostic) ResolveTargetIteration(
        string workspaceRoot,
        string? requestedIterationId)
    {
        if (!string.IsNullOrWhiteSpace(requestedIterationId))
        {
            var (isValid, normId, err) = PathSecurity.ValidateIterationId(requestedIterationId);
            if (!isValid || err != null)
            {
                return (false, null, err ?? Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid iteration ID '{requestedIterationId}'."));
            }
            return (true, normId, null);
        }

        var (enumSuccess, allDocs, _) = WorkspaceDiscovery.EnumerateDocuments(workspaceRoot);
        if (!enumSuccess)
        {
            return (false, null, Diagnostic.Error(DiagnosticCodes.DocumentNotFound, "Failed to enumerate workspace documents."));
        }

        var activeIterationIds = new List<string>();
        foreach (var doc in allDocs.Where(d => string.Equals(d.FileName, "spec.xml", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var stream = File.OpenRead(doc.FullPath);
                using var reader = SecureXmlReaderFactory.CreateReader(stream);
                var xdoc = XDocument.Load(reader);
                var status = xdoc.Root?.Attribute("status")?.Value;
                if (string.Equals(status, "active", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(doc.IterationId))
                {
                    activeIterationIds.Add(doc.IterationId);
                }
            }
            catch
            {
            }
        }

        if (activeIterationIds.Count == 1)
        {
            return (true, activeIterationIds[0], null);
        }

        if (activeIterationIds.Count > 1)
        {
            return (false, null, Diagnostic.Error(
                DiagnosticCodes.InvalidArgument,
                $"Multiple active iterations found ({string.Join(", ", activeIterationIds)}). Specify --iteration explicitly."));
        }

        return (false, null, Diagnostic.Error(
            DiagnosticCodes.InvalidArgument,
            "No active iteration found in workspace. Specify --iteration explicitly."));
    }
}
