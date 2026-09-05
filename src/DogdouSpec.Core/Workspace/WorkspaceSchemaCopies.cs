using System.Xml;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Resources;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Validation;

namespace DogdouSpec.Core.Workspace;

public sealed record SchemaCopyFileStatus(string Name, string Path, string State);

public sealed record SchemaCopyStatusResult(
    string WorkspaceRoot,
    string Version,
    IReadOnlyList<SchemaCopyFileStatus> Files)
{
    public bool InSync => Files.All(file => file.State == "matching");
    public int Matching => Files.Count(file => file.State == "matching");
    public int Modified => Files.Count(file => file.State == "modified");
    public int Missing => Files.Count(file => file.State == "missing");
}

public sealed record SchemaCopySyncResult(
    string WorkspaceRoot,
    string Version,
    int Changed,
    SchemaCopyStatusResult Status);

/// <summary>
/// Inspects and transactionally refreshes optional readable schema copies.
/// Embedded schema resources remain authoritative for validation.
/// </summary>
public static class WorkspaceSchemaCopies
{
    private const int MaxSchemaFileSizeBytes = 1024 * 1024;

    public static (bool Success, SchemaCopyStatusResult? Result, IReadOnlyList<Diagnostic> Diagnostics) Inspect(
        string workspaceRoot,
        string version)
    {
        var diagnostics = new List<Diagnostic>();
        if (!EmbeddedResources.IsVersionSupported(version))
        {
            diagnostics.Add(Diagnostic.Error(
                DiagnosticCodes.UnsupportedVersion,
                $"Schema version '{version}' is not supported. Supported versions: {string.Join(", ", EmbeddedResources.SupportedVersions)}."));
            return (false, null, diagnostics);
        }

        var (rootSafe, rootError) = PathSecurity.VerifyWorkspaceDirectorySecurity(workspaceRoot);
        if (!rootSafe || rootError != null)
        {
            diagnostics.Add(rootError ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, "Workspace root is not secure."));
            return (false, null, diagnostics);
        }

        var schemaDir = Path.Combine(workspaceRoot, "_schema");
        if (File.Exists(schemaDir))
        {
            diagnostics.Add(Diagnostic.Error(
                DiagnosticCodes.FilesystemError,
                "Workspace schema path '_schema' is a file instead of a directory.",
                "_schema"));
            return (false, null, diagnostics);
        }

        if (Directory.Exists(schemaDir))
        {
            var (schemaDirSafe, schemaDirError) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, schemaDir);
            if (!schemaDirSafe || schemaDirError != null)
            {
                diagnostics.Add(schemaDirError ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, "Workspace schema directory is not secure.", "_schema"));
                return (false, null, diagnostics);
            }
        }

        var files = new List<SchemaCopyFileStatus>();
        foreach (var schemaName in EmbeddedResources.SchemaNames.OrderBy(name => name, StringComparer.Ordinal))
        {
            var relPath = $"_schema/{schemaName}.xsd";
            var fullPath = Path.Combine(schemaDir, $"{schemaName}.xsd");
            if (!File.Exists(fullPath))
            {
                files.Add(new SchemaCopyFileStatus(schemaName, relPath, "missing"));
                continue;
            }

            try
            {
                var (fileSafe, fileError) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, fullPath);
                if (!fileSafe || fileError != null)
                {
                    diagnostics.Add(fileError ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, $"Schema copy '{relPath}' is not secure.", relPath));
                    continue;
                }

                var embeddedBytes = EmbeddedResources.GetSchemaBytes(schemaName, version);
                if (embeddedBytes == null)
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.SchemaNotFound,
                        $"Embedded schema '{schemaName}' for version '{version}' was not found.",
                        relPath));
                    continue;
                }

                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var matching = stream.Length <= MaxSchemaFileSizeBytes
                    && stream.Length == embeddedBytes.Length;
                if (matching)
                {
                    var actualBytes = new byte[embeddedBytes.Length];
                    stream.ReadExactly(actualBytes);
                    matching = stream.ReadByte() == -1 && actualBytes.AsSpan().SequenceEqual(embeddedBytes);
                }
                files.Add(new SchemaCopyFileStatus(schemaName, relPath, matching ? "matching" : "modified"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(Diagnostic.Error(
                    WorkspaceSchemaDriftDetector.UnreadableSchemaCopy,
                    $"Cannot inspect schema copy '{relPath}': {ex.Message}",
                    relPath));
            }
        }

        var result = new SchemaCopyStatusResult(workspaceRoot, version, files);
        return (diagnostics.Count == 0, result, diagnostics);
    }

    public static (bool Success, SchemaCopySyncResult? Result, IReadOnlyList<Diagnostic> Diagnostics) Sync(
        string workspaceRoot,
        string expectedVersion)
    {
        if (!EmbeddedResources.IsVersionSupported(expectedVersion))
        {
            return (false, null, new[]
            {
                Diagnostic.Error(
                    DiagnosticCodes.UnsupportedVersion,
                    $"Schema version '{expectedVersion}' is not supported. Supported versions: {string.Join(", ", EmbeddedResources.SupportedVersions)}.")
            });
        }

        var (lockAcquired, workspaceLock, lockError) = WorkspaceLock.Acquire(workspaceRoot);
        if (!lockAcquired || workspaceLock == null)
        {
            return (false, null, new[] { lockError! });
        }

        using (workspaceLock)
        {
            var (recovered, recoveryError) = StartupRecovery.Run(workspaceRoot);
            if (!recovered || recoveryError != null)
            {
                return (false, null, new[] { recoveryError! });
            }

            var versionDiagnostics = ValidateManagedDocumentVersions(workspaceRoot, expectedVersion);
            if (versionDiagnostics.Count > 0)
            {
                return (false, null, versionDiagnostics);
            }

            var (inspected, before, inspectDiagnostics) = Inspect(workspaceRoot, expectedVersion);
            if (!inspected || before == null)
            {
                return (false, null, inspectDiagnostics);
            }

            var changedFiles = before.Files.Where(file => file.State != "matching").ToList();
            if (changedFiles.Count == 0)
            {
                return (true, new SchemaCopySyncResult(workspaceRoot, expectedVersion, 0, before), Array.Empty<Diagnostic>());
            }

            var tempDir = Path.Combine(workspaceRoot, "_tmp", $"schema_sync_{Guid.NewGuid():N}");
            var schemaDir = Path.Combine(workspaceRoot, "_schema");
            var applied = new List<(string Target, string Backup, bool Existed)>();
            var cleanupStaging = false;

            try
            {
                if (!PathSecurity.IsSafeCliTempChild(workspaceRoot, tempDir))
                {
                    return (false, null, new[]
                    {
                        Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, "Schema synchronization staging path is not a safe CLI-owned workspace path.")
                    });
                }

                Directory.CreateDirectory(tempDir);
                if (!Directory.Exists(schemaDir))
                {
                    Directory.CreateDirectory(schemaDir);
                }

                var (schemaDirSafe, schemaDirError) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, schemaDir);
                if (!schemaDirSafe || schemaDirError != null)
                {
                    cleanupStaging = true;
                    return (false, null, new[] { schemaDirError! });
                }

                foreach (var file in changedFiles)
                {
                    var embeddedBytes = EmbeddedResources.GetSchemaBytes(file.Name, expectedVersion)
                        ?? throw new IOException($"Embedded schema '{file.Name}' is unavailable.");
                    var stagedPath = Path.Combine(tempDir, $"{file.Name}.xsd.new");
                    File.WriteAllBytes(stagedPath, embeddedBytes);
                }

                foreach (var file in changedFiles)
                {
                    var target = Path.Combine(schemaDir, $"{file.Name}.xsd");
                    var backup = Path.Combine(tempDir, $"{file.Name}.xsd.bak");
                    if (File.Exists(target))
                    {
                        File.Copy(target, backup, overwrite: true);
                    }
                }

                WriteRecoveryMarker(tempDir, "applying", changedFiles.Select(file =>
                    new SchemaSyncRecoveryItem(file.Name, File.Exists(Path.Combine(schemaDir, $"{file.Name}.xsd")))).ToList());

                foreach (var file in changedFiles)
                {
                    var target = Path.Combine(schemaDir, $"{file.Name}.xsd");
                    var backup = Path.Combine(tempDir, $"{file.Name}.xsd.bak");
                    var staged = Path.Combine(tempDir, $"{file.Name}.xsd.new");
                    var existed = File.Exists(target);
                    File.Move(staged, target, overwrite: true);
                    applied.Add((target, backup, existed));
                }

                var (verified, after, verifyDiagnostics) = Inspect(workspaceRoot, expectedVersion);
                if (!verified || after == null || !after.InSync)
                {
                    throw new IOException(verifyDiagnostics.Count > 0
                        ? string.Join(" | ", verifyDiagnostics.Select(diagnostic => diagnostic.Message))
                        : "Schema copies did not match embedded resources after synchronization.");
                }

                WriteRecoveryMarker(tempDir, "committed", changedFiles.Select(file =>
                    new SchemaSyncRecoveryItem(file.Name, File.Exists(Path.Combine(tempDir, $"{file.Name}.xsd.bak")))).ToList());

                cleanupStaging = true;
                return (true, new SchemaCopySyncResult(workspaceRoot, expectedVersion, changedFiles.Count, after), Array.Empty<Diagnostic>());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var rollbackErrors = new List<string>();
                foreach (var item in applied.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (item.Existed)
                        {
                            File.Copy(item.Backup, item.Target, overwrite: true);
                        }
                        else if (File.Exists(item.Target))
                        {
                            File.Delete(item.Target);
                        }
                    }
                    catch (Exception rollbackEx) when (rollbackEx is IOException or UnauthorizedAccessException)
                    {
                        rollbackErrors.Add(rollbackEx.Message);
                    }
                }

                var rollbackDetail = rollbackErrors.Count == 0
                    ? "All applied schema-copy changes were rolled back."
                    : $"Rollback also failed: {string.Join(" | ", rollbackErrors)}";
                cleanupStaging = rollbackErrors.Count == 0;
                return (false, null, new[]
                {
                    Diagnostic.Error(
                        rollbackErrors.Count == 0 ? DiagnosticCodes.CommitFailed : DiagnosticCodes.RecoveryFailed,
                        $"Schema synchronization failed: {ex.Message} {rollbackDetail}")
                });
            }
            finally
            {
                if (cleanupStaging && Directory.Exists(tempDir))
                {
                    PathSecurity.SafeDeleteCliTempEntry(workspaceRoot, tempDir);
                }
            }
        }
    }

    /// <summary>
    /// Recovers an interrupted schema-copy synchronization while the caller holds the workspace writer lock.
    /// An applying transaction is rolled back; a committed transaction is cleaned up.
    /// </summary>
    public static (bool Success, Diagnostic? Error) RecoverPendingSync(string workspaceRoot, string stagingDirectory)
    {
        if (!PathSecurity.IsSafeCliTempChild(workspaceRoot, stagingDirectory))
        {
            return (false, Diagnostic.Error(
                DiagnosticCodes.RecoveryFailed,
                $"Schema synchronization recovery path '{stagingDirectory}' is not a safe CLI-owned path."));
        }

        var markerPath = Path.Combine(stagingDirectory, "marker.xml");
        if (!File.Exists(markerPath))
        {
            return PathSecurity.SafeDeleteCliTempEntry(workspaceRoot, stagingDirectory)
                ? (true, null)
                : (false, Diagnostic.Error(DiagnosticCodes.RecoveryFailed, $"Cannot remove abandoned schema synchronization staging directory '{stagingDirectory}'."));
        }

        try
        {
            XDocument marker;
            using (var stream = File.OpenRead(markerPath))
            using (var reader = SecureXmlReaderFactory.CreateReader(stream))
            {
                marker = XDocument.Load(reader);
            }

            var root = marker.Root;
            var state = root?.Attribute("state")?.Value;
            if (root?.Name.LocalName != "schema-sync" ||
                (state != "applying" && state != "committed"))
            {
                return (false, Diagnostic.Error(DiagnosticCodes.RecoveryFailed, $"Schema synchronization recovery marker '{markerPath}' is invalid."));
            }

            var items = new List<SchemaSyncRecoveryItem>();
            foreach (var element in root.Elements("file"))
            {
                var name = element.Attribute("name")?.Value;
                if (string.IsNullOrWhiteSpace(name) || !EmbeddedResources.SchemaNames.Contains(name, StringComparer.Ordinal))
                {
                    return (false, Diagnostic.Error(DiagnosticCodes.RecoveryFailed, $"Schema synchronization recovery marker contains unknown schema '{name}'."));
                }

                if (!bool.TryParse(element.Attribute("existed")?.Value, out var existed))
                {
                    return (false, Diagnostic.Error(DiagnosticCodes.RecoveryFailed, $"Schema synchronization recovery marker has invalid existence state for '{name}'."));
                }
                items.Add(new SchemaSyncRecoveryItem(name, existed));
            }

            if (items.Count == 0 || items.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != items.Count)
            {
                return (false, Diagnostic.Error(DiagnosticCodes.RecoveryFailed, $"Schema synchronization recovery marker '{markerPath}' has an empty or duplicate file set."));
            }

            if (state == "applying")
            {
                var schemaDir = Path.Combine(workspaceRoot, "_schema");
                Directory.CreateDirectory(schemaDir);
                var (schemaDirSafe, schemaDirError) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, schemaDir);
                if (!schemaDirSafe || schemaDirError != null)
                {
                    return (false, schemaDirError ?? Diagnostic.Error(
                        DiagnosticCodes.RecoveryFailed,
                        "Schema synchronization recovery target directory is not secure.",
                        "_schema"));
                }

                foreach (var item in items.AsEnumerable().Reverse())
                {
                    var target = Path.Combine(schemaDir, $"{item.Name}.xsd");
                    var backup = Path.Combine(stagingDirectory, $"{item.Name}.xsd.bak");
                    var (targetSafe, targetError) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, target);
                    if (!targetSafe || targetError != null)
                    {
                        return (false, targetError ?? Diagnostic.Error(
                            DiagnosticCodes.RecoveryFailed,
                            $"Schema synchronization recovery target for '{item.Name}' is not secure."));
                    }

                    if (item.Existed)
                    {
                        if (!File.Exists(backup))
                        {
                            return (false, Diagnostic.Error(DiagnosticCodes.RecoveryFailed, $"Schema synchronization backup for '{item.Name}' is missing."));
                        }
                        var (backupSafe, backupError) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, backup);
                        if (!backupSafe || backupError != null)
                        {
                            return (false, backupError ?? Diagnostic.Error(
                                DiagnosticCodes.RecoveryFailed,
                                $"Schema synchronization backup for '{item.Name}' is not secure."));
                        }
                        File.Copy(backup, target, overwrite: true);
                    }
                    else if (File.Exists(target))
                    {
                        File.Delete(target);
                    }
                }
            }

            return PathSecurity.SafeDeleteCliTempEntry(workspaceRoot, stagingDirectory)
                ? (true, null)
                : (false, Diagnostic.Error(DiagnosticCodes.RecoveryFailed, $"Cannot clean schema synchronization staging directory '{stagingDirectory}'."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return (false, Diagnostic.Error(
                DiagnosticCodes.RecoveryFailed,
                $"Schema synchronization recovery failed for '{stagingDirectory}': {ex.Message}"));
        }
    }

    private static void WriteRecoveryMarker(string stagingDirectory, string state, IReadOnlyList<SchemaSyncRecoveryItem> files)
    {
        var marker = new XDocument(
            new XElement("schema-sync",
                new XAttribute("state", state),
                files.Select(file => new XElement("file",
                    new XAttribute("name", file.Name),
                    new XAttribute("existed", file.Existed ? "true" : "false")))));
        var markerPath = Path.Combine(stagingDirectory, "marker.xml");
        var temporaryMarkerPath = Path.Combine(stagingDirectory, "marker.xml.tmp");
        using (var stream = new FileStream(temporaryMarkerPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            marker.Save(stream, SaveOptions.DisableFormatting);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryMarkerPath, markerPath, overwrite: true);
    }

    private static List<Diagnostic> ValidateManagedDocumentVersions(string workspaceRoot, string expectedVersion)
    {
        var diagnostics = new List<Diagnostic>();
        var (enumerated, documents, enumerationDiagnostics) = WorkspaceDiscovery.EnumerateDocuments(workspaceRoot);
        if (!enumerated)
        {
            diagnostics.AddRange(enumerationDiagnostics);
            return diagnostics;
        }

        foreach (var document in documents)
        {
            try
            {
                using var stream = File.OpenRead(document.FullPath);
                using var reader = SecureXmlReaderFactory.CreateReader(stream);
                reader.MoveToContent();
                var actualVersion = reader.GetAttribute("schema_version");
                if (!string.Equals(actualVersion, expectedVersion, StringComparison.Ordinal))
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.UnsupportedVersion,
                        $"Managed document declares schema_version '{actualVersion ?? "(missing)"}', expected '{expectedVersion}'. Schema synchronization does not migrate managed documents.",
                        document.RelativePath));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
            {
                diagnostics.Add(Diagnostic.Error(
                    ex is XmlException ? DiagnosticCodes.XmlParseError : DiagnosticCodes.FilesystemError,
                    $"Cannot verify schema_version for managed document '{document.RelativePath}': {ex.Message}",
                    document.RelativePath));
            }
        }

        return diagnostics;
    }

    private sealed record SchemaSyncRecoveryItem(string Name, bool Existed);
}
