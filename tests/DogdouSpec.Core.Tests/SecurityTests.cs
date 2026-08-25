using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Security;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class SecurityTests
{
    [TestMethod]
    [DataRow("../escape.xml")]
    [DataRow("..\\escape.xml")]
    [DataRow("sub/../../escape.xml")]
    [DataRow("./relative.xml")]
    [DataRow(".")]
    [DataRow("..")]
    public void ValidateRelativeDocumentPath_Traversal_IsRejected(string path)
    {
        var (isValid, _, error) = PathSecurity.ValidateRelativeDocumentPath(path);

        Assert.IsFalse(isValid);
        Assert.IsNotNull(error);
        Assert.AreEqual(DiagnosticCodes.PathTraversalDetected, error.Code);
    }

    [TestMethod]
    [DataRow("/absolute/path.xml")]
    [DataRow("\\absolute\\path.xml")]
    [DataRow("C:/root/path.xml")]
    [DataRow("C:\\root\\path.xml")]
    [DataRow("\\\\server\\share\\path.xml")]
    [DataRow("//server/share/path.xml")]
    public void ValidateRelativeDocumentPath_AbsoluteOrRooted_IsRejected(string path)
    {
        var (isValid, _, error) = PathSecurity.ValidateRelativeDocumentPath(path);

        Assert.IsFalse(isValid);
        Assert.IsNotNull(error);
        Assert.AreEqual(DiagnosticCodes.PathEscapeDetected, error.Code);
    }

    [TestMethod]
    [DataRow("file.xml:stream")]
    [DataRow("sub/file.xml:$DATA")]
    public void ValidateRelativeDocumentPath_AlternateDataStream_IsRejected(string path)
    {
        var (isValid, _, error) = PathSecurity.ValidateRelativeDocumentPath(path);

        Assert.IsFalse(isValid);
        Assert.IsNotNull(error);
        Assert.AreEqual(DiagnosticCodes.InvalidPath, error.Code);
    }

    [TestMethod]
    [DataRow("CON")]
    [DataRow("PRN")]
    [DataRow("AUX")]
    [DataRow("NUL")]
    [DataRow("COM1")]
    [DataRow("LPT1")]
    public void ValidateRelativeDocumentPath_ReservedDeviceNames_IsRejected(string path)
    {
        var (isValid, _, error) = PathSecurity.ValidateRelativeDocumentPath(path);

        Assert.IsFalse(isValid);
        Assert.IsNotNull(error);
        Assert.AreEqual(DiagnosticCodes.InvalidPath, error.Code);
    }

    [TestMethod]
    [DataRow("knowledge.xml", "knowledge.xml")]
    [DataRow("backlog.xml", "backlog.xml")]
    [DataRow("20260823-xpath-core/spec.xml", "20260823-xpath-core/spec.xml")]
    [DataRow("20260823-xpath-core\\tasks.xml", "20260823-xpath-core/tasks.xml")]
    public void ValidateRelativeDocumentPath_ValidPaths_AreNormalized(string input, string expected)
    {
        var (isValid, normalized, error) = PathSecurity.ValidateRelativeDocumentPath(input);

        Assert.IsTrue(isValid);
        Assert.IsNull(error);
        Assert.AreEqual(expected, normalized);
    }

    [TestMethod]
    [DataRow("spec.xml")]
    [DataRow("tasks.xml")]
    [DataRow("_schema/spec.xsd")]
    [DataRow("_schema/knowledge.xsd")]
    [DataRow("_skill/README.md")]
    [DataRow("arbitrary-folder/spec.xml")]
    [DataRow("invalid-iter/tasks.xml")]
    [DataRow("20260823-xpath-core/sub/spec.xml")]
    [DataRow("20260823-xpath-core/requests.xml")]
    [DataRow("requests.xml")]
    [DataRow("template.xml")]
    [DataRow("notes.txt")]
    public void ValidateRelativeDocumentPath_NonManagedDocuments_AreRejected(string input)
    {
        var (isValid, _, error) = PathSecurity.ValidateRelativeDocumentPath(input);

        Assert.IsFalse(isValid, $"Non-managed document reference '{input}' should be rejected");
        Assert.IsNotNull(error);
        Assert.AreEqual(DiagnosticCodes.InvalidArgument, error.Code);
    }

    [TestMethod]
    [DataRow("20260823-xpath-core")]
    [DataRow("20260823-a")]
    [DataRow("20260823-feature-1")]
    [DataRow("20260823-9")]
    [DataRow("20260823-task-update-helper")]
    [DataRow("20260825T143000Z-feat")]
    [DataRow("20260825T000000Z-a")]
    [DataRow("20260825T235959Z-feature-1")]
    public void ValidateIterationId_ValidIds_AreAccepted(string iterationId)
    {
        var (isValid, normalized, error) = PathSecurity.ValidateIterationId(iterationId);

        Assert.IsTrue(isValid, $"Iteration ID '{iterationId}' should be valid");
        Assert.IsNull(error);
        Assert.AreEqual(iterationId, normalized);
    }

    [TestMethod]
    [DataRow(".")]
    [DataRow("..")]
    [DataRow("../20260823-escape")]
    [DataRow("..\\20260823-escape")]
    [DataRow("20260823-name/..")]
    [DataRow("20260823-name/../escape")]
    public void ValidateIterationId_Traversal_IsRejected(string iterationId)
    {
        var (isValid, _, error) = PathSecurity.ValidateIterationId(iterationId);

        Assert.IsFalse(isValid);
        Assert.IsNotNull(error);
        Assert.IsTrue(
            error.Code == DiagnosticCodes.PathTraversalDetected || error.Code == DiagnosticCodes.PathEscapeDetected,
            $"Expected PathTraversalDetected or PathEscapeDetected, got {error.Code}");
    }

    [TestMethod]
    [DataRow("/20260823-rooted")]
    [DataRow("\\20260823-rooted")]
    [DataRow("C:/20260823-rooted")]
    [DataRow("C:\\20260823-rooted")]
    [DataRow("20260823-name/sub")]
    [DataRow("20260823-name\\sub")]
    [DataRow("20260823-name:stream")]
    public void ValidateIterationId_SeparatorsAndAds_AreRejected(string iterationId)
    {
        var (isValid, _, error) = PathSecurity.ValidateIterationId(iterationId);

        Assert.IsFalse(isValid);
        Assert.IsNotNull(error);
        Assert.AreEqual(DiagnosticCodes.PathEscapeDetected, error.Code);
    }

    [TestMethod]
    [DataRow("20260823-XPath")]
    [DataRow("20260823-FEATURE")]
    [DataRow("20260823-MixedCase")]
    [DataRow("20260825t143000z-lower")]
    [DataRow("20260825T143000Z-UPPER")]
    public void ValidateIterationId_InvalidCasing_IsRejected(string iterationId)
    {
        var (isValid, _, error) = PathSecurity.ValidateIterationId(iterationId);

        Assert.IsFalse(isValid);
        Assert.IsNotNull(error);
        Assert.AreEqual(DiagnosticCodes.InvalidArgument, error.Code);
    }

    [TestMethod]
    [DataRow("invalid")]
    [DataRow("2026082-short")]
    [DataRow("202608233-long")]
    [DataRow("20260823_underscore")]
    [DataRow("20260823-")]
    [DataRow("20260823--double-hyphen")]
    [DataRow("20260823-ending-hyphen-")]
    [DataRow("20260825T14300Z-short")]
    [DataRow("20260825T1430000Z-long")]
    [DataRow("20260825T143000-missing-z")]
    [DataRow("20260825T143000Z-")]
    [DataRow("20260825T143000Z--double-hyphen")]
    [DataRow("20260825T143000Z-ending-hyphen-")]
    [DataRow("")]
    [DataRow("   ")]
    public void ValidateIterationId_InvalidGrammar_IsRejected(string iterationId)
    {
        var (isValid, _, error) = PathSecurity.ValidateIterationId(iterationId);

        Assert.IsFalse(isValid);
        Assert.IsNotNull(error);
        Assert.AreEqual(DiagnosticCodes.InvalidArgument, error.Code);
    }

    [TestMethod]
    public void CheckContainmentAndReparsePoints_SafeContainment_ReturnsSafe()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_Safe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var targetDir = Path.Combine(tempDir, "20260823-safe");
            Directory.CreateDirectory(targetDir);
            var targetFile = Path.Combine(targetDir, "spec.xml");
            File.WriteAllText(targetFile, "<test/>");

            var (isSafe, error) = PathSecurity.CheckContainmentAndReparsePoints(tempDir, targetFile);

            Assert.IsTrue(isSafe);
            Assert.IsNull(error);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [TestMethod]
    public void CheckContainmentAndReparsePoints_ReparsePointEscape_FailsClosed()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), "DogdouSpec_ReparseTest_" + Guid.NewGuid().ToString("N"));
        var outsideDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_Outside_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempWorkspace);
        Directory.CreateDirectory(outsideDir);

        try
        {
            var junctionPath = Path.Combine(tempWorkspace, "20260823-junction");

            // Create directory junction using mklink /J on Windows
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junctionPath}\" \"{outsideDir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit();

            if (!Directory.Exists(junctionPath))
            {
                Assert.Inconclusive("Directory junction creation is not supported or not permitted in this environment.");
            }

            var (isSafe, error) = PathSecurity.CheckContainmentAndReparsePoints(tempWorkspace, junctionPath);

            Assert.IsFalse(isSafe, "Reparse point escaping workspace root must be rejected");
            Assert.IsNotNull(error);
            Assert.AreEqual(DiagnosticCodes.PathEscapeDetected, error.Code);
        }
        finally
        {
            if (Directory.Exists(tempWorkspace))
            {
                try { Directory.Delete(tempWorkspace, true); } catch { }
            }
            if (Directory.Exists(outsideDir))
            {
                try { Directory.Delete(outsideDir, true); } catch { }
            }
        }
    }

    [TestMethod]
    public void VerifyWorkspaceDirectorySecurity_EscapingReparsePoint_FailsClosed()
    {
        var projectDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_Proj_" + Guid.NewGuid().ToString("N"));
        var outsideDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_Out_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectDir);
        Directory.CreateDirectory(outsideDir);

        try
        {
            var dogdouJunction = Path.Combine(projectDir, ".dogdouspec");

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

            var (isSafe, error) = PathSecurity.VerifyWorkspaceDirectorySecurity(dogdouJunction, projectDir);

            Assert.IsFalse(isSafe, "Escaping .dogdouspec reparse point must be rejected");
            Assert.IsNotNull(error);
            Assert.AreEqual(DiagnosticCodes.PathEscapeDetected, error.Code);
        }
        finally
        {
            if (Directory.Exists(projectDir))
            {
                try { Directory.Delete(projectDir, true); } catch { }
            }
            if (Directory.Exists(outsideDir))
            {
                try { Directory.Delete(outsideDir, true); } catch { }
            }
        }
    }
}
