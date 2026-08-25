using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Resources;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Iterations;

/// <summary>
/// Read-only discovery and validation of date-prefixed candidate iterations in a workspace.
/// </summary>
public static class IterationLister
{
    public static (bool Success, IterationListResult? Result, IReadOnlyList<Diagnostic> Diagnostics) List(
        string workspaceRoot,
        string version = "1.0")
    {
        var diagnostics = new List<Diagnostic>();
        var iterations = new List<IterationSummary>();

        if (!Directory.Exists(workspaceRoot))
        {
            diagnostics.Add(Diagnostic.Error(
                DiagnosticCodes.WorkspaceNotFound,
                $"Workspace root directory '{workspaceRoot}' does not exist."));
            return (false, null, diagnostics);
        }

        var (isRootSafe, rootSafeError) = PathSecurity.VerifyWorkspaceDirectorySecurity(workspaceRoot);
        if (!isRootSafe || rootSafeError != null)
        {
            diagnostics.Add(rootSafeError!);
            return (false, null, diagnostics);
        }

        var subDirs = Directory.GetDirectories(workspaceRoot);
        Array.Sort(subDirs, StringComparer.Ordinal);

        foreach (var subDir in subDirs)
        {
            var dirName = Path.GetFileName(subDir);

            // Ordinary non-candidate dirs and _schema/_skill/_tmp are ignored
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
            var tasksFile = Path.Combine(subDir, "tasks.xml");
            var tasksRel = $"{dirName}/tasks.xml";

            var specExists = File.Exists(specFile);
            var tasksExists = File.Exists(tasksFile);

            if (!specExists)
            {
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.DocumentNotFound,
                    $"Required document '{specRel}' not found in candidate iteration directory '{dirName}'.",
                    specRel));
            }

            if (!tasksExists)
            {
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.DocumentNotFound,
                    $"Required document '{tasksRel}' not found in candidate iteration directory '{dirName}'.",
                    tasksRel));
            }

            if (!specExists || !tasksExists)
            {
                continue;
            }

            // Validate spec.xml schema
            var specDoc = new ManagedDocument(specRel, specFile, dirName);
            var specSchemaRes = SchemaValidator.ValidateDocument(specDoc, version);
            if (!specSchemaRes.IsValid)
            {
                diagnostics.AddRange(specSchemaRes.Diagnostics);
            }

            // Validate tasks.xml schema
            var tasksDoc = new ManagedDocument(tasksRel, tasksFile, dirName);
            var tasksSchemaRes = SchemaValidator.ValidateDocument(tasksDoc, version);
            if (!tasksSchemaRes.IsValid)
            {
                diagnostics.AddRange(tasksSchemaRes.Diagnostics);
            }

            if (!specSchemaRes.IsValid || !tasksSchemaRes.IsValid)
            {
                continue;
            }

            // Parse root metadata from spec.xml and tasks.xml
            try
            {
                using var specStream = File.OpenRead(specFile);
                using var specReader = SecureXmlReaderFactory.CreateReader(specStream);
                var specXDoc = XDocument.Load(specReader, LoadOptions.SetLineInfo);
                var specRoot = specXDoc.Root;

                if (specRoot == null || specRoot.Name.LocalName != "iteration")
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.SchemaValidationError,
                        $"Root element of '{specRel}' must be <iteration>.",
                        specRel));
                    continue;
                }

                var rootId = specRoot.Attribute("id")?.Value ?? string.Empty;
                if (!string.Equals(rootId, dirName, StringComparison.Ordinal))
                {
                    var lineInfo = (IXmlLineInfo)specRoot;
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.IterationIdMismatch,
                        $"Iteration root ID '{rootId}' in '{specRel}' does not match candidate directory name '{dirName}'.",
                        specRel,
                        lineInfo.HasLineInfo() ? lineInfo.LineNumber : null,
                        lineInfo.HasLineInfo() ? lineInfo.LinePosition : null));
                }

                var kind = specRoot.Attribute("kind")?.Value ?? string.Empty;
                var status = specRoot.Attribute("status")?.Value ?? string.Empty;
                var createdAt = specRoot.Attribute("created_at")?.Value ?? string.Empty;
                var specRevStr = specRoot.Attribute("revision")?.Value ?? string.Empty;
                int.TryParse(specRevStr, CultureInfo.InvariantCulture, out var specRevision);

                var indexEl = specRoot.Element("index");

                using var tasksStream = File.OpenRead(tasksFile);
                using var tasksReader = SecureXmlReaderFactory.CreateReader(tasksStream);
                var tasksXDoc = XDocument.Load(tasksReader, LoadOptions.SetLineInfo);
                var tasksRoot = tasksXDoc.Root;

                if (tasksRoot == null || tasksRoot.Name.LocalName != "tasks")
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.SchemaValidationError,
                        $"Root element of '{tasksRel}' must be <tasks>.",
                        tasksRel));
                    continue;
                }

                var tasksIterAttr = tasksRoot.Attribute("iteration")?.Value ?? string.Empty;
                if (!string.Equals(tasksIterAttr, dirName, StringComparison.Ordinal))
                {
                    var lineInfo = (IXmlLineInfo)tasksRoot;
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.TasksIterationMismatch,
                        $"Tasks iteration attribute '{tasksIterAttr}' in '{tasksRel}' does not match iteration directory name '{dirName}'.",
                        tasksRel,
                        lineInfo.HasLineInfo() ? lineInfo.LineNumber : null,
                        lineInfo.HasLineInfo() ? lineInfo.LinePosition : null));
                }

                var tasksRevStr = tasksRoot.Attribute("revision")?.Value ?? string.Empty;
                int.TryParse(tasksRevStr, CultureInfo.InvariantCulture, out var tasksRevision);

                var dirHasErrors = diagnostics.Any(d =>
                    d.Severity == "error" &&
                    d.Document != null &&
                    (d.Document.StartsWith(dirName + "/", StringComparison.Ordinal) || d.Document.StartsWith(dirName + "\\", StringComparison.Ordinal)));

                if (!dirHasErrors)
                {
                    iterations.Add(new IterationSummary(
                        Id: dirName,
                        RelativePath: dirName,
                        Kind: kind,
                        Status: status,
                        CreatedAt: createdAt,
                        SpecRevision: specRevision,
                        TasksRevision: tasksRevision,
                        IndexElement: indexEl != null ? new XElement(indexEl) : null));
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.XmlParseError,
                    $"Failed to parse XML in candidate iteration '{dirName}': {ex.Message}",
                    specRel));
            }
        }

        if (diagnostics.Any(d => d.Severity == "error"))
        {
            var sortedDiags = diagnostics
                .OrderBy(d => d.Document, StringComparer.Ordinal)
                .ThenBy(d => d.Line ?? int.MaxValue)
                .ThenBy(d => d.Column ?? int.MaxValue)
                .ThenBy(d => d.Code, StringComparer.Ordinal)
                .ToList();
            return (false, null, sortedDiags);
        }

        var sortedIterations = iterations
            .OrderBy(it => DateTimeOffset.TryParse(it.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dt) ? dt : DateTimeOffset.MinValue)
            .ThenBy(it => it.Id, StringComparer.Ordinal)
            .ToList();

        return (true, new IterationListResult(workspaceRoot, sortedIterations), Array.Empty<Diagnostic>());
    }
}
