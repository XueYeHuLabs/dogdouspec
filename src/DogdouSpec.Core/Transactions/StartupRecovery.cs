using System.Globalization;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Transactions;

/// <summary>
/// Startup recovery performed at the start of every write transaction while holding the writer lock.
/// Validates recovery markers, applies forward completion or cleanup, and removes staging artifacts safely.
/// </summary>
public static class StartupRecovery
{
    /// <summary>
    /// Detects CLI-owned recovery artifacts without modifying the workspace.
    /// Dry-run callers use this to avoid previewing state that a real commit
    /// would first change through startup recovery.
    /// </summary>
    public static (bool Success, bool Pending, Diagnostic? Error) InspectPending(string workspaceRoot)
    {
        var tmpDir = Path.Combine(workspaceRoot, "_tmp");
        if (!Directory.Exists(tmpDir))
        {
            return (true, false, null);
        }

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(tmpDir))
            {
                var name = Path.GetFileName(entry);
                if (string.Equals(name, "writer.lock", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!PathSecurity.IsSafeCliTempChild(workspaceRoot, entry))
                {
                    continue;
                }

                if (Directory.Exists(entry) &&
                    (name.StartsWith("create_", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith("tx_", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith("staging_", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith("temp_", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith("backup_", StringComparison.OrdinalIgnoreCase)))
                {
                    return (true, true, null);
                }

                if (File.Exists(entry))
                {
                    return (true, true, null);
                }
            }

            return (true, false, null);
        }
        catch (Exception ex)
        {
            return (false, false, Diagnostic.Error(
                DiagnosticCodes.RecoveryFailed,
                $"Pending recovery inspection failed due to filesystem error: {ex.Message}"));
        }
    }

    public static (bool Success, Diagnostic? Error) Run(string workspaceRoot)
    {
        var tmpDir = Path.Combine(workspaceRoot, "_tmp");
        if (!Directory.Exists(tmpDir))
        {
            return (true, null);
        }

        try
        {
            // 1. Process directories under _tmp
            var subDirs = Directory.GetDirectories(tmpDir);
            foreach (var subDir in subDirs)
            {
                var dirName = Path.GetFileName(subDir);

                // Only process verified CLI-owned directories
                if (!PathSecurity.IsSafeCliTempChild(workspaceRoot, subDir))
                {
                    // Non-CLI / user directory in _tmp is preserved untouched
                    continue;
                }

                if (dirName.StartsWith("create_", StringComparison.OrdinalIgnoreCase))
                {
                    var (createSuccess, createErr) = RecoverCreateDirectory(workspaceRoot, subDir);
                    if (!createSuccess || createErr != null)
                    {
                        return (false, createErr);
                    }
                }
                else if (dirName.StartsWith("tx_", StringComparison.OrdinalIgnoreCase) ||
                         dirName.StartsWith("staging_", StringComparison.OrdinalIgnoreCase))
                {
                    var (txSuccess, txErr) = RecoverTransactionDirectory(workspaceRoot, subDir);
                    if (!txSuccess || txErr != null)
                    {
                        return (false, txErr);
                    }
                }
                else if (dirName.StartsWith("temp_", StringComparison.OrdinalIgnoreCase) ||
                         dirName.StartsWith("backup_", StringComparison.OrdinalIgnoreCase))
                {
                    PathSecurity.SafeDeleteCliTempEntry(workspaceRoot, subDir);
                }
            }

            // 2. Process stray temporary files directly under _tmp (excluding writer.lock)
            var files = Directory.GetFiles(tmpDir);
            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                if (string.Equals(fileName, "writer.lock", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (PathSecurity.IsSafeCliTempChild(workspaceRoot, file))
                {
                    PathSecurity.SafeDeleteCliTempEntry(workspaceRoot, file);
                }
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, Diagnostic.Error(
                DiagnosticCodes.RecoveryFailed,
                $"Startup recovery failed due to filesystem error: {ex.Message}"));
        }
    }

    private static (bool Success, Diagnostic? Error) RecoverCreateDirectory(string workspaceRoot, string createDir)
    {
        var markerPath = Path.Combine(createDir, "marker.xml");
        if (!File.Exists(markerPath))
        {
            // Interrupted before marker was written
            var subDirs = Directory.GetDirectories(createDir);
            foreach (var sub in subDirs)
            {
                var iterName = Path.GetFileName(sub);
                var (isIdValid, normId, _) = PathSecurity.ValidateIterationId(iterName);
                if (isIdValid)
                {
                    var liveTarget = Path.Combine(workspaceRoot, normId);
                    if (Directory.Exists(liveTarget))
                    {
                        var val = SchemaValidator.Validate(workspaceRoot, iterationId: normId);
                        if (!val.IsValid)
                        {
                            return (false, Diagnostic.Error(
                                DiagnosticCodes.RecoveryFailed,
                                $"Incomplete live target directory '{normId}' without valid create marker."));
                        }
                    }
                }
            }

            PathSecurity.SafeDeleteCliTempEntry(workspaceRoot, createDir);
            return (true, null);
        }

        XDocument doc;
        try
        {
            using var stream = File.OpenRead(markerPath);
            using var reader = SecureXmlReaderFactory.CreateReader(stream);
            doc = XDocument.Load(reader);
        }
        catch (Exception ex)
        {
            return (false, Diagnostic.Error(
                DiagnosticCodes.RecoveryFailed,
                $"Failed to parse create recovery marker at '{markerPath}': {ex.Message}"));
        }

        try
        {
            var root = doc.Root;
            if (root == null || root.Name.LocalName != "create-marker")
            {
                return (false, Diagnostic.Error(
                    DiagnosticCodes.RecoveryFailed,
                    $"Tampered or invalid create recovery marker at '{markerPath}'."));
            }

            var iterId = root.Attribute("iteration_id")?.Value;
            var state = root.Attribute("state")?.Value ?? "prepared";

            if (string.IsNullOrEmpty(iterId))
            {
                // Abandoned create dir without target iteration ID
                PathSecurity.SafeDeleteCliTempEntry(workspaceRoot, createDir);
                return (true, null);
            }

            var (isIdValid, normalizedIterId, idErr) = PathSecurity.ValidateIterationId(iterId);
            if (!isIdValid || idErr != null)
            {
                return (false, Diagnostic.Error(
                    DiagnosticCodes.RecoveryFailed,
                    $"Create recovery marker contains invalid iteration identifier '{iterId}'."));
            }

            var targetDir = Path.Combine(workspaceRoot, normalizedIterId);
            var stagedIterDir = Path.Combine(createDir, normalizedIterId);

            if (string.Equals(state, "prepared", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state, "staged", StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(targetDir))
                {
                    var val = SchemaValidator.Validate(workspaceRoot, iterationId: normalizedIterId);
                    if (!val.IsValid)
                    {
                        return (false, Diagnostic.Error(
                            DiagnosticCodes.RecoveryFailed,
                            $"Live iteration directory '{normalizedIterId}' exists in incomplete or invalid state while create marker is in prepared state. Refusing to stream-copy or guess."));
                    }
                }

                PathSecurity.SafeDeleteCliTempEntry(workspaceRoot, createDir);
                return (true, null);
            }
            else if (string.Equals(state, "publishing", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(state, "committed", StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(targetDir))
                {
                    // Target exists: validate spec.xml and tasks.xml exist and are valid
                    var specPath = Path.Combine(targetDir, "spec.xml");
                    var tasksPath = Path.Combine(targetDir, "tasks.xml");
                    if (!File.Exists(specPath) || !File.Exists(tasksPath))
                    {
                        return (false, Diagnostic.Error(
                            DiagnosticCodes.RecoveryFailed,
                            $"Live iteration directory '{normalizedIterId}' is missing spec.xml or tasks.xml. Tampered or partial target state; refusing to stream-copy."));
                    }

                    var val = SchemaValidator.Validate(workspaceRoot, iterationId: normalizedIterId);
                    if (!val.IsValid)
                    {
                        return (false, Diagnostic.Error(
                            DiagnosticCodes.RecoveryFailed,
                            $"Live iteration directory '{normalizedIterId}' failed validation: {string.Join("; ", val.Diagnostics.Select(d => d.Message))}."));
                    }
                }
                else
                {
                    // Target does not exist: forward-complete via atomic Directory.Move if staged folder exists
                    if (Directory.Exists(stagedIterDir))
                    {
                        var stagedSpec = Path.Combine(stagedIterDir, "spec.xml");
                        var stagedTasks = Path.Combine(stagedIterDir, "tasks.xml");
                        if (!File.Exists(stagedSpec) || !File.Exists(stagedTasks))
                        {
                            return (false, Diagnostic.Error(
                                DiagnosticCodes.RecoveryFailed,
                                $"Staged iteration directory '{normalizedIterId}' is missing spec.xml or tasks.xml."));
                        }

                        Directory.Move(stagedIterDir, targetDir);

                        var val = SchemaValidator.Validate(workspaceRoot, iterationId: normalizedIterId);
                        if (!val.IsValid)
                        {
                            return (false, Diagnostic.Error(
                                DiagnosticCodes.RecoveryFailed,
                                $"Recovered iteration directory '{normalizedIterId}' failed validation."));
                        }
                    }
                    else
                    {
                        return (false, Diagnostic.Error(
                            DiagnosticCodes.RecoveryFailed,
                            $"Create transaction was in publishing state but neither live target nor valid staged directory exists for '{normalizedIterId}'."));
                    }
                }

                PathSecurity.SafeDeleteCliTempEntry(workspaceRoot, createDir);
                return (true, null);
            }
            else
            {
                return (false, Diagnostic.Error(
                    DiagnosticCodes.RecoveryFailed,
                    $"Unknown create recovery marker state '{state}' at '{markerPath}'."));
            }
        }
        catch (Exception ex)
        {
            return (false, Diagnostic.Error(
                DiagnosticCodes.RecoveryFailed,
                $"Failed to recover create transaction from '{markerPath}': {ex.Message}"));
        }
    }

    private static (bool Success, Diagnostic? Error) RecoverTransactionDirectory(string workspaceRoot, string txDir)
    {
        var recoveryPath = Path.Combine(txDir, "recovery.xml");
        if (!File.Exists(recoveryPath))
        {
            // Transaction was interrupted before writing recovery marker; safe cleanup
            PathSecurity.SafeDeleteCliTempEntry(workspaceRoot, txDir);
            return (true, null);
        }

        XDocument doc;
        try
        {
            using var stream = File.OpenRead(recoveryPath);
            using var reader = SecureXmlReaderFactory.CreateReader(stream);
            doc = XDocument.Load(reader);
        }
        catch (Exception ex)
        {
            return (false, Diagnostic.Error(
                DiagnosticCodes.RecoveryFailed,
                $"Failed to parse transaction recovery marker at '{recoveryPath}': {ex.Message}"));
        }

        try
        {
            var root = doc.Root;

            if (root == null || root.Name.LocalName != "recovery-marker")
            {
                return (false, Diagnostic.Error(
                    DiagnosticCodes.RecoveryFailed,
                    $"Tampered or invalid transaction recovery marker at '{recoveryPath}'."));
            }

            var state = root.Attribute("state")?.Value ?? "prepared";
            if (!string.Equals(state, "prepared", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(state, "publishing", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(state, "committed", StringComparison.OrdinalIgnoreCase))
            {
                return (false, Diagnostic.Error(
                    DiagnosticCodes.RecoveryFailed,
                    $"Unknown transaction recovery marker state '{state}'."));
            }

            var opElements = root.Elements("operation").ToList();
            if (opElements.Count == 0)
            {
                return (false, Diagnostic.Error(
                    DiagnosticCodes.RecoveryFailed,
                    "Transaction recovery marker contains no operations."));
            }

            var parsedOps = new List<(string TargetRel, string TargetFullPath, string StagedPath, string BackupPath, int ExpectedRev, int NewRev)>();

            foreach (var op in opElements)
            {
                var targetRel = op.Attribute("target")?.Value;
                var stagedPath = op.Attribute("staged")?.Value;
                var backupPath = op.Attribute("backup")?.Value ?? string.Empty;
                var expRevStr = op.Attribute("expected_revision")?.Value;
                var newRevStr = op.Attribute("new_revision")?.Value;

                if (string.IsNullOrWhiteSpace(targetRel) || string.IsNullOrWhiteSpace(stagedPath))
                {
                    return (false, Diagnostic.Error(
                        DiagnosticCodes.RecoveryFailed,
                        "Transaction recovery marker has missing operation paths."));
                }

                var (isTargetValid, normalizedTarget, targetErr) = PathSecurity.ValidateRelativeDocumentPath(targetRel);
                if (!isTargetValid || targetErr != null)
                {
                    return (false, Diagnostic.Error(
                        DiagnosticCodes.RecoveryFailed,
                        $"Transaction recovery marker references invalid target path '{targetRel}'."));
                }

                var targetFullPath = Path.Combine(workspaceRoot, normalizedTarget.Replace('/', Path.DirectorySeparatorChar));
                var (isContained, contErr) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, targetFullPath);
                if (!isContained || contErr != null)
                {
                    return (false, Diagnostic.Error(
                        DiagnosticCodes.RecoveryFailed,
                        $"Transaction recovery marker target escapes workspace: '{targetRel}'."));
                }

                if (!PathSecurity.IsSafeCliTempChild(workspaceRoot, stagedPath))
                {
                    return (false, Diagnostic.Error(
                        DiagnosticCodes.RecoveryFailed,
                        $"Transaction recovery marker references unsafe staged path '{stagedPath}'."));
                }

                if (!string.IsNullOrEmpty(backupPath) && !PathSecurity.IsSafeCliTempChild(workspaceRoot, backupPath))
                {
                    return (false, Diagnostic.Error(
                        DiagnosticCodes.RecoveryFailed,
                        $"Transaction recovery marker references unsafe backup path '{backupPath}'."));
                }

                if (!int.TryParse(expRevStr, CultureInfo.InvariantCulture, out var expectedRev) || expectedRev <= 0 ||
                    !int.TryParse(newRevStr, CultureInfo.InvariantCulture, out var newRev) || newRev != expectedRev + 1)
                {
                    return (false, Diagnostic.Error(
                        DiagnosticCodes.RecoveryFailed,
                        $"Transaction recovery marker contains invalid revisions for '{targetRel}'."));
                }

                parsedOps.Add((normalizedTarget, targetFullPath, stagedPath, backupPath, expectedRev, newRev));
            }

            if (string.Equals(state, "prepared", StringComparison.OrdinalIgnoreCase))
            {
                // Prepared: Rollback/abort. Targets must be in expected old state or restored from backup.
                foreach (var (targetRel, targetFullPath, _, backupPath, expectedRev, _) in parsedOps)
                {
                    var (hasTargetRev, targetRev, _) = TryGetRootRevision(targetFullPath);
                    if (hasTargetRev && targetRev == expectedRev)
                    {
                        // Target is intact at expected revision
                        continue;
                    }

                    // Target was modified or missing; attempt restore from backup
                    if (!string.IsNullOrEmpty(backupPath) && File.Exists(backupPath))
                    {
                        var (hasBackupRev, backupRev, _) = TryGetRootRevision(backupPath);
                        if (hasBackupRev && backupRev == expectedRev)
                        {
                            File.Move(backupPath, targetFullPath, overwrite: true);
                            continue;
                        }
                    }

                    return (false, Diagnostic.Error(
                        DiagnosticCodes.RecoveryFailed,
                        $"Prepared transaction target '{targetRel}' is not at expected revision and valid backup is unavailable."));
                }

                PathSecurity.SafeDeleteCliTempEntry(workspaceRoot, txDir);
                return (true, null);
            }
            else
            {
                // Publishing or Committed: Roll forward to all-new.
                foreach (var (targetRel, targetFullPath, stagedPath, _, expectedRev, newRev) in parsedOps)
                {
                    var (hasTargetRev, targetRev, _) = TryGetRootRevision(targetFullPath);

                    if (hasTargetRev && targetRev == newRev)
                    {
                        // Already published to new revision; verify well-formed XML
                        continue;
                    }

                    if (hasTargetRev && targetRev == expectedRev)
                    {
                        // Target is still at old revision; forward-publish from staged file
                        if (File.Exists(stagedPath))
                        {
                            var (hasStagedRev, stagedRev, _) = TryGetRootRevision(stagedPath);
                            if (hasStagedRev && stagedRev == newRev)
                            {
                                File.Move(stagedPath, targetFullPath, overwrite: true);
                                continue;
                            }
                        }

                        return (false, Diagnostic.Error(
                            DiagnosticCodes.RecoveryFailed,
                            $"Neither valid staged file nor valid new target exists for '{targetRel}'. Staged file was consumed or missing without target update."));
                    }

                    if (!hasTargetRev && !File.Exists(targetFullPath))
                    {
                        // Target missing; publish from staged file
                        if (File.Exists(stagedPath))
                        {
                            var (hasStagedRev, stagedRev, _) = TryGetRootRevision(stagedPath);
                            if (hasStagedRev && stagedRev == newRev)
                            {
                                File.Move(stagedPath, targetFullPath, overwrite: true);
                                continue;
                            }
                        }

                        return (false, Diagnostic.Error(
                            DiagnosticCodes.RecoveryFailed,
                            $"Target file '{targetRel}' and staged file are both missing."));
                    }

                    // Target revision is neither expected nor new revision (tampered / corrupted)
                    return (false, Diagnostic.Error(
                        DiagnosticCodes.RecoveryFailed,
                        $"Target document '{targetRel}' has unexpected revision {targetRev}. Expected {expectedRev} or {newRev}."));
                }

                // Final validation of recovered mutated documents
                foreach (var (targetRel, targetFullPath, _, _, _, _) in parsedOps)
                {
                    string? iterId = null;
                    var segs = targetRel.Split('/');
                    if (segs.Length > 1)
                    {
                        iterId = segs[0];
                    }

                    var managedDoc = new ManagedDocument(targetRel, targetFullPath, iterId);
                    var docVal = SchemaValidator.ValidateDocument(managedDoc);
                    if (!docVal.IsValid)
                    {
                        return (false, Diagnostic.Error(
                            DiagnosticCodes.RecoveryFailed,
                            $"Recovered document '{targetRel}' failed schema validation: {string.Join("; ", docVal.Diagnostics.Select(d => d.Message))}."));
                    }
                }

                PathSecurity.SafeDeleteCliTempEntry(workspaceRoot, txDir);
                return (true, null);
            }
        }
        catch (Exception ex)
        {
            return (false, Diagnostic.Error(
                DiagnosticCodes.RecoveryFailed,
                $"Failed to recover transaction from '{recoveryPath}': {ex.Message}"));
        }
    }

    private static (bool Success, int Revision, string? Error) TryGetRootRevision(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return (false, 0, "File not found");
            }

            using var stream = File.OpenRead(filePath);
            using var reader = SecureXmlReaderFactory.CreateReader(stream);
            var doc = XDocument.Load(reader);
            var revStr = doc.Root?.Attribute("revision")?.Value;
            if (int.TryParse(revStr, CultureInfo.InvariantCulture, out var rev) && rev > 0)
            {
                return (true, rev, null);
            }

            return (false, 0, "Revision attribute missing, non-positive, or malformed");
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }
}
