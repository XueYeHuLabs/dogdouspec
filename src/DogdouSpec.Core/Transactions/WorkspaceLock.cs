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
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);

                return (true, new WorkspaceLock(fs, lockFilePath), null);
            }
            catch (IOException)
            {
                if (timeoutMs <= 0 || (DateTime.UtcNow - startTime).TotalMilliseconds >= timeoutMs)
                {
                    return (false, null, Diagnostic.Error(
                        DiagnosticCodes.LockConflict,
                        $"Cannot acquire workspace writer lock on '{lockFilePath}'. Another process is currently modifying the workspace."));
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

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
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
