using System.Globalization;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Revisions;

namespace DogdouSpec.Core.Iterations;

/// <summary>
/// Reconciles optional CLI confirmation inputs with an iteration-confirmation request.
/// Request attributes remain authoritative when present; explicit values may only fill
/// missing attributes or assert equality.
/// </summary>
public static class IterationConfirmationRequestResolver
{
    public static (bool Success, string? RequestXml, Diagnostic? Error) Reconcile(
        string workspaceRoot,
        XDocument requestDocument,
        string? explicitIterationId = null,
        int? explicitSpecRevision = null,
        int? explicitTasksRevision = null)
    {
        var root = requestDocument.Root;
        if (root == null || !string.Equals(root.Name.LocalName, "iteration-confirmation", StringComparison.Ordinal))
        {
            return (false, null, Diagnostic.Error(
                DiagnosticCodes.InvalidArgument,
                "Root element must be iteration-confirmation."));
        }

        var requestIterationId = root.Attribute("iteration")?.Value;
        if (!string.IsNullOrWhiteSpace(explicitIterationId))
        {
            if (!string.IsNullOrWhiteSpace(requestIterationId) &&
                !string.Equals(explicitIterationId, requestIterationId, StringComparison.Ordinal))
            {
                return (false, null, Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    $"Iteration argument '{explicitIterationId}' disagrees with request attribute '{requestIterationId}'."));
            }

            if (string.IsNullOrWhiteSpace(requestIterationId))
            {
                root.SetAttributeValue("iteration", explicitIterationId);
                requestIterationId = explicitIterationId;
            }
        }
        else if (string.IsNullOrWhiteSpace(requestIterationId))
        {
            requestIterationId = ResolveIterationId(workspaceRoot);
            if (string.IsNullOrWhiteSpace(requestIterationId))
            {
                return (false, null, Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "Iteration could not be determined for confirmation. Specify --iteration or set request @iteration."));
            }

            root.SetAttributeValue("iteration", requestIterationId);
        }

        var specResult = ReconcileRevision(
            root,
            "expected_spec_revision",
            explicitSpecRevision,
            workspaceRoot,
            $"{requestIterationId}/spec.xml",
            "Expected revision");
        if (!specResult.Success)
        {
            return (false, null, specResult.Error);
        }

        var action = root.Attribute("action")?.Value;
        var requestTasksRevision = root.Attribute("expected_tasks_revision")?.Value;
        var tasksRevisionRequired = action is "continue" or "complete";
        if (explicitTasksRevision.HasValue || !string.IsNullOrWhiteSpace(requestTasksRevision) || tasksRevisionRequired)
        {
            var tasksResult = ReconcileRevision(
                root,
                "expected_tasks_revision",
                explicitTasksRevision,
                workspaceRoot,
                $"{requestIterationId}/tasks.xml",
                "Expected tasks revision");
            if (!tasksResult.Success)
            {
                return (false, null, tasksResult.Error);
            }
        }

        return (true, root.ToString(SaveOptions.DisableFormatting), null);
    }

    private static (bool Success, Diagnostic? Error) ReconcileRevision(
        XElement root,
        string attributeName,
        int? explicitRevision,
        string workspaceRoot,
        string relativeDocumentPath,
        string optionLabel)
    {
        if (explicitRevision.HasValue && explicitRevision.Value <= 0)
        {
            return (false, Diagnostic.Error(
                DiagnosticCodes.InvalidArgument,
                $"{optionLabel} must be positive."));
        }

        var requestValue = root.Attribute(attributeName)?.Value;
        if (!string.IsNullOrWhiteSpace(requestValue))
        {
            if (!int.TryParse(requestValue, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedRequestRevision) ||
                parsedRequestRevision <= 0)
            {
                return (false, Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    $"Request attribute '{attributeName}' must be a positive integer."));
            }

            if (explicitRevision.HasValue && explicitRevision.Value != parsedRequestRevision)
            {
                return (false, Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    $"{optionLabel} argument '{explicitRevision.Value}' disagrees with request attribute '{requestValue}'."));
            }

            return (true, null);
        }

        if (explicitRevision.HasValue)
        {
            root.SetAttributeValue(attributeName, explicitRevision.Value.ToString(CultureInfo.InvariantCulture));
            return (true, null);
        }

        var (revisionResolved, revision, revisionError) = DocumentRevisionResolver.ReadDocumentRevision(
            workspaceRoot,
            relativeDocumentPath);
        if (!revisionResolved || revisionError != null)
        {
            return (false, revisionError ?? Diagnostic.Error(
                DiagnosticCodes.DocumentNotFound,
                $"Could not resolve revision for '{relativeDocumentPath}'.",
                relativeDocumentPath));
        }

        root.SetAttributeValue(attributeName, revision.ToString(CultureInfo.InvariantCulture));
        return (true, null);
    }

    private static string? ResolveIterationId(string workspaceRoot)
    {
        var (success, result, _) = IterationLister.List(workspaceRoot);
        if (!success || result == null || result.Iterations.Count == 0)
        {
            return null;
        }

        var active = result.Iterations
            .Where(i => string.Equals(i.Status, "active", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (active.Count == 1)
        {
            return active[0].Id;
        }

        var draft = result.Iterations
            .Where(i => string.Equals(i.Status, "draft", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (draft.Count == 1)
        {
            return draft[0].Id;
        }

        return result.Iterations.Count == 1 ? result.Iterations[0].Id : null;
    }
}
