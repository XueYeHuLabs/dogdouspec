using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DogdouSpec.Core.Tests;

[TestClass]
public class TaskCoverageVerifierTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null &&
               !File.Exists(Path.Combine(dir, "DogdouSpec.slnx")) &&
               !File.Exists(Path.Combine(dir, "DogdouSpec.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("Could not find repository root.");
    }

    private static string CreateWorkspaceCopy()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_CoverageTests_" + Guid.NewGuid().ToString("N"));
        var destDir = Path.Combine(tempDir, ".dogdouspec");
        Directory.CreateDirectory(tempDir);
        var srcDemo = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");
        CopyDirectory(srcDemo, destDir);
        return destDir;
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), true);
        }
        foreach (var sub in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(sub, Path.Combine(targetDir, Path.GetFileName(sub)));
        }
    }

    private static readonly string[] SampleCriteria = ["crit-1", "crit-2"];
    private static readonly string[] ExpectedUncovered = ["crit-2"];

    [TestMethod]
    public void TaskCoverageVerifier_BuildExpectedCoversFragment_FormatsExpectedXml()
    {
        var fragment = TaskCoverageVerifier.BuildExpectedCoversFragment(SampleCriteria);
        Assert.IsTrue(fragment.Contains("<covers>"));
        Assert.IsTrue(fragment.Contains("</covers>"));
        Assert.IsTrue(fragment.Contains("<ref scope=\"document\" target=\"crit-1\" relation=\"covers\"/>"));
        Assert.IsTrue(fragment.Contains("<ref scope=\"document\" target=\"crit-2\" relation=\"covers\"/>"));
    }

    [TestMethod]
    public void TaskCoverageVerifier_GetUncoveredCriteria_AllCoveredReturnsTrue()
    {
        var taskXml = """
<task id="task-1" status="in-progress">
  <acceptance>
    <criterion id="crit-1" status="pending"/>
    <criterion id="crit-2" status="pending"/>
  </acceptance>
  <records>
    <record id="rec-1" kind="verification">
      <covers>
        <ref scope="document" target="crit-1" relation="covers"/>
        <ref scope="document" target="crit-2" relation="covers"/>
      </covers>
    </record>
  </records>
</task>
""";
        var taskElem = XElement.Parse(taskXml);
        var (isCovered, uncovered) = TaskCoverageVerifier.GetUncoveredCriteria(taskElem);
        Assert.IsTrue(isCovered);
        Assert.AreEqual(0, uncovered.Count);
    }

    [TestMethod]
    public void TaskCoverageVerifier_GetUncoveredCriteria_MissingCriterionIdentified()
    {
        var taskXml = """
<task id="task-1" status="in-progress">
  <acceptance>
    <criterion id="crit-1" status="pending"/>
    <criterion id="crit-2" status="pending"/>
  </acceptance>
  <records>
    <record id="rec-1" kind="verification">
      <covers>
        <ref scope="document" target="crit-1" relation="covers"/>
      </covers>
    </record>
  </records>
</task>
""";
        var taskElem = XElement.Parse(taskXml);
        var (isCovered, uncovered) = TaskCoverageVerifier.GetUncoveredCriteria(taskElem);
        Assert.IsFalse(isCovered);
        CollectionAssert.AreEqual(ExpectedUncovered, uncovered);
    }

    [TestMethod]
    public void TaskCoverageVerifier_ValidateVerifyTransitionCoverage_FailsWithDiagnosticAndFragment()
    {
        var taskXml = """
<task id="task-1" status="in-progress">
  <acceptance>
    <criterion id="crit-1" status="pending"/>
  </acceptance>
  <records>
  </records>
</task>
""";
        var taskElem = XElement.Parse(taskXml);
        var (success, diag) = TaskCoverageVerifier.ValidateVerifyTransitionCoverage(taskElem, null, "tasks.xml");
        Assert.IsFalse(success);
        Assert.IsNotNull(diag);
        Assert.AreEqual(DiagnosticCodes.TaskCriterionNotCovered, diag.Code);
        Assert.IsTrue(diag.Message.Contains("crit-1"));
        Assert.IsTrue(diag.Message.Contains("<covers>"));
        Assert.IsTrue(diag.Message.Contains("<ref scope=\"document\" target=\"crit-1\" relation=\"covers\"/>"));
    }

    [TestMethod]
    public void TaskUpdate_VerifyTransitionWithoutCovers_RejectsFailFastWithDiagnostic()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";
        var taskId = "20260823-task-xpath-projection";
        var tasksPath = Path.Combine(workspace, iterId, "tasks.xml");
        var beforeBytes = File.ReadAllBytes(tasksPath);

        // Try transition="verify" without any covers in record
        var verifyNoCoversXml = """
<task-update
  id="20260823T052000Z-update-verify-failfast"
  transition="verify"
  actor="codex"
  occurred_at="2026-08-23T05:20:00Z">
  <records>
    <record
      id="20260823T052000Z-record-verify-failfast"
      kind="verification"
      status="informational"
      created_at="2026-08-23T05:20:00Z"
      actor="codex">
      <summary>Verification attempt missing covers.</summary>
    </record>
  </records>
</task-update>
""";

        var (success, env, diags) = TaskUpdater.Update(
            workspace,
            iterId,
            taskId,
            expectedRevision: 9,
            requestXml: verifyNoCoversXml);

        Assert.IsFalse(success);
        Assert.IsNull(env);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.TaskCriterionNotCovered &&
                                     d.Message.Contains("Expected covers fragment") &&
                                     d.Message.Contains("<covers>")));

        // Verify document was NOT modified
        var afterBytes = File.ReadAllBytes(tasksPath);
        CollectionAssert.AreEqual(beforeBytes, afterBytes);
    }

    [TestMethod]
    public void TaskUpdate_VerifyTransitionWithCovers_Succeeds()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";
        var taskId = "20260823-task-xpath-projection";

        var verifyWithCoversXml = """
<task-update
  id="20260823T052000Z-update-verify-ok"
  transition="verify"
  actor="codex"
  occurred_at="2026-08-23T05:20:00Z">
  <records>
    <record
      id="20260823T052000Z-record-verify-ok"
      kind="verification"
      status="informational"
      created_at="2026-08-23T05:20:00Z"
      actor="codex">
      <summary>Verified implementation with full criteria coverage.</summary>
      <covers>
        <ref scope="document" target="20260823-taskaccept-filter-members" relation="covers"/>
        <ref scope="document" target="20260823-taskaccept-filterout-members" relation="covers"/>
        <ref scope="document" target="20260823-taskaccept-filter-composition" relation="covers"/>
        <ref scope="document" target="20260823-taskaccept-result-limit" relation="covers"/>
      </covers>
    </record>
  </records>
</task-update>
""";

        var (success, env, diags) = TaskUpdater.Update(
            workspace,
            iterId,
            taskId,
            expectedRevision: 9,
            requestXml: verifyWithCoversXml);

        Assert.IsTrue(success, string.Join("; ", diags.Select(d => d.Message)));
        Assert.IsNotNull(env);
    }
}
