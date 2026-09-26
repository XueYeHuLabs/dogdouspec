using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class WorkspaceUnlockCliTests
{
    private string _tempDir = null!;
    private string _wsRoot = null!;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_UnlockCliTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var (_, wsRoot, _) = WorkspaceInitializer.Initialize(_tempDir, _tempDir);
        _wsRoot = wsRoot;
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
    public void WorkspaceUnlock_OnCleanWorkspace_Succeeds()
    {
        using var sw = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(sw);
            var exitCode = Program.Main(new[] { "workspace", "unlock", "--workspace-root", _tempDir, "--format", "human" });
            Assert.AreEqual(0, exitCode);

            var output = sw.ToString();
            Assert.IsTrue(output.Contains("Workspace unlocked and startup recovery completed"));
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [TestMethod]
    public void WorkspaceUnlock_OnActivelyHeldLock_RefusesWithoutForce()
    {
        var (acquired, wsLock, _) = WorkspaceLock.Acquire(_wsRoot);
        Assert.IsTrue(acquired);
        Assert.IsNotNull(wsLock);

        try
        {
            using var sw = new StringWriter();
            var originalErr = Console.Error;
            try
            {
                Console.SetError(sw);
                var exitCode = Program.Main(new[] { "workspace", "unlock", "--workspace-root", _tempDir, "--format", "human" });
                Assert.AreEqual(1, exitCode);

                var output = sw.ToString();
                Assert.IsTrue(output.Contains("Workspace writer lock is actively held"));
            }
            finally
            {
                Console.SetError(originalErr);
            }
        }
        finally
        {
            wsLock.Dispose();
        }
    }
}
