using System.Text.RegularExpressions;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Security;

namespace DogdouSpec.Core.Workspace;

/// <summary>
/// Handles ancestor discovery of .dogdouspec workspaces and enumeration of managed documents.
/// </summary>
public static class WorkspaceDiscovery
{
    /// <summary>
    /// Validates an iteration identifier against the TimeFirstIdType grammar.
    /// Delegates directly to the central PathSecurity.ValidateIterationId authority.
    /// </summary>
    public static (bool IsValid, string NormalizedId, Diagnostic? Error) ValidateIterationId(string? iterationId) =>
        PathSecurity.ValidateIterationId(iterationId);

    /// <summary>
    /// Discovers the nearest ancestor .dogdouspec directory from startDirectory,
    /// or validates the explicit workspaceRoot if specified.
    /// </summary>
    public static (bool Success, string WorkspaceRoot, Diagnostic? Error) FindWorkspaceRoot(
        string? explicitWorkspaceRoot,
        string startDirectory)
    {
        if (!string.IsNullOrWhiteSpace(explicitWorkspaceRoot))
        {
            var (isValid, normalizedPath, pathError) = PathSecurity.ValidateWorkspaceRootPath(explicitWorkspaceRoot);
            if (!isValid || pathError != null)
            {
                return (false, string.Empty, pathError);
            }

            var fullPath = Path.GetFullPath(normalizedPath);

            // Check if path exists
            if (Directory.Exists(fullPath))
            {
                var dirName = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.Equals(dirName, ".dogdouspec", StringComparison.OrdinalIgnoreCase))
                {
                    var (isSafe, secError) = PathSecurity.VerifyWorkspaceDirectorySecurity(fullPath);
                    if (!isSafe || secError != null)
                    {
                        return (false, string.Empty, secError);
                    }

                    return (true, PathSecurity.NormalizeSeparators(fullPath), null);
                }

                // Check if directory contains a child .dogdouspec directory
                var childDogdou = Path.Combine(fullPath, ".dogdouspec");
                if (Directory.Exists(childDogdou))
                {
                    var (isSafe, secError) = PathSecurity.VerifyWorkspaceDirectorySecurity(childDogdou, parentProjectDirectory: fullPath);
                    if (!isSafe || secError != null)
                    {
                        return (false, string.Empty, secError);
                    }

                    return (true, PathSecurity.NormalizeSeparators(childDogdou), null);
                }

                return (false, string.Empty, Diagnostic.Error(
                    DiagnosticCodes.WorkspaceNotFound,
                    $"Explicit workspace root '{explicitWorkspaceRoot}' is not a .dogdouspec directory and does not contain a .dogdouspec subdirectory."));
            }

            return (false, string.Empty, Diagnostic.Error(
                DiagnosticCodes.WorkspaceNotFound,
                $"Explicit workspace root directory '{explicitWorkspaceRoot}' does not exist."));
        }

        // Ancestor walk from startDirectory
        var currentDir = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (currentDir != null)
        {
            // Check if currentDir itself is .dogdouspec
            if (string.Equals(currentDir.Name, ".dogdouspec", StringComparison.OrdinalIgnoreCase))
            {
                var (isSafe, secError) = PathSecurity.VerifyWorkspaceDirectorySecurity(currentDir.FullName, parentProjectDirectory: currentDir.Parent?.FullName);
                if (!isSafe || secError != null)
                {
                    return (false, string.Empty, secError);
                }

                return (true, PathSecurity.NormalizeSeparators(currentDir.FullName), null);
            }

            // Check if currentDir contains .dogdouspec directory
            var candidate = Path.Combine(currentDir.FullName, ".dogdouspec");
            if (Directory.Exists(candidate))
            {
                var (isSafe, secError) = PathSecurity.VerifyWorkspaceDirectorySecurity(candidate, parentProjectDirectory: currentDir.FullName);
                if (!isSafe || secError != null)
                {
                    return (false, string.Empty, secError);
                }

                return (true, PathSecurity.NormalizeSeparators(candidate), null);
            }

            currentDir = currentDir.Parent;
        }

        return (false, string.Empty, Diagnostic.Error(
            DiagnosticCodes.WorkspaceNotFound,
            $"No .dogdouspec workspace directory found in ancestor tree of '{startDirectory}'."));
    }

    /// <summary>
    /// Enumerates managed documents in the workspace according to scope.
    /// </summary>
    public static (bool Success, IReadOnlyList<ManagedDocument> Documents, IReadOnlyList<Diagnostic> Diagnostics) EnumerateDocuments(
        string workspaceRoot,
        string? iterationId = null,
        string? relativeDocumentPath = null)
    {
        var diagnostics = new List<Diagnostic>();
        var documents = new List<ManagedDocument>();

        if (!Directory.Exists(workspaceRoot))
        {
            diagnostics.Add(Diagnostic.Error(DiagnosticCodes.WorkspaceNotFound, $"Workspace root '{workspaceRoot}' does not exist."));
            return (false, documents, diagnostics);
        }

        var (isRootSafe, rootSafeError) = PathSecurity.VerifyWorkspaceDirectorySecurity(workspaceRoot);
        if (!isRootSafe || rootSafeError != null)
        {
            diagnostics.Add(rootSafeError!);
            return (false, documents, diagnostics);
        }

        // Document scope
        if (!string.IsNullOrWhiteSpace(relativeDocumentPath))
        {
            var (isValid, normalizedRelPath, pathError) = PathSecurity.ValidateRelativeDocumentPath(relativeDocumentPath);
            if (!isValid || pathError != null)
            {
                diagnostics.Add(pathError!);
                return (false, documents, diagnostics);
            }

            var fullPath = Path.Combine(workspaceRoot, normalizedRelPath.Replace('/', Path.DirectorySeparatorChar));
            var (isSafe, safeError) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, fullPath);
            if (!isSafe || safeError != null)
            {
                diagnostics.Add(safeError!);
                return (false, documents, diagnostics);
            }

            if (!File.Exists(fullPath))
            {
                diagnostics.Add(Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Document '{normalizedRelPath}' does not exist.", normalizedRelPath));
                return (false, documents, diagnostics);
            }

            string? iterId = null;
            var segments = normalizedRelPath.Split('/');
            if (segments.Length > 1)
            {
                iterId = segments[0];
            }

            documents.Add(new ManagedDocument(normalizedRelPath, fullPath, iterId));
            return (true, documents, diagnostics);
        }

        // Iteration scope
        if (!string.IsNullOrWhiteSpace(iterationId))
        {
            var (isValidIter, normalizedIterId, iterError) = ValidateIterationId(iterationId);
            if (!isValidIter || iterError != null)
            {
                diagnostics.Add(iterError!);
                return (false, documents, diagnostics);
            }

            var iterDir = Path.Combine(workspaceRoot, normalizedIterId);
            if (!Directory.Exists(iterDir))
            {
                diagnostics.Add(Diagnostic.Error(DiagnosticCodes.IterationNotFound, $"Iteration directory '{normalizedIterId}' does not exist."));
                return (false, documents, diagnostics);
            }

            var (isDirSafe, dirSafeError) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, iterDir);
            if (!isDirSafe || dirSafeError != null)
            {
                diagnostics.Add(dirSafeError!);
                return (false, documents, diagnostics);
            }

            var specPath = Path.Combine(iterDir, "spec.xml");
            var specRel = $"{normalizedIterId}/spec.xml";
            if (File.Exists(specPath))
            {
                var (isSpecSafe, specSafeErr) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, specPath);
                if (!isSpecSafe)
                {
                    diagnostics.Add(specSafeErr!);
                }
                else
                {
                    documents.Add(new ManagedDocument(specRel, specPath, normalizedIterId));
                }
            }
            else
            {
                diagnostics.Add(Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Required document '{specRel}' not found in iteration.", specRel));
            }

            var tasksPath = Path.Combine(iterDir, "tasks.xml");
            var tasksRel = $"{normalizedIterId}/tasks.xml";
            if (File.Exists(tasksPath))
            {
                var (isTasksSafe, tasksSafeErr) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, tasksPath);
                if (!isTasksSafe)
                {
                    diagnostics.Add(tasksSafeErr!);
                }
                else
                {
                    documents.Add(new ManagedDocument(tasksRel, tasksPath, normalizedIterId));
                }
            }
            else
            {
                diagnostics.Add(Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Required document '{tasksRel}' not found in iteration.", tasksRel));
            }

            return (diagnostics.Count == 0, documents, diagnostics);
        }

        // Workspace scope (all managed documents)
        var knowledgePath = Path.Combine(workspaceRoot, "knowledge.xml");
        if (File.Exists(knowledgePath))
        {
            var (isSafe, safeError) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, knowledgePath);
            if (!isSafe)
            {
                diagnostics.Add(safeError!);
            }
            else
            {
                documents.Add(new ManagedDocument("knowledge.xml", knowledgePath));
            }
        }
        else
        {
            diagnostics.Add(Diagnostic.Error(DiagnosticCodes.DocumentNotFound, "Required document 'knowledge.xml' not found in workspace.", "knowledge.xml"));
        }

        var backlogPath = Path.Combine(workspaceRoot, "backlog.xml");
        if (File.Exists(backlogPath))
        {
            var (isSafe, safeError) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, backlogPath);
            if (!isSafe)
            {
                diagnostics.Add(safeError!);
            }
            else
            {
                documents.Add(new ManagedDocument("backlog.xml", backlogPath));
            }
        }
        else
        {
            diagnostics.Add(Diagnostic.Error(DiagnosticCodes.DocumentNotFound, "Required document 'backlog.xml' not found in workspace.", "backlog.xml"));
        }

        // Discover iterations
        var subDirs = Directory.GetDirectories(workspaceRoot);
        // Sort in deterministic alphabetical/time order
        Array.Sort(subDirs, StringComparer.Ordinal);

        foreach (var subDir in subDirs)
        {
            var dirName = Path.GetFileName(subDir);
            // Ignore non-candidate ordinary directories (e.g. _schema, _skill, or non-iteration directories)
            if (!PathSecurity.IterationIdRegex.IsMatch(dirName))
            {
                continue;
            }

            var (isDirSafe, dirSafeError) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, subDir);
            if (!isDirSafe)
            {
                diagnostics.Add(dirSafeError!);
                continue;
            }

            var specFile = Path.Combine(subDir, "spec.xml");
            var specRel = $"{dirName}/spec.xml";
            if (File.Exists(specFile))
            {
                var (isSpecSafe, specSafeErr) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, specFile);
                if (!isSpecSafe)
                {
                    diagnostics.Add(specSafeErr!);
                }
                else
                {
                    documents.Add(new ManagedDocument(specRel, specFile, dirName));
                }
            }
            else
            {
                diagnostics.Add(Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Required document '{specRel}' not found in iteration.", specRel));
            }

            var tasksFile = Path.Combine(subDir, "tasks.xml");
            var tasksRel = $"{dirName}/tasks.xml";
            if (File.Exists(tasksFile))
            {
                var (isTasksSafe, tasksSafeErr) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, tasksFile);
                if (!isTasksSafe)
                {
                    diagnostics.Add(tasksSafeErr!);
                }
                else
                {
                    documents.Add(new ManagedDocument(tasksRel, tasksFile, dirName));
                }
            }
            else
            {
                diagnostics.Add(Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Required document '{tasksRel}' not found in iteration.", tasksRel));
            }
        }

        return (diagnostics.Count == 0, documents, diagnostics);
    }
}
