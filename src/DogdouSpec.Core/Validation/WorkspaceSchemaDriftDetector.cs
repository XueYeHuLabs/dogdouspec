using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Resources;
using DogdouSpec.Core.Security;

namespace DogdouSpec.Core.Validation;

/// <summary>
/// Detects drift between workspace readable schema copies under _schema and CLI authoritative embedded schemas.
/// </summary>
public static class WorkspaceSchemaDriftDetector
{
    public const string SchemaDriftDetected = "SCHEMA_DRIFT_DETECTED";
    public const string UnreadableSchemaCopy = "UNREADABLE_SCHEMA_COPY";
    public const int MaxSchemaFileSizeBytes = 1024 * 1024; // 1 MB bound

    /// <summary>
    /// Checks for drift in the workspace _schema directory against authoritative embedded schemas.
    /// Absent optional schema copies are valid; existing known schema copies must match embedded bytes exactly.
    /// Never parses or compiles local schema copies.
    /// </summary>
    public static IReadOnlyList<Diagnostic> DetectDrift(string workspaceRoot, string version = "1.0")
    {
        var diagnostics = new List<Diagnostic>();

        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return diagnostics;
        }

        if (!EmbeddedResources.IsVersionSupported(version))
        {
            diagnostics.Add(Diagnostic.Error(
                DiagnosticCodes.UnsupportedVersion,
                $"Schema version '{version}' is not supported. Supported versions: {string.Join(", ", EmbeddedResources.SupportedVersions)}."));
            return diagnostics;
        }

        var schemaDir = Path.Combine(workspaceRoot, "_schema");

        FileAttributes schemaDirectoryAttributes;
        try
        {
            schemaDirectoryAttributes = File.GetAttributes(schemaDir);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return diagnostics; // Optional readable copies are absent.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Diagnostic.Error(
                UnreadableSchemaCopy,
                $"Cannot inspect workspace schema directory '_schema': {ex.Message}. Embedded schemas remain authoritative.",
                "_schema"));
            return diagnostics;
        }

        if (!schemaDirectoryAttributes.HasFlag(FileAttributes.Directory))
        {
            diagnostics.Add(Diagnostic.Error(
                UnreadableSchemaCopy,
                "Workspace schema path '_schema' is not a directory. Embedded schemas remain authoritative.",
                "_schema"));
            return diagnostics;
        }

        // Verify containment and reparse points of the _schema directory
        var (isDirSafe, dirSafeError) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, schemaDir);
        if (!isDirSafe || dirSafeError != null)
        {
            diagnostics.Add(dirSafeError ?? Diagnostic.Error(
                DiagnosticCodes.PathEscapeDetected,
                "Workspace schema directory '_schema' is not secure or escapes workspace root.",
                "_schema"));
            return diagnostics;
        }

        var majorVersion = version.Split('.')[0];

        foreach (var schemaName in EmbeddedResources.SchemaNames)
        {
            var normalizedName = EmbeddedResources.NormalizeSchemaName(schemaName);
            var fileName = $"{normalizedName}.xsd";
            var relPath = $"_schema/{fileName}";
            var fullPath = Path.Combine(schemaDir, fileName);
            var resourceName = $"schemas.v{majorVersion}.{normalizedName}.xsd";

            try
            {
                var attributes = File.GetAttributes(fullPath);
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    diagnostics.Add(Diagnostic.Error(
                        UnreadableSchemaCopy,
                        $"Workspace schema copy '{relPath}' is a directory instead of a file; embedded authoritative schema is '{resourceName}'.",
                        relPath));
                    continue;
                }

                var (isSafe, safeError) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, fullPath);
                if (!isSafe || safeError != null)
                {
                    diagnostics.Add(safeError ?? Diagnostic.Error(
                        DiagnosticCodes.PathEscapeDetected,
                        $"Workspace schema copy '{relPath}' is not secure or escapes workspace root.",
                        relPath));
                    continue;
                }

                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > MaxSchemaFileSizeBytes)
                {
                    diagnostics.Add(Diagnostic.Error(
                        SchemaDriftDetected,
                        $"Workspace schema copy '{relPath}' exceeds maximum allowed size ({stream.Length} bytes > {MaxSchemaFileSizeBytes} bytes) and differs from embedded authoritative schema '{resourceName}'.",
                        relPath));
                    continue;
                }

                var embeddedBytes = EmbeddedResources.GetSchemaBytes(normalizedName, version);
                if (embeddedBytes == null)
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.SchemaNotFound,
                        $"Embedded authoritative schema '{resourceName}' not found for version '{version}'.",
                        relPath));
                    continue;
                }

                // Allocate and read only the trusted resource length, even if a local file grows.
                var matches = stream.Length == embeddedBytes.Length;
                if (matches)
                {
                    var localBytes = new byte[embeddedBytes.Length];
                    stream.ReadExactly(localBytes);
                    matches = stream.ReadByte() == -1 && localBytes.AsSpan().SequenceEqual(embeddedBytes);
                }
                if (!matches)
                {
                    diagnostics.Add(Diagnostic.Error(
                        SchemaDriftDetected,
                        $"Workspace schema copy '{relPath}' differs from embedded authoritative schema '{resourceName}'. Refresh the optional copy from 'dogdouspec schema show --name {normalizedName} --version {version}'; embedded schemas remain authoritative.",
                        relPath));
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Missing optional copies remain valid; inspection failures are not treated as absence.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(Diagnostic.Error(
                    UnreadableSchemaCopy,
                    $"Workspace schema copy '{relPath}' cannot be read ({ex.Message}) and differs or cannot be verified against embedded authoritative schema '{resourceName}'.",
                    relPath));
            }
        }

        return diagnostics;
    }
}
