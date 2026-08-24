using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class DiscoveryTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_DiscoveryTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [TestMethod]
    public void FindWorkspaceRoot_FromProjectRoot_FindsChildDogdou()
    {
        var dogdouDir = Path.Combine(_tempDir, ".dogdouspec");
        Directory.CreateDirectory(dogdouDir);

        var (success, root, error) = WorkspaceDiscovery.FindWorkspaceRoot(null, _tempDir);

        Assert.IsTrue(success);
        Assert.IsNull(error);
        Assert.AreEqual(dogdouDir.Replace('\\', '/'), root);
    }

    [TestMethod]
    public void FindWorkspaceRoot_FromInsideDogdou_FindsWorkspace()
    {
        var dogdouDir = Path.Combine(_tempDir, ".dogdouspec");
        Directory.CreateDirectory(dogdouDir);

        var (success, root, error) = WorkspaceDiscovery.FindWorkspaceRoot(null, dogdouDir);

        Assert.IsTrue(success);
        Assert.IsNull(error);
        Assert.AreEqual(dogdouDir.Replace('\\', '/'), root);
    }

    [TestMethod]
    public void FindWorkspaceRoot_FromDeeplyNestedDirectory_FindsAncestorDogdou()
    {
        var dogdouDir = Path.Combine(_tempDir, ".dogdouspec");
        var nestedDir = Path.Combine(dogdouDir, "20260823-test", "sub1", "sub2");
        Directory.CreateDirectory(nestedDir);

        var (success, root, error) = WorkspaceDiscovery.FindWorkspaceRoot(null, nestedDir);

        Assert.IsTrue(success);
        Assert.IsNull(error);
        Assert.AreEqual(dogdouDir.Replace('\\', '/'), root);
    }

    [TestMethod]
    public void FindWorkspaceRoot_NearestAncestorBehavior_SelectsClosestWorkspace()
    {
        // Outer workspace
        var outerDogdou = Path.Combine(_tempDir, ".dogdouspec");
        Directory.CreateDirectory(outerDogdou);

        // Inner nested project with its own .dogdouspec
        var innerProject = Path.Combine(_tempDir, "subproject");
        var innerDogdou = Path.Combine(innerProject, ".dogdouspec");
        var deepDir = Path.Combine(innerDogdou, "20260823-sub", "nested");
        Directory.CreateDirectory(deepDir);

        var (success, root, error) = WorkspaceDiscovery.FindWorkspaceRoot(null, deepDir);

        Assert.IsTrue(success);
        Assert.IsNull(error);
        // Must find the nearest inner workspace, not outer
        Assert.AreEqual(innerDogdou.Replace('\\', '/'), root);
    }

    [TestMethod]
    public void FindWorkspaceRoot_NoWorkspaceInAncestors_FailsWithWorkspaceNotFound()
    {
        var emptyDir = Path.Combine(_tempDir, "empty", "nested");
        Directory.CreateDirectory(emptyDir);

        var (success, root, error) = WorkspaceDiscovery.FindWorkspaceRoot(null, emptyDir);

        Assert.IsFalse(success);
        Assert.IsNotNull(error);
        Assert.AreEqual(DiagnosticCodes.WorkspaceNotFound, error.Code);
    }

    [TestMethod]
    public void FindWorkspaceRoot_ExplicitRootPointingToDogdouDirectory_Succeeds()
    {
        var dogdouDir = Path.Combine(_tempDir, ".dogdouspec");
        Directory.CreateDirectory(dogdouDir);

        var (success, root, error) = WorkspaceDiscovery.FindWorkspaceRoot(dogdouDir, @"C:\Unrelated");

        Assert.IsTrue(success);
        Assert.IsNull(error);
        Assert.AreEqual(dogdouDir.Replace('\\', '/'), root);
    }

    [TestMethod]
    public void FindWorkspaceRoot_ExplicitRootPointingToParentProject_Succeeds()
    {
        var dogdouDir = Path.Combine(_tempDir, ".dogdouspec");
        Directory.CreateDirectory(dogdouDir);

        var (success, root, error) = WorkspaceDiscovery.FindWorkspaceRoot(_tempDir, @"C:\Unrelated");

        Assert.IsTrue(success);
        Assert.IsNull(error);
        Assert.AreEqual(dogdouDir.Replace('\\', '/'), root);
    }

    [TestMethod]
    public void FindWorkspaceRoot_ExplicitRootNonExistent_Fails()
    {
        var nonExistent = Path.Combine(_tempDir, "does_not_exist");

        var (success, root, error) = WorkspaceDiscovery.FindWorkspaceRoot(nonExistent, _tempDir);

        Assert.IsFalse(success);
        Assert.IsNotNull(error);
        Assert.AreEqual(DiagnosticCodes.WorkspaceNotFound, error.Code);
    }

    [TestMethod]
    public void FindWorkspaceRoot_AncestorReparseEscape_FailsClosedWithEscapeDetected()
    {
        var outsideDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_OutDesc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);

        try
        {
            var dogdouJunction = Path.Combine(_tempDir, ".dogdouspec");

            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{dogdouJunction}\" \"{outsideDir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit();

            if (!Directory.Exists(dogdouJunction))
            {
                Assert.Inconclusive("Directory junction creation is not supported or not permitted in this environment.");
            }

            var (success, root, error) = WorkspaceDiscovery.FindWorkspaceRoot(null, _tempDir);

            Assert.IsFalse(success, "Ancestor discovery with escaping .dogdouspec reparse point must fail closed");
            Assert.IsNotNull(error);
            Assert.AreEqual(DiagnosticCodes.PathEscapeDetected, error.Code);
        }
        finally
        {
            if (Directory.Exists(outsideDir))
            {
                try { Directory.Delete(outsideDir, true); } catch { }
            }
        }
    }

    [TestMethod]
    public void FindWorkspaceRoot_ExplicitParentProjectReparseEscape_FailsClosedWithEscapeDetected()
    {
        var outsideDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_OutExpProj_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);

        try
        {
            var dogdouJunction = Path.Combine(_tempDir, ".dogdouspec");

            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{dogdouJunction}\" \"{outsideDir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit();

            if (!Directory.Exists(dogdouJunction))
            {
                Assert.Inconclusive("Directory junction creation is not supported or not permitted in this environment.");
            }

            var (success, root, error) = WorkspaceDiscovery.FindWorkspaceRoot(_tempDir, @"C:\Unrelated");

            Assert.IsFalse(success, "Explicit parent project with escaping .dogdouspec must fail closed");
            Assert.IsNotNull(error);
            Assert.AreEqual(DiagnosticCodes.PathEscapeDetected, error.Code);
        }
        finally
        {
            if (Directory.Exists(outsideDir))
            {
                try { Directory.Delete(outsideDir, true); } catch { }
            }
        }
    }

    [TestMethod]
    public void FindWorkspaceRoot_ExplicitDogdouPathReparseEscape_FailsClosedWithEscapeDetected()
    {
        var outsideDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_OutExpDogdou_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);

        try
        {
            var dogdouJunction = Path.Combine(_tempDir, ".dogdouspec");

            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{dogdouJunction}\" \"{outsideDir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit();

            if (!Directory.Exists(dogdouJunction))
            {
                Assert.Inconclusive("Directory junction creation is not supported or not permitted in this environment.");
            }

            var (success, root, error) = WorkspaceDiscovery.FindWorkspaceRoot(dogdouJunction, @"C:\Unrelated");

            Assert.IsFalse(success, "Explicit .dogdouspec path that is an escaping reparse point must fail closed");
            Assert.IsNotNull(error);
            Assert.AreEqual(DiagnosticCodes.PathEscapeDetected, error.Code);
        }
        finally
        {
            if (Directory.Exists(outsideDir))
            {
                try { Directory.Delete(outsideDir, true); } catch { }
            }
        }
    }
}
