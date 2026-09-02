using System.Globalization;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Tasks;

public static class TaskSummary
{
    public static (bool Success, TaskSummaryResult? Result, IReadOnlyList<Diagnostic> Diagnostics) Summarize(
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

        var tasks = tasksDoc.Root?.Elements("task").ToList() ?? new List<XElement>();
        int total = tasks.Count;
        int pending = 0, inProgress = 0, verification = 0, done = 0, blocked = 0, transferred = 0, superseded = 0, cancelled = 0;

        foreach (var t in tasks)
        {
            var s = t.Attribute("status")?.Value?.ToLowerInvariant() ?? "pending";
            switch (s)
            {
                case "pending": pending++; break;
                case "in-progress": inProgress++; break;
                case "verification": verification++; break;
                case "done": done++; break;
                case "blocked": blocked++; break;
                case "transferred": transferred++; break;
                case "superseded": superseded++; break;
                case "cancelled": cancelled++; break;
                default: pending++; break;
            }
        }

        var result = new TaskSummaryResult(
            targetIterationId!,
            tasksRevision,
            total,
            pending,
            inProgress,
            verification,
            done,
            blocked,
            transferred,
            superseded,
            cancelled);

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
