using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Serialization;
using DogdouSpec.Core.Time;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.XPath;

namespace DogdouSpec.Core.Transactions;

/// <summary>
/// Specification for an individual document replacement within a transaction.
/// </summary>
public sealed record TransactionDocumentOperation(
    string RelativePath,
    string ReplacementContent,
    int ExpectedRevision,
    int NewRevision);

/// <summary>Revision precondition for a document read by, but not replaced by, a transaction.</summary>
public sealed record TransactionReadPrecondition(string RelativePath, int ExpectedRevision);

/// <summary>
/// General atomic transaction engine for multi-document existing-file commits.
/// Stages replacements, flushes, validates prospective state, writes recovery marker, commits atomically, and cleans up.
/// </summary>
public static class WorkspaceTransactionCommitter
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Commit(
        string workspaceRoot,
        string commandName,
        IReadOnlyList<TransactionDocumentOperation> operations,
        IClock? clock = null,
        IFaultInjector? faultInjector = null,
        string version = "1.0",
        string? correlationId = null,
        IReadOnlyList<TransactionReadPrecondition>? readPreconditions = null)
    {
        clock ??= SystemClock.Instance;

        if (operations == null || operations.Count == 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Transaction must contain at least one operation.") });
        }

        if (correlationId != null && !ProjectSemanticIndex.IsValidTimeFirstId(correlationId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, $"Transaction correlation ID '{correlationId}' does not conform to the time-first ID grammar.") });
        }

        // 1. Validate workspace directory security
        var (isWsSafe, wsErr) = PathSecurity.VerifyWorkspaceDirectorySecurity(workspaceRoot);
        if (!isWsSafe || wsErr != null)
        {
            return (false, null, new[] { wsErr ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, "Workspace directory security verification failed.") });
        }

        // 2. Acquire writer lock
        var (lockAcquired, wsLock, lockError) = WorkspaceLock.Acquire(workspaceRoot);
        if (!lockAcquired || wsLock == null)
        {
            return (false, null, new[] { lockError! });
        }

        using (wsLock)
        {
            // 3. Startup recovery
            var (recSuccess, recError) = StartupRecovery.Run(workspaceRoot);
            if (!recSuccess || recError != null)
            {
                return (false, null, new[] { recError! });
            }

            // Read-only documents can influence a high-level command's
            // decision. Recheck their revisions while holding the writer lock
            // so a read-then-write command cannot commit against stale input.
            var readPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var read in readPreconditions ?? Array.Empty<TransactionReadPrecondition>())
            {
                var (isRelValid, normPath, relErr) = PathSecurity.ValidateRelativeDocumentPath(read.RelativePath);
                if (!isRelValid || relErr != null)
                {
                    return (false, null, new[] { relErr ?? Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid read-precondition document path '{read.RelativePath}'.") });
                }
                if (!readPaths.Add(normPath) || read.ExpectedRevision <= 0)
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Read precondition for '{normPath}' must be unique and use a positive expected revision.", normPath) });
                }

                var fullReadPath = Path.Combine(workspaceRoot, normPath.Replace('/', Path.DirectorySeparatorChar));
                var (isContained, contErr) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, fullReadPath);
                if (!isContained || contErr != null)
                {
                    return (false, null, new[] { contErr ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, $"Read-precondition path escapes workspace: '{normPath}'.") });
                }
                if (!File.Exists(fullReadPath))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Read-precondition document '{normPath}' does not exist in workspace.", normPath) });
                }

                try
                {
                    using var stream = File.OpenRead(fullReadPath);
                    using var reader = SecureXmlReaderFactory.CreateReader(stream);
                    var document = XDocument.Load(reader);
                    var revisionText = document.Root?.Attribute("revision")?.Value;
                    if (!int.TryParse(revisionText, CultureInfo.InvariantCulture, out var actualRevision) || actualRevision <= 0)
                    {
                        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Document '{normPath}' root revision attribute is missing, non-positive, or malformed.", normPath) });
                    }
                    if (actualRevision != read.ExpectedRevision)
                    {
                        return (false, null, new[] { new Diagnostic(DiagnosticCodes.RevisionConflict, "error", $"Expected read revision {read.ExpectedRevision} does not match actual revision {actualRevision} for document '{normPath}'.", normPath, ExpectedRevision: read.ExpectedRevision, ActualRevision: actualRevision) });
                    }
                }
                catch (XmlException xmlEx)
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Failed to parse read-precondition document '{normPath}': {xmlEx.Message}", normPath) });
                }
                catch (Exception ex)
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Failed to read read-precondition document '{normPath}': {ex.Message}", normPath) });
                }
            }

            // 4. Operation preconditions validation before any target read/stage/backup
            var seenTargetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var normalizedOps = new List<(string NormalizedPath, string FullTarget, TransactionDocumentOperation Op, string CanonicalContent)>();

            foreach (var op in operations)
            {
                // Validate and normalize relative path as a managed document reference
                var (isRelValid, normPath, relErr) = PathSecurity.ValidateRelativeDocumentPath(op.RelativePath);
                if (!isRelValid || relErr != null)
                {
                    return (false, null, new[] { relErr ?? Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid document path '{op.RelativePath}'.") });
                }

                // Reject duplicate target paths
                if (!seenTargetPaths.Add(normPath))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Duplicate operation target path '{normPath}' in transaction.") });
                }

                if (Encoding.UTF8.GetByteCount(op.ReplacementContent) > XPathQueryLimits.MaxDocumentBytes)
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"Replacement XML for '{normPath}' exceeds maximum allowed size of {XPathQueryLimits.MaxDocumentBytes} bytes.", normPath) });
                }

                // Normalize replacement XML to canonical managed document format
                string canonicalContent;
                try
                {
                    canonicalContent = ManagedDocumentSerializer.Normalize(op.ReplacementContent);
                }
                catch (XmlException xmlEx)
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Replacement XML for '{normPath}' is malformed: {xmlEx.Message}", normPath) });
                }
                catch (Exception ex)
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Failed to parse replacement XML for '{normPath}': {ex.Message}", normPath) });
                }

                // Enforce byte limits on canonical output
                if (Encoding.UTF8.GetByteCount(canonicalContent) > XPathQueryLimits.MaxDocumentBytes)
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"Replacement XML for '{normPath}' exceeds maximum allowed size of {XPathQueryLimits.MaxDocumentBytes} bytes.", normPath) });
                }

                // Verify containment and reparse points before opening target
                var fullTarget = Path.Combine(workspaceRoot, normPath.Replace('/', Path.DirectorySeparatorChar));
                var (isContained, contErr) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, fullTarget);
                if (!isContained || contErr != null)
                {
                    return (false, null, new[] { contErr ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, $"Target path escapes workspace: '{normPath}'.") });
                }

                // Existing-file operation must fail if target missing
                if (!File.Exists(fullTarget))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Target document '{normPath}' does not exist in workspace.", normPath) });
                }

                // Revision validation: ExpectedRevision and NewRevision must be positive integers
                if (op.ExpectedRevision <= 0)
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Expected revision for '{normPath}' must be a positive integer, but got {op.ExpectedRevision}.", normPath) });
                }

                if (op.NewRevision <= 0)
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"New revision for '{normPath}' must be a positive integer, but got {op.NewRevision}.", normPath) });
                }

                // Existing root revision must parse as positive integer and equal ExpectedRevision
                int actualRev;
                try
                {
                    using var s = File.OpenRead(fullTarget);
                    using var r = SecureXmlReaderFactory.CreateReader(s);
                    var xDoc = XDocument.Load(r);
                    var revStr = xDoc.Root?.Attribute("revision")?.Value;
                    if (string.IsNullOrWhiteSpace(revStr) || !int.TryParse(revStr, CultureInfo.InvariantCulture, out actualRev) || actualRev <= 0)
                    {
                        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Document '{normPath}' root revision attribute is missing, non-positive, or malformed.", normPath) });
                    }
                }
                catch (XmlException xmlEx)
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Failed to parse XML document '{normPath}': {xmlEx.Message}", normPath) });
                }
                catch (Exception ex)
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Failed to read '{normPath}': {ex.Message}", normPath) });
                }

                if (actualRev != op.ExpectedRevision)
                {
                    var diag = new Diagnostic(
                        DiagnosticCodes.RevisionConflict,
                        "error",
                        $"Expected revision {op.ExpectedRevision} does not match actual revision {actualRev} for document '{normPath}'.",
                        Document: normPath,
                        ExpectedRevision: op.ExpectedRevision,
                        ActualRevision: actualRev);
                    return (false, null, new[] { diag });
                }

                // NewRevision must equal ExpectedRevision + 1
                if (op.NewRevision != op.ExpectedRevision + 1)
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"New revision {op.NewRevision} must equal ExpectedRevision {op.ExpectedRevision} + 1 for document '{normPath}'.", normPath) });
                }

                // Replacement XML root revision must parse as positive integer and equal NewRevision
                int replacementRev;
                try
                {
                    using var strR = new StringReader(canonicalContent);
                    using var r = SecureXmlReaderFactory.CreateReader(strR);
                    var replXDoc = XDocument.Load(r);
                    var revStr = replXDoc.Root?.Attribute("revision")?.Value;
                    if (string.IsNullOrWhiteSpace(revStr) || !int.TryParse(revStr, CultureInfo.InvariantCulture, out replacementRev) || replacementRev <= 0)
                    {
                        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Replacement XML for '{normPath}' root revision attribute is missing, non-positive, or malformed.", normPath) });
                    }
                }
                catch (XmlException xmlEx)
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Replacement XML for '{normPath}' is malformed: {xmlEx.Message}", normPath) });
                }
                catch (Exception ex)
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Failed to parse replacement XML for '{normPath}': {ex.Message}", normPath) });
                }

                if (replacementRev != op.NewRevision)
                {
                    var diag = new Diagnostic(
                        DiagnosticCodes.RevisionConflict,
                        "error",
                        $"Replacement XML root revision {replacementRev} does not match NewRevision {op.NewRevision} for document '{normPath}'.",
                        Document: normPath,
                        ExpectedRevision: op.NewRevision,
                        ActualRevision: replacementRev);
                    return (false, null, new[] { diag });
                }

                normalizedOps.Add((normPath, fullTarget, op, canonicalContent));
            }

            // 5. Staging directory under _tmp (same filesystem volume)
            var txId = correlationId ?? $"{clock.UtcNow:yyyyMMddTHHmmssZ}-tx-{Guid.NewGuid():N}";
            var txDir = Path.Combine(workspaceRoot, "_tmp", $"tx_{txId}");
            var stagedDir = Path.Combine(txDir, "staged");
            var backupDir = Path.Combine(txDir, "backup");

            try
            {
                Directory.CreateDirectory(stagedDir);
                Directory.CreateDirectory(backupDir);

                faultInjector?.InjectFaultIfMatched(FaultPhase.BeforeStaging);

                var prospectiveDocs = new List<ProspectiveDocument>();

                foreach (var (normPath, _, op, canonicalContent) in normalizedOps)
                {
                    var stagedFile = Path.Combine(stagedDir, normPath.Replace('/', '_'));
                    using (var fs = new FileStream(stagedFile, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var sw = new StreamWriter(fs, Utf8NoBom))
                    {
                        sw.Write(canonicalContent);
                        sw.Flush();
                        fs.Flush(true);
                    }

                    prospectiveDocs.Add(new ProspectiveDocument(
                        normPath,
                        canonicalContent,
                        IsNew: false,
                        ExpectedRevision: op.ExpectedRevision));
                }

                faultInjector?.InjectFaultIfMatched(FaultPhase.AfterStagingBeforeValidation);

                // 6. Prospective validation against prospective workspace view
                var valResult = SchemaValidator.ValidateProspective(workspaceRoot, prospectiveDocs, version);
                if (!valResult.IsValid)
                {
                    PathSecurity.SafeDeleteCliTempEntry(workspaceRoot, txDir);
                    return (false, null, valResult.Diagnostics);
                }

                // 7. Backup existing files and flush
                foreach (var (normPath, fullTarget, _, _) in normalizedOps)
                {
                    var backupFile = Path.Combine(backupDir, normPath.Replace('/', '_'));
                    using (var src = File.OpenRead(fullTarget))
                    using (var dst = new FileStream(backupFile, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        src.CopyTo(dst);
                        dst.Flush(true);
                    }
                }

                // 8. Write recovery marker in 'prepared' state and flush
                var markerPath = Path.Combine(txDir, "recovery.xml");
                WriteMarkerXml(markerPath, txId, "prepared", normalizedOps, stagedDir, backupDir, clock.UtcNow);

                faultInjector?.InjectFaultIfMatched(FaultPhase.AfterValidationBeforeCommitMarker);

                // 9. Update recovery marker to 'publishing' state and flush
                WriteMarkerXml(markerPath, txId, "publishing", normalizedOps, stagedDir, backupDir, clock.UtcNow);

                faultInjector?.InjectFaultIfMatched(FaultPhase.AfterCommitMarkerBeforePublish);

                // 10. Atomic publication per file using Windows/.NET atomic move/replace
                var mutatedDocs = new List<MutatedDocument>();
                var isFirst = true;

                foreach (var (normPath, fullTarget, op, _) in normalizedOps)
                {
                    var stagedFile = Path.Combine(stagedDir, normPath.Replace('/', '_'));

                    // Atomic replacement primitive on NTFS (same volume): File.Move with overwrite:true
                    // replaces the directory entry atomically without streaming copy over live target.
                    File.Move(stagedFile, fullTarget, overwrite: true);

                    mutatedDocs.Add(new MutatedDocument(normPath, op.NewRevision, op.ExpectedRevision));

                    if (isFirst && normalizedOps.Count > 1)
                    {
                        isFirst = false;
                        faultInjector?.InjectFaultIfMatched(FaultPhase.DuringMultiFileCommitAfterFirstFile);
                    }
                }

                // 11. Update recovery marker to 'committed' state and flush
                WriteMarkerXml(markerPath, txId, "committed", normalizedOps, stagedDir, backupDir, clock.UtcNow);

                faultInjector?.InjectFaultIfMatched(FaultPhase.AfterPublishBeforeCleanup);

                // 12. Cleanup transaction directory
                PathSecurity.SafeDeleteCliTempEntry(workspaceRoot, txDir);

                return (true, new MutationEnvelope(commandName, mutatedDocs), Array.Empty<Diagnostic>());
            }
            catch (Exception ex)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.CommitFailed, $"Transaction commit failed: {ex.Message}") });
            }
        }
    }

    private static void WriteMarkerXml(
        string markerPath,
        string txId,
        string state,
        IReadOnlyList<(string NormalizedPath, string FullTarget, TransactionDocumentOperation Op, string CanonicalContent)> operations,
        string stagedDir,
        string backupDir,
        DateTime createdAt)
    {
        var root = new XElement("recovery-marker",
            new XAttribute("id", txId),
            new XAttribute("state", state),
            new XAttribute("created_at", createdAt.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));

        foreach (var (normPath, _, op, _) in operations)
        {
            var stagedFile = Path.Combine(stagedDir, normPath.Replace('/', '_'));
            var backupFile = Path.Combine(backupDir, normPath.Replace('/', '_'));

            root.Add(new XElement("operation",
                new XAttribute("type", "replace"),
                new XAttribute("target", normPath),
                new XAttribute("staged", stagedFile),
                new XAttribute("backup", backupFile),
                new XAttribute("expected_revision", op.ExpectedRevision.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("new_revision", op.NewRevision.ToString(CultureInfo.InvariantCulture))));
        }

        var xml = ManagedDocumentSerializer.Serialize(new XDocument(root));
        using var fs = new FileStream(markerPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var sw = new StreamWriter(fs, Utf8NoBom);
        sw.Write(xml);
        sw.Flush();
        fs.Flush(true);
    }
}
