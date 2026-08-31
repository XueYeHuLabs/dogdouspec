using System.Diagnostics;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Transactions;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class LockMetadataAndDiagnosticTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_LockMetaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [TestMethod]
    public void LockMetadata_SerializationRoundTrip_PreservesFields()
    {
        var meta = new LockMetadata(
            Pid: 12345,
            ProcessName: "dogdouspec",
            CommandLine: "dogdouspec task update --task 20260831-01",
            AcquiredAtUtc: DateTimeOffset.UtcNow,
            MachineName: "TEST-HOST");

        var json = meta.ToJsonString();
        var parsed = LockMetadata.FromJsonString(json);

        Assert.IsNotNull(parsed);
        Assert.AreEqual(12345, parsed.Pid);
        Assert.AreEqual("dogdouspec", parsed.ProcessName);
        Assert.AreEqual("dogdouspec task update --task 20260831-01", parsed.CommandLine);
        Assert.AreEqual("TEST-HOST", parsed.MachineName);
    }

    [TestMethod]
    public void LockMetadata_LongCommandLine_TruncatesTo200CharsInJson()
    {
        var longCmd = "dogdouspec task update --task 20260831-01 --title " + new string('A', 250);
        var meta = new LockMetadata(
            Pid: 12345,
            ProcessName: "dogdouspec",
            CommandLine: longCmd,
            AcquiredAtUtc: DateTimeOffset.UtcNow,
            MachineName: "TEST-HOST");

        var json = meta.ToJsonString();
        var parsed = LockMetadata.FromJsonString(json);

        Assert.IsNotNull(parsed);
        Assert.AreEqual(200, parsed.CommandLine.Length);
        Assert.IsTrue(parsed.CommandLine.EndsWith("...", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WorkspaceLock_RecordsMetadata_AndReportsConflictDetails()
    {
        var (acquired, wsLock, error) = WorkspaceLock.Acquire(_tempDir);
        Assert.IsTrue(acquired);
        Assert.IsNotNull(wsLock);
        Assert.IsNull(error);

        try
        {
            var (isHeld, metadata, checkErr) = WorkspaceLock.CheckLockStatus(_tempDir);
            Assert.IsTrue(isHeld);
            Assert.IsNotNull(metadata);
            Assert.IsNull(checkErr);
            Assert.AreEqual(Environment.ProcessId, metadata.Pid);

            // Attempting to acquire again should fail with LOCK_CONFLICT and include PID in message
            var (secondAcquired, secondLock, conflictError) = WorkspaceLock.Acquire(_tempDir, timeoutMs: 0);
            Assert.IsFalse(secondAcquired);
            Assert.IsNull(secondLock);
            Assert.IsNotNull(conflictError);
            Assert.AreEqual(DiagnosticCodes.LockConflict, conflictError.Code);
            Assert.IsTrue(conflictError.Message.Contains(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        finally
        {
            wsLock.Dispose();
        }

        var (afterDisposeHeld, afterDisposeMeta, afterDisposeErr) = WorkspaceLock.CheckLockStatus(_tempDir);
        Assert.IsFalse(afterDisposeHeld);
        Assert.IsNull(afterDisposeErr);
    }

    [TestMethod]
    public void WorkspaceLock_ReleaseStaleLock_SafelyHandlesActiveAndStaleLocks()
    {
        // 1. Clean workspace
        var (cleanOk, _, cleanErr) = WorkspaceLock.ReleaseStaleLock(_tempDir, force: false);
        Assert.IsTrue(cleanOk);
        Assert.IsNull(cleanErr);

        // 2. Active lock held by current process
        var (acquired, wsLock, _) = WorkspaceLock.Acquire(_tempDir);
        Assert.IsTrue(acquired);
        Assert.IsNotNull(wsLock);

        try
        {
            // Without force on actively held lock, should fail with LOCK_CONFLICT
            var (unforcedOk, conflictMeta, unforcedErr) = WorkspaceLock.ReleaseStaleLock(_tempDir, force: false);
            Assert.IsFalse(unforcedOk);
            Assert.IsNotNull(conflictMeta);
            Assert.IsNotNull(unforcedErr);
            Assert.AreEqual(DiagnosticCodes.LockConflict, unforcedErr.Code);
        }
        finally
        {
            wsLock.Dispose();
        }

        // 3. Stale lock file left on disk (e.g. from a terminated process)
        var lockFilePath = Path.Combine(_tempDir, "_tmp", "writer.lock");
        Directory.CreateDirectory(Path.Combine(_tempDir, "_tmp"));
        File.WriteAllText(lockFilePath, "stale-metadata");
        Assert.IsTrue(File.Exists(lockFilePath));

        var (staleOk, _, staleErr) = WorkspaceLock.ReleaseStaleLock(_tempDir, force: false);
        Assert.IsTrue(staleOk);
        Assert.IsNull(staleErr);
        Assert.IsFalse(File.Exists(lockFilePath));
    }
}
