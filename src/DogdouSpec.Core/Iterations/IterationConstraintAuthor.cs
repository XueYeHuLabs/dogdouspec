using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Progression;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Serialization;
using DogdouSpec.Core.Time;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;
using DogdouSpec.Core.XPath;

namespace DogdouSpec.Core.Iterations;

public sealed record IterationConstraintItem(
    string Id,
    string? Kind,
    string Text);

/// <summary>
/// Authoritative porcelain service for iteration-level constraint authoring and management.
/// Supports adding, listing, and removing iteration-level constraints (e.g. environment, configuration)
/// with optimistic revision checking, canonical serialization, schema validation, and atomic commits.
/// </summary>
public static class IterationConstraintAuthor
{
    private const int MaxConstraintTextLength = 4096;
    private static readonly Regex TokenKeyRegex = new(@"^[a-z][a-z0-9-]*$", RegexOptions.Compiled);
    private static readonly Regex AutoDetectKindRegex = new(@"^(?:\[([a-z][a-z0-9-]*)\]|([a-z][a-z0-9-]*):)\s*(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Add(
        string workspaceRoot,
        string iterationId,
        string text,
        string? kind = null,
        string? constraintId = null,
        int? expectedSpecRevision = null,
        IClock? clock = null,
        IFaultInjector? faultInjector = null,
        string version = "1.0")
    {
        clock ??= SystemClock.Instance;

        // 1. Basic argument validation
        var (argValid, normIterId, argDiag) = ValidateArguments(workspaceRoot, iterationId, text, expectedSpecRevision);
        if (!argValid || argDiag != null)
        {
            return (false, null, new[] { argDiag! });
        }

        var trimmedText = text.Trim();
        if (trimmedText.Length > MaxConstraintTextLength)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Constraint text exceeds maximum length of {MaxConstraintTextLength} characters.") });
        }

        // Auto-detect kind if not explicitly specified
        string? resolvedKind = kind?.Trim().ToLowerInvariant();
        string resolvedText = trimmedText;
        if (string.IsNullOrEmpty(resolvedKind))
        {
            var match = AutoDetectKindRegex.Match(trimmedText);
            if (match.Success)
            {
                var detectedKind = !string.IsNullOrEmpty(match.Groups[1].Value) ? match.Groups[1].Value : match.Groups[2].Value;
                resolvedKind = detectedKind.ToLowerInvariant();
                resolvedText = match.Groups[3].Value.Trim();
            }
        }

        if (!string.IsNullOrEmpty(resolvedKind) && !TokenKeyRegex.IsMatch(resolvedKind))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Constraint kind '{resolvedKind}' must conform to token format (lowercase letters, numbers, hyphens).") });
        }

        if (string.IsNullOrWhiteSpace(resolvedText))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Constraint text cannot be empty.") });
        }

        // 2. Workspace security verification
        var (isWsSafe, wsErr) = PathSecurity.VerifyWorkspaceDirectorySecurity(workspaceRoot);
        if (!isWsSafe || wsErr != null)
        {
            return (false, null, new[] { wsErr ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, "Workspace directory security verification failed.") });
        }

        var specRelPath = $"{normIterId}/spec.xml";
        var (loadSuccess, specDoc, actualSpecRev, docDiag) = LoadSpecDocument(workspaceRoot, specRelPath, normIterId!);
        if (!loadSuccess || specDoc == null)
        {
            return (false, null, new[] { docDiag! });
        }

        // 3. Optimistic spec revision check
        if (expectedSpecRevision.HasValue && expectedSpecRevision.Value != actualSpecRev)
        {
            return (false, null, new[] { new Diagnostic(
                DiagnosticCodes.RevisionConflict,
                "error",
                $"Expected spec revision {expectedSpecRevision.Value} does not match actual revision {actualSpecRev} for document '{specRelPath}'.",
                Document: specRelPath,
                ExpectedRevision: expectedSpecRevision.Value,
                ActualRevision: actualSpecRev) });
        }

        var specRoot = specDoc.Root!;
        var iterStatus = specRoot.Attribute("status")?.Value ?? "draft";
        if (string.Equals(iterStatus, "completed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(iterStatus, "cancelled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(iterStatus, "superseded", StringComparison.OrdinalIgnoreCase))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.OwnerDecisionRequired, $"Cannot modify constraints of terminal iteration '{normIterId}' with status '{iterStatus}'.", specRelPath) });
        }

        // 4. Determine deterministic constraint ID
        var constraintsEl = specRoot.Element("constraints");
        var existingConstraints = constraintsEl?.Elements("constraint").ToList() ?? new List<XElement>();
        var existingIds = existingConstraints
            .Select(c => c.Attribute("id")?.Value)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToHashSet(StringComparer.Ordinal);

        string newId;
        if (!string.IsNullOrWhiteSpace(constraintId))
        {
            var trimmedId = constraintId.Trim();
            if (!ProjectSemanticIndex.IsValidTimeFirstId(trimmedId))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, $"Constraint ID '{trimmedId}' does not conform to time-first grammar (YYYYMMDD-name or YYYYMMDDTHHmmssZ-name).", specRelPath) });
            }

            if (existingIds.Contains(trimmedId))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DuplicateId, $"Iteration constraint with ID '{trimmedId}' already exists in '{specRelPath}'.", specRelPath) });
            }

            newId = trimmedId;
        }
        else
        {
            var firstDash = normIterId!.IndexOf('-');
            var timePrefix = normIterId!.Substring(0, firstDash);
            var slug = normIterId!.Substring(firstDash + 1);

            if (existingConstraints.Count == 0 && !existingIds.Contains($"{timePrefix}-constraint-{slug}"))
            {
                newId = $"{timePrefix}-constraint-{slug}";
            }
            else
            {
                var n = existingConstraints.Count + 1;
                while (existingIds.Contains($"{timePrefix}-constraint-{slug}-{n}"))
                {
                    n++;
                }
                newId = $"{timePrefix}-constraint-{slug}-{n}";
            }
        }

        // 5. Ensure <constraints> element exists in schema-valid sequence
        if (constraintsEl == null)
        {
            constraintsEl = new XElement("constraints");
            var designEl = specRoot.Element("design");
            var confirmEl = specRoot.Element("confirmations");
            if (designEl != null)
            {
                designEl.AddBeforeSelf(constraintsEl);
            }
            else if (confirmEl != null)
            {
                confirmEl.AddBeforeSelf(constraintsEl);
            }
            else
            {
                specRoot.Add(constraintsEl);
            }
        }

        // 6. Append new constraint
        var newConstraintEl = new XElement("constraint", new XAttribute("id", newId));
        if (!string.IsNullOrEmpty(resolvedKind))
        {
            newConstraintEl.SetAttributeValue("kind", resolvedKind);
        }
        newConstraintEl.Value = resolvedText;
        constraintsEl.Add(newConstraintEl);

        var newRevision = actualSpecRev + 1;
        specRoot.SetAttributeValue("revision", newRevision.ToString(CultureInfo.InvariantCulture));
        var nowUtc = clock.UtcNow;
        var isoTime = nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        specRoot.SetAttributeValue("updated_at", isoTime);

        // 7. Canonical serialization and atomic commit
        var serializedXml = ManagedDocumentSerializer.Serialize(specDoc);
        var operation = new TransactionDocumentOperation(specRelPath, serializedXml, actualSpecRev, newRevision);
        return WorkspaceTransactionCommitter.Commit(
            workspaceRoot,
            "iteration constraint add",
            new[] { operation },
            clock,
            faultInjector,
            version);
    }

    public static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Remove(
        string workspaceRoot,
        string iterationId,
        string constraintId,
        int? expectedSpecRevision = null,
        IClock? clock = null,
        IFaultInjector? faultInjector = null,
        string version = "1.0")
    {
        clock ??= SystemClock.Instance;

        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Workspace root must be specified.") });
        }

        if (string.IsNullOrWhiteSpace(iterationId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Iteration ID must be specified.") });
        }

        if (string.IsNullOrWhiteSpace(constraintId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Constraint ID must be specified.") });
        }

        var (isIterValid, normIterId, iterErr) = WorkspaceDiscovery.ValidateIterationId(iterationId);
        if (!isIterValid || iterErr != null)
        {
            return (false, null, new[] { iterErr ?? Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid iteration ID '{iterationId}'.") });
        }

        var (isWsSafe, wsErr) = PathSecurity.VerifyWorkspaceDirectorySecurity(workspaceRoot);
        if (!isWsSafe || wsErr != null)
        {
            return (false, null, new[] { wsErr ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, "Workspace directory security verification failed.") });
        }

        var specRelPath = $"{normIterId}/spec.xml";
        var (loadSuccess, specDoc, actualSpecRev, docDiag) = LoadSpecDocument(workspaceRoot, specRelPath, normIterId!);
        if (!loadSuccess || specDoc == null)
        {
            return (false, null, new[] { docDiag! });
        }

        if (expectedSpecRevision.HasValue && expectedSpecRevision.Value != actualSpecRev)
        {
            return (false, null, new[] { new Diagnostic(
                DiagnosticCodes.RevisionConflict,
                "error",
                $"Expected spec revision {expectedSpecRevision.Value} does not match actual revision {actualSpecRev} for document '{specRelPath}'.",
                Document: specRelPath,
                ExpectedRevision: expectedSpecRevision.Value,
                ActualRevision: actualSpecRev) });
        }

        var specRoot = specDoc.Root!;
        var iterStatus = specRoot.Attribute("status")?.Value ?? "draft";
        if (string.Equals(iterStatus, "completed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(iterStatus, "cancelled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(iterStatus, "superseded", StringComparison.OrdinalIgnoreCase))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.OwnerDecisionRequired, $"Cannot modify constraints of terminal iteration '{normIterId}' with status '{iterStatus}'.", specRelPath) });
        }

        var constraintsEl = specRoot.Element("constraints");
        if (constraintsEl == null)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.ResourceNotFound, $"Iteration '{normIterId}' has no constraints.", specRelPath) });
        }

        var targetConstraint = constraintsEl.Elements("constraint")
            .FirstOrDefault(c => string.Equals(c.Attribute("id")?.Value, constraintId.Trim(), StringComparison.Ordinal));
        if (targetConstraint == null)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.ResourceNotFound, $"Iteration constraint '{constraintId}' not found in '{specRelPath}'.", specRelPath) });
        }

        targetConstraint.Remove();
        if (!constraintsEl.Elements("constraint").Any())
        {
            constraintsEl.Remove();
        }

        var newRevision = actualSpecRev + 1;
        specRoot.SetAttributeValue("revision", newRevision.ToString(CultureInfo.InvariantCulture));
        var nowUtc = clock.UtcNow;
        var isoTime = nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        specRoot.SetAttributeValue("updated_at", isoTime);

        var serializedXml = ManagedDocumentSerializer.Serialize(specDoc);
        var operation = new TransactionDocumentOperation(specRelPath, serializedXml, actualSpecRev, newRevision);
        return WorkspaceTransactionCommitter.Commit(
            workspaceRoot,
            "iteration constraint remove",
            new[] { operation },
            clock,
            faultInjector,
            version);
    }

    public static (bool Success, string? IterationId, IReadOnlyList<IterationConstraintItem> Constraints, IReadOnlyList<Diagnostic> Diagnostics) List(
        string workspaceRoot,
        string? iterationId = null)
    {
        var (discoverSuccess, root, discoverError) = WorkspaceDiscovery.FindWorkspaceRoot(workspaceRoot, Environment.CurrentDirectory);
        if (!discoverSuccess || discoverError != null)
        {
            return (false, null, Array.Empty<IterationConstraintItem>(), new[] { discoverError ?? Diagnostic.Error(DiagnosticCodes.WorkspaceNotFound, "Workspace root could not be determined.") });
        }

        var (iterOk, resolvedIterId, iterErr) = ProgressionEngine.ResolveTargetIteration(root, iterationId);
        if (!iterOk || iterErr != null || string.IsNullOrEmpty(resolvedIterId))
        {
            return (false, null, Array.Empty<IterationConstraintItem>(), new[] { iterErr ?? Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Failed to resolve iteration.") });
        }

        var specRelPath = $"{resolvedIterId}/spec.xml";
        var (loadSuccess, specDoc, _, docDiag) = LoadSpecDocument(root, specRelPath, resolvedIterId);
        if (!loadSuccess || specDoc == null)
        {
            return (false, resolvedIterId, Array.Empty<IterationConstraintItem>(), new[] { docDiag! });
        }

        var constraintsEl = specDoc.Root?.Element("constraints");
        if (constraintsEl == null)
        {
            return (true, resolvedIterId, Array.Empty<IterationConstraintItem>(), Array.Empty<Diagnostic>());
        }

        var list = new List<IterationConstraintItem>();
        foreach (var c in constraintsEl.Elements("constraint"))
        {
            var id = (string?)c.Attribute("id") ?? string.Empty;
            var kind = (string?)c.Attribute("kind");
            var text = c.Value.Trim();
            list.Add(new IterationConstraintItem(id, kind, text));
        }

        return (true, resolvedIterId, list, Array.Empty<Diagnostic>());
    }

    private static (bool Valid, string? NormIterId, Diagnostic? Error) ValidateArguments(
        string workspaceRoot,
        string iterationId,
        string text,
        int? expectedSpecRevision)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return (false, null, Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Workspace root must be specified."));
        }

        if (string.IsNullOrWhiteSpace(iterationId))
        {
            return (false, null, Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Iteration ID must be specified."));
        }

        var (isIterValid, normIterId, iterErr) = WorkspaceDiscovery.ValidateIterationId(iterationId);
        if (!isIterValid || iterErr != null)
        {
            return (false, null, iterErr ?? Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid iteration ID '{iterationId}'."));
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return (false, null, Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Constraint text must be specified."));
        }

        if (expectedSpecRevision.HasValue && expectedSpecRevision.Value <= 0)
        {
            return (false, null, Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Expected spec revision must be a positive integer."));
        }

        return (true, normIterId, null);
    }

    private static (bool Success, XDocument? Document, int ActualRevision, Diagnostic? Error) LoadSpecDocument(
        string workspaceRoot,
        string specRelPath,
        string normIterId)
    {
        var fullSpecPath = Path.Combine(workspaceRoot, specRelPath.Replace('/', Path.DirectorySeparatorChar));
        var (isSpecContained, specContErr) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, fullSpecPath);
        if (!isSpecContained || specContErr != null)
        {
            return (false, null, 0, specContErr ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, $"Target path escapes workspace: '{specRelPath}'."));
        }

        if (!File.Exists(fullSpecPath))
        {
            return (false, null, 0, Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Document '{specRelPath}' does not exist in workspace.", specRelPath));
        }

        var specFileInfo = new FileInfo(fullSpecPath);
        if (specFileInfo.Length > XPathQueryLimits.MaxDocumentBytes)
        {
            return (false, null, 0, Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"Document '{specRelPath}' exceeds maximum allowed size of {XPathQueryLimits.MaxDocumentBytes} bytes.", specRelPath));
        }

        XDocument specDoc;
        try
        {
            using var specStream = File.OpenRead(fullSpecPath);
            using var specReader = SecureXmlReaderFactory.CreateReader(specStream);
            specDoc = XDocument.Load(specReader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (Exception ex)
        {
            return (false, null, 0, Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Failed to load XML document '{specRelPath}': {ex.Message}", specRelPath));
        }

        var specRoot = specDoc.Root;
        if (specRoot == null || !string.Equals(specRoot.Name.LocalName, "iteration", StringComparison.Ordinal))
        {
            return (false, null, 0, Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Document root in '{specRelPath}' must be <iteration>.", specRelPath));
        }

        var actualSpecRevStr = specRoot.Attribute("revision")?.Value;
        if (string.IsNullOrWhiteSpace(actualSpecRevStr) || !int.TryParse(actualSpecRevStr, CultureInfo.InvariantCulture, out var actualSpecRev) || actualSpecRev <= 0)
        {
            return (false, null, 0, Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Invalid or missing root revision in '{specRelPath}'.", specRelPath));
        }

        return (true, specDoc, actualSpecRev, null);
    }
}
