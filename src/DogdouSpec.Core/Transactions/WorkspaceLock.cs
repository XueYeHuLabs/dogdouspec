using System.Diagnostics;
using System.Text;
using DogdouSpec.Core.Diagnostics;

namespace DogdouSpec.Core.Transactions;

/// <summary>
/// Project-wide interprocess writer lock under CLI-owned workspace temp state (.dogdouspec/_tmp/writer.lock).
/// </summary>
public sealed class WorkspaceLock : IWorkspaceLock
{
    private readonly FileStream _lockStream;
    private bool _disposed;

    public string LockFilePath { get; }

    private WorkspaceLock(FileStream lockStream, string lockFilePath)
    {
        _lockStream = lockStream;
        LockFilePath = lockFilePath;
    }

    /// <summary>
    /// Acquires an exclusive writer lock for the given workspace.
    /// Returns (false, null, Diagnostic) with code LOCK_CONFLICT if the lock is held by another process.
    /// </summary>
    public static (bool Acquired, IWorkspaceLock? Lock, Diagnostic? Error) Acquire(
        string workspaceRoot,
        int timeoutMs = 0)
    {
        var tmpDir = Path.Combine(workspaceRoot, "_tmp");
        try
        {
            if (!Directory.Exists(tmpDir))
            {
                Directory.CreateDirectory(tmpDir);
            }
        }
        catch (Exception ex)
        {
            return (false, null, Diagnostic.Error(
                DiagnosticCodes.FilesystemError,
                $"Failed to create workspace temp directory at '{tmpDir}': {ex.Message}"));
        }

        var lockFilePath = Path.Combine(tmpDir, "writer.lock");
        var startTime = DateTime.UtcNow;

        while (true)
        {
            try
            {
                var fs = new FileStream(
                    lockFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.None);

                try
                {
                    fs.SetLength(0);
                    var meta = LockMetadata.CreateCurrent();
                    var metaBytes = Encoding.UTF8.GetBytes(meta.ToJsonString());
                    fs.Write(metaBytes, 0, metaBytes.Length);
                    fs.Flush();
                }
                catch
                {
                    // Ignore metadata write error; lock handle is still valid
                }

                return (true, new WorkspaceLock(fs, lockFilePath), null);
            }
            catch (IOException)
            {
                if (timeoutMs <= 0 || (DateTime.UtcNow - startTime).TotalMilliseconds >= timeoutMs)
                {
                    var meta = LockMetadata.TryReadFromFile(lockFilePath);
                    var detail = meta != null
                        ? " " + meta.FormatConflictDetails()
                        : " Another process is currently modifying the workspace.";

                    return (false, null, Diagnostic.Error(
                        DiagnosticCodes.LockConflict,
                        $"Cannot acquire workspace writer lock on '{lockFilePath}'.{detail}"));
                }

                Thread.Sleep(20);
            }
            catch (UnauthorizedAccessException)
            {
                return (false, null, Diagnostic.Error(
                    DiagnosticCodes.LockConflict,
                    $"Access denied when acquiring workspace writer lock on '{lockFilePath}'."));
            }
            catch (Exception ex)
            {
                return (false, null, Diagnostic.Error(
                    DiagnosticCodes.FilesystemError,
                    $"Unexpected error acquiring workspace writer lock on '{lockFilePath}': {ex.Message}"));
            }
        }
    }

    /// <summary>
    /// Checks whether the workspace writer lock is currently held by an active process.
    /// </summary>
    public static (bool IsHeld, LockMetadata? Metadata, Diagnostic? Error) CheckLockStatus(string workspaceRoot)
    {
        var lockFilePath = Path.Combine(workspaceRoot, "_tmp", "writer.lock");
        if (!File.Exists(lockFilePath))
        {
            return (false, null, null);
        }

        try
        {
            using var fs = new FileStream(
                lockFilePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.None);

            return (false, null, null);
        }
        catch (IOException)
        {
            var meta = LockMetadata.TryReadFromFile(lockFilePath);
            return (true, meta, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (false, null, Diagnostic.Error(
                DiagnosticCodes.LockConflict,
                $"Access denied when accessing lock file at '{lockFilePath}'."));
        }
        catch (Exception ex)
        {
            return (false, null, Diagnostic.Error(
                DiagnosticCodes.FilesystemError,
                $"Unexpected error checking lock file '{lockFilePath}': {ex.Message}"));
        }
    }

    /// <summary>
    /// Safely releases or removes a stale workspace writer lock.
    /// If force is false, verifies no active process owns the lock before removing it.
    /// If force is true, forcibly deletes the lock file even if held or conflicting.
    /// </summary>
    public static (bool Success, LockMetadata? ConflictMetadata, Diagnostic? Error) ReleaseStaleLock(
        string workspaceRoot,
        bool force = false)
    {
        var tmpDir = Path.Combine(workspaceRoot, "_tmp");
        var lockFilePath = Path.Combine(tmpDir, "writer.lock");

        if (!File.Exists(lockFilePath))
        {
            return (true, null, null);
        }

        // Try non-blocking acquire first to safely verify no active process owns the lock
        var (acquired, activeLock, _) = Acquire(workspaceRoot, timeoutMs: 0);
        if (acquired && activeLock != null)
        {
            activeLock.Dispose();
            try
            {
                if (File.Exists(lockFilePath))
                {
                    File.Delete(lockFilePath);
                }
                return (true, null, null);
            }
            catch (Exception ex)
            {
                return (false, null, Diagnostic.Error(
                    DiagnosticCodes.FilesystemError,
                    $"Failed to remove lock file '{lockFilePath}': {ex.Message}"));
            }
        }

        var meta = LockMetadata.TryReadFromFile(lockFilePath);

        if (!force)
        {
            var detail = meta != null ? $" {meta.FormatConflictDetails()}" : string.Empty;
            return (false, meta, Diagnostic.Error(
                DiagnosticCodes.LockConflict,
                $"Workspace writer lock is actively held.{detail} Use --force only if you are certain the process is dead or frozen."));
        }

        try
        {
            if (File.Exists(lockFilePath))
            {
                File.Delete(lockFilePath);
            }
            return (true, meta, null);
        }
        catch (Exception ex)
        {
            return (false, meta, Diagnostic.Error(
                DiagnosticCodes.FilesystemError,
                $"Failed to remove lock file '{lockFilePath}': {ex.Message}"));
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            try
            {
                _lockStream.SetLength(0);
                _lockStream.Flush();
            }
            catch
            {
                // Ignore errors on stream truncate
            }

            try
            {
                _lockStream.Dispose();
            }
            catch
            {
                // Ignore errors on stream disposal
            }
        }
    }
}
