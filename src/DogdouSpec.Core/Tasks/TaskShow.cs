using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Tasks;

public static class TaskShow
{
    public static (bool Success, TaskShowResult? Result, IReadOnlyList<Diagnostic> Diagnostics) Show(
        string workspaceRoot,
        string taskId,
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

        if (string.IsNullOrWhiteSpace(taskId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Task ID cannot be empty.") });
        }

        var normTaskId = taskId.Trim();
        if (!ProjectSemanticIndex.IsValidTimeFirstId(normTaskId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, $"Task ID '{taskId}' does not conform to time-first grammar.") });
        }

        var (enumSuccess, allDocs, enumDiags) = WorkspaceDiscovery.EnumerateDocuments(workspaceRoot, requestedIterationId);
        if (!enumSuccess || enumDiags.Count > 0)
        {
            return (false, null, enumDiags);
        }

        var taskDocs = allDocs.Where(d => string.Equals(d.FileName, "tasks.xml", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var doc in taskDocs)
        {
            try
            {
                using var stream = File.OpenRead(doc.FullPath);
                using var reader = SecureXmlReaderFactory.CreateReader(stream);
                var xdoc = XDocument.Load(reader, LoadOptions.SetLineInfo);
                var matchingTask = xdoc.Descendants("task").FirstOrDefault(t =>
                    string.Equals(t.Attribute("id")?.Value, normTaskId, StringComparison.Ordinal));

                if (matchingTask != null)
                {
                    var iterId = doc.IterationId ?? xdoc.Root?.Attribute("iteration")?.Value ?? "unknown";
                    return (true, new TaskShowResult(iterId, normTaskId, matchingTask), Array.Empty<Diagnostic>());
                }
            }
            catch (Exception ex)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Failed to parse '{doc.RelativePath}': {ex.Message}", doc.RelativePath) });
            }
        }

        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.ResourceNotFound, $"Task '{taskId}' was not found in workspace.") });
    }
}
