using System.Globalization;
using System.Text;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Serialization;
using DogdouSpec.Core.Time;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;
using DogdouSpec.Core.XPath;

namespace DogdouSpec.Core.Iterations;

/// <summary>
/// Authoritative porcelain service for owner-authorized acceptance criterion authoring.
/// Supports defining/replacing seeded pending criteria and adding new criteria using
/// optimistic spec revision checks, canonical serialization, prospective validation,
/// and atomic workspace transaction infrastructure.
/// </summary>
public static class IterationCriterionAuthor
{
    private const int MaxCriterionTextLength = 4096;

    public static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Define(
        string workspaceRoot,
        string iterationId,
        string text,
        string? criterionId = null,
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

        // 2. Policy validation of criterion text
        var (isTextValid, textFailureReason) = IterationCriterionPolicy.Validate(text, criterionId);
        if (!isTextValid)
        {
            return (false, null, new[] { Diagnostic.Error(
                DiagnosticCodes.CriterionUndefined,
                textFailureReason ?? "Acceptance criterion text is undefined.") });
        }

        // 3. Workspace security verification
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

        // 4. Optimistic spec revision check
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
        var acceptanceEl = specRoot.Element("product")?.Element("acceptance") ??
                           specRoot.Element("research")?.Element("acceptance");

        if (acceptanceEl == null)
        {
            return (false, null, new[] { Diagnostic.Error(
                DiagnosticCodes.DanglingReference,
                $"Iteration '{normIterId}' does not contain an <acceptance> element.",
                specRelPath) });
        }

        var criteria = acceptanceEl.Elements("criterion").ToList();

        // 5. Locate target criterion to define or replace
        XElement? targetCriterion = null;
        if (!string.IsNullOrWhiteSpace(criterionId))
        {
            targetCriterion = criteria.FirstOrDefault(c => string.Equals(c.Attribute("id")?.Value, criterionId, StringComparison.Ordinal));
            if (targetCriterion == null)
            {
                if (int.TryParse(criterionId, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index >= 1 && index <= criteria.Count)
                {
                    targetCriterion = criteria[index - 1];
                }
                else
                {
                    return (false, null, new[] { Diagnostic.Error(
                        DiagnosticCodes.DanglingReference,
                        $"Acceptance criterion '{criterionId}' does not exist in iteration '{normIterId}'.",
                        specRelPath) });
                }
            }
        }
        else
        {
            var undefinedCriteria = criteria.Where(c => !IterationCriterionPolicy.IsDefined(c.Value)).ToList();
            if (undefinedCriteria.Count == 1)
            {
                targetCriterion = undefinedCriteria[0];
            }
            else if (undefinedCriteria.Count > 1)
            {
                return (false, null, new[] { Diagnostic.Error(
                    DiagnosticCodes.AmbiguousReference,
                    $"Iteration '{normIterId}' has multiple undefined criteria ({undefinedCriteria.Count}). Specify target criterion ID or 1-based index to indicate which criterion to update.",
                    specRelPath) });
            }
            else if (criteria.Count == 1)
            {
                targetCriterion = criteria[0];
            }
            else if (criteria.Count > 1)
            {
                return (false, null, new[] { Diagnostic.Error(
                    DiagnosticCodes.AmbiguousReference,
                    $"Iteration '{normIterId}' has multiple criteria ({criteria.Count}). Specify target criterion ID or 1-based index to indicate which criterion to update.",
                    specRelPath) });
            }
            else
            {
                return (false, null, new[] { Diagnostic.Error(
                    DiagnosticCodes.DanglingReference,
                    $"Iteration '{normIterId}' has no acceptance criteria. Use add to create a criterion.",
                    specRelPath) });
            }
        }

        // 6. Decided criteria cannot be rewritten
        var targetId = targetCriterion.Attribute("id")?.Value ?? string.Empty;
        var decision = targetCriterion.Attribute("decision")?.Value ?? "pending";
        if (!string.Equals(decision, "pending", StringComparison.Ordinal))
        {
            return (false, null, new[] { Diagnostic.Error(
                DiagnosticCodes.OwnerDecisionRequired,
                $"Cannot modify decided acceptance criterion '{targetId}' with decision '{decision}'. Decided criteria cannot be rewritten.",
                specRelPath) });
        }

        // 7. Update criterion text and document metadata
        targetCriterion.Value = text.Trim();
        var newRevision = actualSpecRev + 1;
        specRoot.SetAttributeValue("revision", newRevision.ToString(CultureInfo.InvariantCulture));
        var nowUtc = clock.UtcNow;
        var isoTime = nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        specRoot.SetAttributeValue("updated_at", isoTime);

        // 8. Canonical structural serialization
        var serializedXml = ManagedDocumentSerializer.Serialize(specDoc);

        // 9. Atomic transaction commit
        var operation = new TransactionDocumentOperation(specRelPath, serializedXml, actualSpecRev, newRevision);
        return WorkspaceTransactionCommitter.Commit(
            workspaceRoot,
            "iteration criterion",
            new[] { operation },
            clock,
            faultInjector,
            version);
    }

    public static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Add(
        string workspaceRoot,
        string iterationId,
        string text,
        string? criterionId = null,
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

        // 2. Policy validation of criterion text
        var (isTextValid, textFailureReason) = IterationCriterionPolicy.Validate(text, criterionId);
        if (!isTextValid)
        {
            return (false, null, new[] { Diagnostic.Error(
                DiagnosticCodes.CriterionUndefined,
                textFailureReason ?? "Acceptance criterion text is undefined.") });
        }

        // 3. Workspace security verification
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

        // 4. Optimistic spec revision check
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
        var section = specRoot.Element("product") ?? specRoot.Element("research");
        if (section == null)
        {
            return (false, null, new[] { Diagnostic.Error(
                DiagnosticCodes.SchemaValidationError,
                $"Iteration document '{specRelPath}' is missing <product> or <research> section.",
                specRelPath) });
        }

        var acceptanceEl = section.Element("acceptance");
        if (acceptanceEl == null)
        {
            acceptanceEl = new XElement("acceptance");
            section.Add(acceptanceEl);
        }

        var existingCriteria = acceptanceEl.Elements("criterion").ToList();
        var existingIds = existingCriteria
            .Select(c => c.Attribute("id")?.Value)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToHashSet(StringComparer.Ordinal);

        // 5. Determine deterministic criterion identifier
        string newId;
        if (!string.IsNullOrWhiteSpace(criterionId))
        {
            if (!ProjectSemanticIndex.IsValidTimeFirstId(criterionId))
            {
                return (false, null, new[] { Diagnostic.Error(
                    DiagnosticCodes.InvalidIdGrammar,
                    $"Criterion ID '{criterionId}' does not conform to the time-first ID grammar.",
                    specRelPath) });
            }

            if (existingIds.Contains(criterionId))
            {
                return (false, null, new[] { Diagnostic.Error(
                    DiagnosticCodes.DuplicateId,
                    $"Acceptance criterion with ID '{criterionId}' already exists in '{specRelPath}'.",
                    specRelPath) });
            }

            newId = criterionId;
        }
        else
        {
            var firstDash = normIterId!.IndexOf('-');
            var timePrefix = normIterId!.Substring(0, firstDash);
            var slug = normIterId!.Substring(firstDash + 1);

            if (existingCriteria.Count == 0 && !existingIds.Contains($"{timePrefix}-crit-{slug}"))
            {
                newId = $"{timePrefix}-crit-{slug}";
            }
            else
            {
                var n = existingCriteria.Count + 1;
                while (existingIds.Contains($"{timePrefix}-crit-{slug}-{n}"))
                {
                    n++;
                }
                newId = $"{timePrefix}-crit-{slug}-{n}";
            }
        }

        // 6. Add new criterion element
        var newCritElement = new XElement("criterion",
            new XAttribute("id", newId),
            new XAttribute("decision", "pending"),
            text.Trim());
        acceptanceEl.Add(newCritElement);

        var newRevision = actualSpecRev + 1;
        specRoot.SetAttributeValue("revision", newRevision.ToString(CultureInfo.InvariantCulture));
        var nowUtc = clock.UtcNow;
        var isoTime = nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        specRoot.SetAttributeValue("updated_at", isoTime);

        // 7. Canonical structural serialization
        var serializedXml = ManagedDocumentSerializer.Serialize(specDoc);

        // 8. Atomic transaction commit
        var operation = new TransactionDocumentOperation(specRelPath, serializedXml, actualSpecRev, newRevision);
        return WorkspaceTransactionCommitter.Commit(
            workspaceRoot,
            "iteration criterion",
            new[] { operation },
            clock,
            faultInjector,
            version);
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

        if (text == null)
        {
            return (false, null, Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Criterion text cannot be null."));
        }

        if (text.Length > MaxCriterionTextLength || Encoding.UTF8.GetByteCount(text) > MaxCriterionTextLength * 2)
        {
            return (false, null, Diagnostic.Error(
                DiagnosticCodes.LimitExceeded,
                $"Criterion text exceeds maximum allowed length of {MaxCriterionTextLength} characters."));
        }

        if (expectedSpecRevision.HasValue && expectedSpecRevision.Value <= 0)
        {
            return (false, null, Diagnostic.Error(DiagnosticCodes.InvalidArgument, "--expected-revision must be a positive integer."));
        }

        return (true, normIterId, null);
    }

    private static (bool Success, XDocument? Document, int Revision, Diagnostic? Error) LoadSpecDocument(
        string workspaceRoot,
        string specRelPath,
        string normIterId)
    {
        var (isSpecRelValid, _, specRelErr) = PathSecurity.ValidateRelativeDocumentPath(specRelPath);
        if (!isSpecRelValid || specRelErr != null)
        {
            return (false, null, 0, specRelErr ?? Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid document path '{specRelPath}'."));
        }

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
