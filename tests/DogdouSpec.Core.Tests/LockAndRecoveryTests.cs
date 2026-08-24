using System.Globalization;
using System.Text;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Iterations;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Time;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class LockAndRecoveryTests
{
    private static string RepoRoot = null!;
    private string _tempDir = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext context)
    {
        var current = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "DogdouSpec.slnx")) ||
                File.Exists(Path.Combine(current.FullName, "DogdouSpec.sln")))
            {
                RepoRoot = current.FullName;
                break;
            }
            current = current.Parent;
        }

        Assert.IsNotNull(RepoRoot, "Repository root could not be located.");
    }

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_LockRecTests_" + Guid.NewGuid().ToString("N"));
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

    private string CreateWorkspaceCopy()
    {
        var srcDemo = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");
        var destDir = Path.Combine(_tempDir, ".dogdouspec");
        CopyDirectory(srcDemo, destDir);
        return destDir;
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), true);
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(destinationDir, Path.GetFileName(dir)));
        }
    }

    [TestMethod]
    public void WorkspaceLock_ExclusiveAcquisition_BlocksConcurrentAcquisitionWithLockConflict()
    {
        var workspace = CreateWorkspaceCopy();

        var (firstAcquired, lock1, firstError) = WorkspaceLock.Acquire(workspace);
        Assert.IsTrue(firstAcquired, "First lock acquire must succeed");
        Assert.IsNotNull(lock1);
        Assert.IsNull(firstError);

        using (lock1)
        {
            var (secondAcquired, lock2, secondError) = WorkspaceLock.Acquire(workspace, timeoutMs: 50);
            Assert.IsFalse(secondAcquired, "Concurrent lock acquire must fail while lock is held");
            Assert.IsNull(lock2);
            Assert.IsNotNull(secondError);
            Assert.AreEqual(DiagnosticCodes.LockConflict, secondError.Code);
            Assert.AreEqual(4, DiagnosticsEnvelope.GetExitCodeForCode(secondError.Code));
        }

        // After lock1 disposal, new acquire succeeds
        var (thirdAcquired, lock3, thirdError) = WorkspaceLock.Acquire(workspace);
        Assert.IsTrue(thirdAcquired, "Acquire after disposal must succeed");
        Assert.IsNotNull(lock3);
        Assert.IsNull(thirdError);
        lock3.Dispose();
    }

    [TestMethod]
    public void TempChildSafety_PreservesUnrelatedUserFiles_InTempDir()
    {
        var workspace = CreateWorkspaceCopy();
        var tmpDir = Path.Combine(workspace, "_tmp");
        Directory.CreateDirectory(tmpDir);

        // User file in _tmp
        var userFile = Path.Combine(tmpDir, "my_custom_script.py");
        File.WriteAllText(userFile, "print('hello')", Encoding.UTF8);

        // User directory in _tmp
        var userDir = Path.Combine(tmpDir, "custom_user_folder");
        Directory.CreateDirectory(userDir);
        var userNestedFile = Path.Combine(userDir, "notes.txt");
        File.WriteAllText(userNestedFile, "important notes", Encoding.UTF8);

        // CLI-owned temp directory in _tmp
        var cliDir = Path.Combine(tmpDir, "create_20260823T120000Z-create-12345");
        Directory.CreateDirectory(cliDir);
        var cliFile = Path.Combine(cliDir, "marker.xml");
        File.WriteAllText(cliFile, "<create-marker id=\"test\" state=\"staged\"/>", Encoding.UTF8);

        // Run recovery
        var (success, error) = StartupRecovery.Run(workspace);

        Assert.IsTrue(success);
        Assert.IsNull(error);

        // CLI-owned directory must be cleaned up
        Assert.IsFalse(Directory.Exists(cliDir), "CLI-owned staging directory must be cleaned up");

        // Unrelated user files and folders MUST be preserved
        Assert.IsTrue(File.Exists(userFile), "Unrelated user file in _tmp must NOT be deleted");
        Assert.IsTrue(Directory.Exists(userDir), "Unrelated user directory in _tmp must NOT be deleted");
        Assert.IsTrue(File.Exists(userNestedFile), "Nested file in user directory must NOT be deleted");
    }

    [TestMethod]
    public void StaleRevision_RejectsMutationBeforeStaging()
    {
        var workspace = CreateWorkspaceCopy();
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var originalContent = File.ReadAllText(tasksPath);

        var ops = new[]
        {
            new TransactionDocumentOperation(
                "20260823-xpath-core/tasks.xml",
                originalContent.Replace("revision=\"9\"", "revision=\"10\""),
                ExpectedRevision: 8, // Stale! Actual revision is 9
                NewRevision: 10)
        };

        var (success, env, diags) = WorkspaceTransactionCommitter.Commit(
            workspace,
            "test commit",
            ops);

        Assert.IsFalse(success);
        Assert.IsNull(env);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.RevisionConflict));
        var diag = diags.First(d => d.Code == DiagnosticCodes.RevisionConflict);
        Assert.AreEqual(8, diag.ExpectedRevision);
        Assert.AreEqual(9, diag.ActualRevision);
        Assert.AreEqual(4, DiagnosticsEnvelope.GetExitCodeForCode(diag.Code));

        // Original file untouched
        Assert.AreEqual(originalContent, File.ReadAllText(tasksPath));
    }

    [TestMethod]
    public void MultiDocumentCommit_HappyPath_UpdatesRevisionsAtomically()
    {
        var workspace = CreateWorkspaceCopy();
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");

        var tasksContent = File.ReadAllText(tasksPath).Replace("revision=\"9\"", "revision=\"10\"");
        var specContent = File.ReadAllText(specPath).Replace("revision=\"4\"", "revision=\"5\"");

        var ops = new[]
        {
            new TransactionDocumentOperation("20260823-xpath-core/spec.xml", specContent, 4, 5),
            new TransactionDocumentOperation("20260823-xpath-core/tasks.xml", tasksContent, 9, 10)
        };

        var (success, env, diags) = WorkspaceTransactionCommitter.Commit(
            workspace,
            "multi update",
            ops);

        Assert.IsTrue(success, $"Commit failed: {string.Join("; ", diags.Select(d => d.Message))}");
        Assert.IsNotNull(env);
        Assert.AreEqual(2, env.Documents.Count);

        // Check revisions on disk
        Assert.IsTrue(File.ReadAllText(specPath).Contains("revision=\"5\""));
        Assert.IsTrue(File.ReadAllText(tasksPath).Contains("revision=\"10\""));

        // Validate whole workspace
        var val = SchemaValidator.Validate(workspace);
        Assert.IsTrue(val.IsValid);
    }

    [TestMethod]
    public void MultiDocumentCommit_FaultAfterFirstFile_RecoversToCompleteState()
    {
        var workspace = CreateWorkspaceCopy();
        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");

        var specContent = File.ReadAllText(specPath).Replace("revision=\"4\"", "revision=\"5\"");
        var tasksContent = File.ReadAllText(tasksPath).Replace("revision=\"9\"", "revision=\"10\"");

        var ops = new[]
        {
            new TransactionDocumentOperation("20260823-xpath-core/spec.xml", specContent, 4, 5),
            new TransactionDocumentOperation("20260823-xpath-core/tasks.xml", tasksContent, 9, 10)
        };

        var injector = new TestFaultInjector(FaultPhase.DuringMultiFileCommitAfterFirstFile);

        var (success, env, diags) = WorkspaceTransactionCommitter.Commit(
            workspace,
            "interrupted commit",
            ops,
            faultInjector: injector);

        Assert.IsFalse(success, "Commit must be interrupted by injected fault");
        Assert.IsNull(env);

        // At this crash point, spec.xml was updated to revision 5, but tasks.xml was not yet updated to 10
        Assert.IsTrue(File.ReadAllText(specPath).Contains("revision=\"5\""));
        Assert.IsTrue(File.ReadAllText(tasksPath).Contains("revision=\"9\""));

        // Both files on disk are complete, well-formed XML
        Assert.IsNotNull(XDocument.Load(specPath).Root);
        Assert.IsNotNull(XDocument.Load(tasksPath).Root);

        // Now run startup recovery (as the next write transaction would)
        var (recSuccess, recErr) = StartupRecovery.Run(workspace);
        Assert.IsTrue(recSuccess);
        Assert.IsNull(recErr);

        // After recovery, tasks.xml has been forward completed to revision 10
        Assert.IsTrue(File.ReadAllText(specPath).Contains("revision=\"5\""));
        Assert.IsTrue(File.ReadAllText(tasksPath).Contains("revision=\"10\""));

        // No leftover temp directories
        var tmpEntries = Directory.GetFileSystemEntries(Path.Combine(workspace, "_tmp"));
        Assert.AreEqual(0, tmpEntries.Length(entry => !entry.EndsWith("writer.lock", StringComparison.OrdinalIgnoreCase)));

        // Whole workspace is valid
        var val = SchemaValidator.Validate(workspace);
        Assert.IsTrue(val.IsValid, $"Workspace validation failed: {string.Join("; ", val.Diagnostics.Select(d => d.Message))}");
    }

    [TestMethod]
    public void FaultInjection_AllPhases_LiveFilesAlwaysCompleteOldOrNew_AndRecoveryConverges()
    {
        var phases = new[]
        {
            FaultPhase.BeforeStaging,
            FaultPhase.AfterStagingBeforeValidation,
            FaultPhase.AfterValidationBeforeCommitMarker,
            FaultPhase.AfterCommitMarkerBeforePublish,
            FaultPhase.DuringMultiFileCommitAfterFirstFile,
            FaultPhase.AfterPublishBeforeCleanup
        };

        foreach (var phase in phases)
        {
            var workspace = CreateWorkspaceCopy();
            var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
            var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");

            var specContent = File.ReadAllText(specPath).Replace("revision=\"4\"", "revision=\"5\"");
            var tasksContent = File.ReadAllText(tasksPath).Replace("revision=\"9\"", "revision=\"10\"");

            var ops = new[]
            {
                new TransactionDocumentOperation("20260823-xpath-core/spec.xml", specContent, 4, 5),
                new TransactionDocumentOperation("20260823-xpath-core/tasks.xml", tasksContent, 9, 10)
            };

            var injector = new TestFaultInjector(phase);

            var (success, env, _) = WorkspaceTransactionCommitter.Commit(
                workspace,
                $"interrupted_{phase}",
                ops,
                faultInjector: injector);

            Assert.IsFalse(success, $"Commit must fail at phase {phase}");
            Assert.IsNull(env);

            // BEFORE RECOVERY: Inspect live files on disk.
            // Target files MUST be complete, valid XML (never torn, truncated, or empty)
            XDocument specXDoc;
            using (var s = File.OpenRead(specPath))
            using (var r = SecureXmlReaderFactory.CreateReader(s))
            {
                specXDoc = XDocument.Load(r);
            }
            Assert.IsNotNull(specXDoc.Root);
            var specRev = int.Parse(specXDoc.Root.Attribute("revision")!.Value, CultureInfo.InvariantCulture);
            Assert.IsTrue(specRev == 4 || specRev == 5, $"Spec revision must be 4 or 5 at phase {phase}, got {specRev}");

            XDocument tasksXDoc;
            using (var s = File.OpenRead(tasksPath))
            using (var r = SecureXmlReaderFactory.CreateReader(s))
            {
                tasksXDoc = XDocument.Load(r);
            }
            Assert.IsNotNull(tasksXDoc.Root);
            var tasksRev = int.Parse(tasksXDoc.Root.Attribute("revision")!.Value, CultureInfo.InvariantCulture);
            Assert.IsTrue(tasksRev == 9 || tasksRev == 10, $"Tasks revision must be 9 or 10 at phase {phase}, got {tasksRev}");

            // RUN RECOVERY
            var (recSuccess, recErr) = StartupRecovery.Run(workspace);
            Assert.IsTrue(recSuccess, $"Recovery must succeed for phase {phase}: {recErr?.Message}");
            Assert.IsNull(recErr);

            // AFTER RECOVERY: Workspace must be valid and files must be complete
            var val = SchemaValidator.Validate(workspace);
            Assert.IsTrue(val.IsValid, $"Workspace validation failed after recovery from phase {phase}: {string.Join("; ", val.Diagnostics.Select(d => d.Message))}");

            // No leftover CLI temp entries in _tmp
            var tmpEntries = Directory.GetFileSystemEntries(Path.Combine(workspace, "_tmp"));
            Assert.AreEqual(0, tmpEntries.Length(entry => !entry.EndsWith("writer.lock", StringComparison.OrdinalIgnoreCase)));
        }
    }

    [TestMethod]
    public void Commit_MissingTargetDocument_FailsWithDocumentNotFoundExitCode2()
    {
        var workspace = CreateWorkspaceCopy();
        var ops = new[]
        {
            new TransactionDocumentOperation("20260899-nonexistent/tasks.xml", "<tasks revision=\"2\"/>", 1, 2)
        };

        var (success, env, diags) = WorkspaceTransactionCommitter.Commit(workspace, "test", ops);
        Assert.IsFalse(success);
        Assert.IsNull(env);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.DocumentNotFound));
        Assert.AreEqual(2, DiagnosticsEnvelope.GetExitCodeForDiagnostics(diags));
    }

    [TestMethod]
    public void Commit_MalformedExistingRevision_FailsWithXmlParseError()
    {
        var workspace = CreateWorkspaceCopy();
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var badContent = File.ReadAllText(tasksPath).Replace("revision=\"9\"", "revision=\"non-integer\"");
        File.WriteAllText(tasksPath, badContent);

        var ops = new[]
        {
            new TransactionDocumentOperation("20260823-xpath-core/tasks.xml", badContent.Replace("revision=\"non-integer\"", "revision=\"10\""), 9, 10)
        };

        var (success, env, diags) = WorkspaceTransactionCommitter.Commit(workspace, "test", ops);
        Assert.IsFalse(success);
        Assert.IsNull(env);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.XmlParseError));
    }

    [TestMethod]
    public void Commit_WrongNewRevision_NotExpectedPlusOne_FailsWithInvalidArgument()
    {
        var workspace = CreateWorkspaceCopy();
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var content = File.ReadAllText(tasksPath).Replace("revision=\"9\"", "revision=\"11\"");

        var ops = new[]
        {
            new TransactionDocumentOperation("20260823-xpath-core/tasks.xml", content, ExpectedRevision: 9, NewRevision: 11) // Skipped 10!
        };

        var (success, env, diags) = WorkspaceTransactionCommitter.Commit(workspace, "test", ops);
        Assert.IsFalse(success);
        Assert.IsNull(env);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.InvalidArgument));
        Assert.AreEqual(2, DiagnosticsEnvelope.GetExitCodeForDiagnostics(diags));
    }

    [TestMethod]
    public void Commit_ReplacementRevisionMismatch_FailsWithRevisionConflict()
    {
        var workspace = CreateWorkspaceCopy();
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var content = File.ReadAllText(tasksPath); // Root revision is 9

        var ops = new[]
        {
            new TransactionDocumentOperation("20260823-xpath-core/tasks.xml", content, ExpectedRevision: 9, NewRevision: 10) // Replacement XML still has revision 9
        };

        var (success, env, diags) = WorkspaceTransactionCommitter.Commit(workspace, "test", ops);
        Assert.IsFalse(success);
        Assert.IsNull(env);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.RevisionConflict));
        Assert.AreEqual(4, DiagnosticsEnvelope.GetExitCodeForDiagnostics(diags));
    }

    [TestMethod]
    public void Commit_DuplicateOperationTargets_FailsWithInvalidArgument()
    {
        var workspace = CreateWorkspaceCopy();
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var content = File.ReadAllText(tasksPath).Replace("revision=\"9\"", "revision=\"10\"");

        var ops = new[]
        {
            new TransactionDocumentOperation("20260823-xpath-core/tasks.xml", content, 9, 10),
            new TransactionDocumentOperation("20260823-xpath-core/tasks.xml", content, 9, 10)
        };

        var (success, env, diags) = WorkspaceTransactionCommitter.Commit(workspace, "test", ops);
        Assert.IsFalse(success);
        Assert.IsNull(env);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.InvalidArgument));
        Assert.AreEqual(2, DiagnosticsEnvelope.GetExitCodeForDiagnostics(diags));
    }

    [TestMethod]
    public void Commit_PathTraversalInOperation_FailsWithPathTraversalDetected()
    {
        var workspace = CreateWorkspaceCopy();
        var ops = new[]
        {
            new TransactionDocumentOperation("../outside.xml", "<test revision=\"2\"/>", 1, 2)
        };

        var (success, env, diags) = WorkspaceTransactionCommitter.Commit(workspace, "test", ops);
        Assert.IsFalse(success);
        Assert.IsNull(env);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.PathTraversalDetected));
        Assert.AreEqual(2, DiagnosticsEnvelope.GetExitCodeForDiagnostics(diags));
    }

    [TestMethod]
    public void Commit_ReparseEscapeInWorkspace_FailsWithPathEscapeDetected()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), "DogdouSpec_ReparseWs_" + Guid.NewGuid().ToString("N"));
        var outsideDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_OutsideWs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempWorkspace);
        Directory.CreateDirectory(outsideDir);

        try
        {
            var junctionPath = Path.Combine(tempWorkspace, "20260823-escaped-junction");

            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junctionPath}\" \"{outsideDir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit();

            if (!Directory.Exists(junctionPath))
            {
                // Environment does not support junctions without elevation
                return;
            }

            var ops = new[]
            {
                new TransactionDocumentOperation("20260823-escaped-junction/tasks.xml", "<tasks revision=\"2\"/>", 1, 2)
            };

            var (success, env, diags) = WorkspaceTransactionCommitter.Commit(tempWorkspace, "test", ops);
            Assert.IsFalse(success);
            Assert.IsNull(env);
            Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.PathEscapeDetected));
            Assert.AreEqual(2, DiagnosticsEnvelope.GetExitCodeForDiagnostics(diags));
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
    public void Commit_SchemaInvalidUntouchedWorkspace_FailsProspectiveValidation()
    {
        var workspace = CreateWorkspaceCopy();

        // Corrupt untouched backlog.xml
        var backlogPath = Path.Combine(workspace, "backlog.xml");
        File.WriteAllText(backlogPath, "<backlog invalid_schema=\"true\"><broken/></backlog>");

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var originalTasks = File.ReadAllText(tasksPath);
        var newTasks = originalTasks.Replace("revision=\"9\"", "revision=\"10\"");

        var ops = new[]
        {
            new TransactionDocumentOperation("20260823-xpath-core/tasks.xml", newTasks, 9, 10)
        };

        var (success, env, diags) = WorkspaceTransactionCommitter.Commit(workspace, "test", ops);
        Assert.IsFalse(success, "Commit must fail when untouched workspace documents are schema invalid");
        Assert.IsNull(env);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.SchemaValidationError));

        // Live tasks.xml must remain untouched
        Assert.AreEqual(originalTasks, File.ReadAllText(tasksPath));
    }

    [TestMethod]
    public void Recovery_Publishing_ConsumedStagedFile_TargetHasNewRevision_RecoversSuccessfully()
    {
        var workspace = CreateWorkspaceCopy();
        var tmpDir = Path.Combine(workspace, "_tmp");
        var txDir = Path.Combine(tmpDir, "tx_consumed_test");
        var stagedDir = Path.Combine(txDir, "staged");
        var backupDir = Path.Combine(txDir, "backup");
        Directory.CreateDirectory(stagedDir);
        Directory.CreateDirectory(backupDir);

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var newContent = File.ReadAllText(tasksPath).Replace("revision=\"9\"", "revision=\"10\"");
        // Target is already published to new revision 10
        File.WriteAllText(tasksPath, newContent);

        // Staged file was consumed/deleted (does not exist in stagedDir)
        var stagedFile = Path.Combine(stagedDir, "20260823-xpath-core_tasks.xml");
        var backupFile = Path.Combine(backupDir, "20260823-xpath-core_tasks.xml");

        var markerXml = $"""
<?xml version="1.0" encoding="utf-8"?>
<recovery-marker id="tx_consumed_test" state="publishing" created_at="2026-08-23T12:00:00Z">
  <operation type="replace" target="20260823-xpath-core/tasks.xml" staged="{stagedFile.Replace('\\', '/')}" backup="{backupFile.Replace('\\', '/')}" expected_revision="9" new_revision="10"/>
</recovery-marker>
""";
        File.WriteAllText(Path.Combine(txDir, "recovery.xml"), markerXml);

        var (recSuccess, recErr) = StartupRecovery.Run(workspace);
        Assert.IsTrue(recSuccess, $"Recovery must succeed when target has new revision: {recErr?.Message}");
        Assert.IsNull(recErr);

        // Staging directory cleaned up
        Assert.IsFalse(Directory.Exists(txDir));
    }

    [TestMethod]
    public void Recovery_Publishing_ConsumedStagedFile_TargetHasOldRevision_FailsClosedRecoveryFailed()
    {
        var workspace = CreateWorkspaceCopy();
        var tmpDir = Path.Combine(workspace, "_tmp");
        var txDir = Path.Combine(tmpDir, "tx_consumed_fail_test");
        var stagedDir = Path.Combine(txDir, "staged");
        var backupDir = Path.Combine(txDir, "backup");
        Directory.CreateDirectory(stagedDir);
        Directory.CreateDirectory(backupDir);

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        // Target is still at old revision 9
        Assert.IsTrue(File.ReadAllText(tasksPath).Contains("revision=\"9\""));

        // Staged file is missing/consumed without target having been updated!
        var stagedFile = Path.Combine(stagedDir, "20260823-xpath-core_tasks.xml");
        var backupFile = Path.Combine(backupDir, "20260823-xpath-core_tasks.xml");

        var markerXml = $"""
<?xml version="1.0" encoding="utf-8"?>
<recovery-marker id="tx_consumed_fail_test" state="publishing" created_at="2026-08-23T12:00:00Z">
  <operation type="replace" target="20260823-xpath-core/tasks.xml" staged="{stagedFile.Replace('\\', '/')}" backup="{backupFile.Replace('\\', '/')}" expected_revision="9" new_revision="10"/>
</recovery-marker>
""";
        File.WriteAllText(Path.Combine(txDir, "recovery.xml"), markerXml);

        var (recSuccess, recErr) = StartupRecovery.Run(workspace);
        Assert.IsFalse(recSuccess, "Recovery must fail closed when neither valid staged nor valid new target exists");
        Assert.IsNotNull(recErr);
        Assert.AreEqual(DiagnosticCodes.RecoveryFailed, recErr.Code);
        Assert.AreEqual(6, DiagnosticsEnvelope.GetExitCodeForCode(recErr.Code));
    }

    [TestMethod]
    public void Recovery_Prepared_ModifiedTarget_MissingBackup_FailsClosedRecoveryFailed()
    {
        var workspace = CreateWorkspaceCopy();
        var tmpDir = Path.Combine(workspace, "_tmp");
        var txDir = Path.Combine(tmpDir, "tx_prepared_fail_test");
        var stagedDir = Path.Combine(txDir, "staged");
        var backupDir = Path.Combine(txDir, "backup");
        Directory.CreateDirectory(stagedDir);
        Directory.CreateDirectory(backupDir);

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        // Target was modified to revision 10, but marker is still 'prepared'
        File.WriteAllText(tasksPath, File.ReadAllText(tasksPath).Replace("revision=\"9\"", "revision=\"10\""));

        var stagedFile = Path.Combine(stagedDir, "20260823-xpath-core_tasks.xml");
        var backupFile = Path.Combine(backupDir, "20260823-xpath-core_tasks.xml"); // Missing backup!

        var markerXml = $"""
<?xml version="1.0" encoding="utf-8"?>
<recovery-marker id="tx_prepared_fail_test" state="prepared" created_at="2026-08-23T12:00:00Z">
  <operation type="replace" target="20260823-xpath-core/tasks.xml" staged="{stagedFile.Replace('\\', '/')}" backup="{backupFile.Replace('\\', '/')}" expected_revision="9" new_revision="10"/>
</recovery-marker>
""";
        File.WriteAllText(Path.Combine(txDir, "recovery.xml"), markerXml);

        var (recSuccess, recErr) = StartupRecovery.Run(workspace);
        Assert.IsFalse(recSuccess, "Recovery must fail closed when prepared target was modified and backup is missing");
        Assert.IsNotNull(recErr);
        Assert.AreEqual(DiagnosticCodes.RecoveryFailed, recErr.Code);
        Assert.AreEqual(6, DiagnosticsEnvelope.GetExitCodeForCode(recErr.Code));
    }

    [TestMethod]
    public void Recovery_Create_TargetDirectoryTamperedMissingTasks_FailsClosedWithoutStreamingCopy()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260825-tampered-create";
        var liveIterDir = Path.Combine(workspace, iterId);
        Directory.CreateDirectory(liveIterDir);
        // spec.xml is written in live, but tasks.xml is missing!
        File.WriteAllText(Path.Combine(liveIterDir, "spec.xml"), """
<?xml version="1.0" encoding="utf-8"?>
<iteration id="20260825-tampered-create" schema_version="1.0" revision="1" kind="feature" status="draft" created_at="2026-08-25T00:00:00Z" updated_at="2026-08-25T00:00:00Z">
  <index><summary>Test</summary><term key="kind" value="feature"/><term key="iteration" value="20260825-tampered-create"/><term key="status" value="draft"/></index>
  <product><objective>Test</objective><deliverables><deliverable id="20260825-deliv-test"><index><summary>Test</summary><term key="kind" value="deliverable"/></index><description>Test</description></deliverable></deliverables><scope><included/><excluded/></scope><requirements><requirement id="20260825-req-test" status="proposed"><index><summary>Test</summary><term key="kind" value="requirement"/></index><statement>Test</statement><rationale>Test</rationale></requirement></requirements><acceptance><criterion id="20260825-crit-test" decision="pending">Test</criterion></acceptance></product><confirmations/>
</iteration>
""");

        var tmpDir = Path.Combine(workspace, "_tmp");
        var createDir = Path.Combine(tmpDir, "create_test_tamper");
        var stagedIterDir = Path.Combine(createDir, iterId);
        Directory.CreateDirectory(stagedIterDir);
        File.WriteAllText(Path.Combine(stagedIterDir, "spec.xml"), File.ReadAllText(Path.Combine(liveIterDir, "spec.xml")));
        File.WriteAllText(Path.Combine(stagedIterDir, "tasks.xml"), """
<?xml version="1.0" encoding="utf-8"?>
<tasks id="20260825-tasks-test" iteration="20260825-tampered-create" schema_version="1.0" revision="1"><index><summary>Test</summary><term key="iteration" value="20260825-tampered-create"/></index></tasks>
""");

        var markerXml = $"""
<?xml version="1.0" encoding="utf-8"?>
<create-marker id="test_tamper" iteration_id="{iterId}" state="publishing" created_at="2026-08-25T00:00:00Z"/>
""";
        File.WriteAllText(Path.Combine(createDir, "marker.xml"), markerXml);

        var (recSuccess, recErr) = StartupRecovery.Run(workspace);
        Assert.IsFalse(recSuccess, "Recovery must fail closed when live target directory is incomplete/tampered");
        Assert.IsNotNull(recErr);
        Assert.AreEqual(DiagnosticCodes.RecoveryFailed, recErr.Code);
        Assert.AreEqual(6, DiagnosticsEnvelope.GetExitCodeForCode(recErr.Code));

        // Live target directory MUST NOT have had tasks.xml stream-copied into it
        Assert.IsFalse(File.Exists(Path.Combine(liveIterDir, "tasks.xml")), "Must NOT stream-copy tasks.xml into live target directory");
    }

    [TestMethod]
    public void Recovery_Create_TargetAbsent_ValidStaged_DirectoryMovesForward()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260825-forward-create";
        var liveIterDir = Path.Combine(workspace, iterId);
        Assert.IsFalse(Directory.Exists(liveIterDir));

        var tmpDir = Path.Combine(workspace, "_tmp");
        var createDir = Path.Combine(tmpDir, "create_test_forward");
        var stagedIterDir = Path.Combine(createDir, iterId);
        Directory.CreateDirectory(stagedIterDir);

        File.WriteAllText(Path.Combine(stagedIterDir, "spec.xml"), """
<?xml version="1.0" encoding="utf-8"?>
<iteration id="20260825-forward-create" schema_version="1.0" revision="1" kind="feature" status="draft" created_at="2026-08-25T00:00:00Z" updated_at="2026-08-25T00:00:00Z">
  <index><summary>Test</summary><term key="kind" value="feature"/><term key="iteration" value="20260825-forward-create"/><term key="status" value="draft"/></index>
  <product><objective>Test</objective><deliverables><deliverable id="20260825-deliv-test"><index><summary>Test</summary><term key="kind" value="deliverable"/></index><description>Test</description></deliverable></deliverables><scope><included/><excluded/></scope><requirements><requirement id="20260825-req-test" status="proposed"><index><summary>Test</summary><term key="kind" value="requirement"/></index><statement>Test</statement><rationale>Test</rationale></requirement></requirements><acceptance><criterion id="20260825-crit-test" decision="pending">Test</criterion></acceptance></product><confirmations/>
</iteration>
""");
        File.WriteAllText(Path.Combine(stagedIterDir, "tasks.xml"), """
<?xml version="1.0" encoding="utf-8"?>
<tasks id="20260825-tasks-test" iteration="20260825-forward-create" schema_version="1.0" revision="1"><index><summary>Test</summary><term key="iteration" value="20260825-forward-create"/></index></tasks>
""");

        var markerXml = $"""
<?xml version="1.0" encoding="utf-8"?>
<create-marker id="test_forward" iteration_id="{iterId}" state="publishing" created_at="2026-08-25T00:00:00Z"/>
""";
        File.WriteAllText(Path.Combine(createDir, "marker.xml"), markerXml);

        var (recSuccess, recErr) = StartupRecovery.Run(workspace);
        Assert.IsTrue(recSuccess, $"Recovery must succeed via Directory.Move: {recErr?.Message}");
        Assert.IsNull(recErr);

        // Live iteration directory now exists and is valid
        Assert.IsTrue(Directory.Exists(liveIterDir));
        Assert.IsTrue(File.Exists(Path.Combine(liveIterDir, "spec.xml")));
        Assert.IsTrue(File.Exists(Path.Combine(liveIterDir, "tasks.xml")));

        // Staging directory cleaned up
        Assert.IsFalse(Directory.Exists(createDir));

        var val = SchemaValidator.Validate(workspace, iterationId: iterId);
        Assert.IsTrue(val.IsValid, $"Recovered iteration must be valid: {string.Join("; ", val.Diagnostics.Select(d => d.Message))}");
    }

    [TestMethod]
    public void RealDisposableWorkspace_SmokeTest_CreateListValidateMutate()
    {
        // 1. Initialize fresh workspace
        var wsDir = Path.Combine(_tempDir, "SmokeWorkspace", ".dogdouspec");
        var (initSuccess, initRoot, initErr) = WorkspaceInitializer.Initialize(wsDir, _tempDir);
        Assert.IsTrue(initSuccess, $"Init failed: {initErr?.Message}");

        // 2. Validate empty workspace
        var initVal = SchemaValidator.Validate(wsDir);
        Assert.IsTrue(initVal.IsValid);

        // 3. Create a feature iteration
        var iterId = "20260823-smoke-feature";
        var (createSuccess, createEnv, createDiags) = IterationCreator.Create(wsDir, iterId, "feature");
        Assert.IsTrue(createSuccess, $"Create failed: {string.Join("; ", createDiags.Select(d => d.Message))}");
        Assert.IsNotNull(createEnv);

        // 4. List iterations
        var (listSuccess, listResult, listDiags) = IterationLister.List(wsDir);
        Assert.IsTrue(listSuccess);
        Assert.IsNotNull(listResult);
        Assert.AreEqual(1, listResult.Iterations.Count);
        Assert.AreEqual(iterId, listResult.Iterations[0].Id);
        Assert.AreEqual(1, listResult.Iterations[0].SpecRevision);
        Assert.AreEqual(1, listResult.Iterations[0].TasksRevision);

        // 5. Validate workspace with iteration
        var valAfterCreate = SchemaValidator.Validate(wsDir);
        Assert.IsTrue(valAfterCreate.IsValid);

        // 6. Mutate spec.xml and tasks.xml atomically (revision 1 -> 2)
        var specPath = Path.Combine(wsDir, iterId, "spec.xml");
        var tasksPath = Path.Combine(wsDir, iterId, "tasks.xml");
        var newSpec = File.ReadAllText(specPath).Replace("revision=\"1\"", "revision=\"2\"");
        var newTasks = File.ReadAllText(tasksPath).Replace("revision=\"1\"", "revision=\"2\"");

        var ops = new[]
        {
            new TransactionDocumentOperation($"{iterId}/spec.xml", newSpec, 1, 2),
            new TransactionDocumentOperation($"{iterId}/tasks.xml", newTasks, 1, 2)
        };

        var (commitSuccess, commitEnv, commitDiags) = WorkspaceTransactionCommitter.Commit(wsDir, "smoke update", ops);
        Assert.IsTrue(commitSuccess, $"Commit failed: {string.Join("; ", commitDiags.Select(d => d.Message))}");
        Assert.IsNotNull(commitEnv);
        Assert.AreEqual(2, commitEnv.Documents.Count);

        // 7. Verify revisions and validate workspace
        Assert.IsTrue(File.ReadAllText(specPath).Contains("revision=\"2\""));
        Assert.IsTrue(File.ReadAllText(tasksPath).Contains("revision=\"2\""));

        var finalVal = SchemaValidator.Validate(wsDir);
        Assert.IsTrue(finalVal.IsValid);
    }

    [TestMethod]
    public void Recovery_TamperedMarker_FailsClosedWithExitCode6()
    {
        var workspace = CreateWorkspaceCopy();
        var tmpDir = Path.Combine(workspace, "_tmp");
        var txDir = Path.Combine(tmpDir, "tx_tampered_test");
        Directory.CreateDirectory(txDir);

        // Tampered recovery marker attempting path traversal outside workspace
        var tamperedXml = """
<?xml version="1.0" encoding="utf-8"?>
<recovery-marker id="tampered" state="prepared">
  <operation type="replace" target="../../outside.xml" staged="_tmp/tx_tampered_test/staged.xml" expected_revision="1" new_revision="2"/>
</recovery-marker>
""";
        File.WriteAllText(Path.Combine(txDir, "recovery.xml"), tamperedXml);

        var (recSuccess, recErr) = StartupRecovery.Run(workspace);

        Assert.IsFalse(recSuccess, "Tampered marker must cause recovery to fail closed");
        Assert.IsNotNull(recErr);
        Assert.AreEqual(DiagnosticCodes.RecoveryFailed, recErr.Code);
        Assert.AreEqual(6, DiagnosticsEnvelope.GetExitCodeForCode(recErr.Code));
    }
}

file static class Extensions
{
    public static int Length(this string[] array, Func<string, bool> predicate) =>
        array.Count(predicate);
}
