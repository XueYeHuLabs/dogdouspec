using System;
using System.IO;
using System.Xml.Linq;
using DogdouSpec.Core.Tasks;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class TaskArchivalTests
{
    [TestMethod]
    public void TaskArchivalDescriptor_ParsesUnarchivedTaskCorrectly()
    {
        var taskXml = """
<task id="20260925-task-test" status="in-progress">
  <title>Active Task</title>
  <records>
    <record id="20260925T010000Z-rec-1" kind="discussion" status="informational">
      <summary>Record 1</summary>
    </record>
    <record id="20260925T020000Z-rec-2" kind="verification" status="informational">
      <summary>Record 2</summary>
    </record>
  </records>
</task>
""";
        var el = XElement.Parse(taskXml);
        var descriptor = TaskArchivalDescriptor.FromTaskElement(el);

        Assert.AreEqual("20260925-task-test", descriptor.TaskId);
        Assert.IsFalse(descriptor.IsArchived);
        Assert.AreEqual(2, descriptor.TotalCount);
        Assert.AreEqual(2, descriptor.InlineCount);
        Assert.AreEqual(0, descriptor.ArchivedCount);
        Assert.IsNull(descriptor.ArchivePath);
        Assert.IsNull(descriptor.ArchiveSha256);
    }

    [TestMethod]
    public void TaskArchivalDescriptor_ParsesArchivedTaskWithRetainedTerminalRecord()
    {
        var taskXml = """
<task id="20260925-task-done" status="done">
  <title>Done Task</title>
  <records total_count="10" archived="true" archive_path="20260925-iter/tasks.archive.xml" archive_sha256="abc12345">
    <summary>9 historical records archived.</summary>
    <record id="20260925T050000Z-rec-complete" kind="completion" status="passed">
      <summary>Terminal completion</summary>
    </record>
  </records>
</task>
""";
        var el = XElement.Parse(taskXml);
        var descriptor = TaskArchivalDescriptor.FromTaskElement(el);

        Assert.AreEqual("20260925-task-done", descriptor.TaskId);
        Assert.IsTrue(descriptor.IsArchived);
        Assert.AreEqual(10, descriptor.TotalCount);
        Assert.AreEqual(1, descriptor.InlineCount);
        Assert.AreEqual(9, descriptor.ArchivedCount);
        Assert.AreEqual("20260925-iter/tasks.archive.xml", descriptor.ArchivePath);
        Assert.AreEqual("abc12345", descriptor.ArchiveSha256);
    }

    [TestMethod]
    public void TaskArchivalDescriptor_ComputeRecordSetDigest_ProducesDeterministicSha256()
    {
        var rec1 = XElement.Parse("<record id=\"1\"><summary>Test 1</summary></record>");
        var rec2 = XElement.Parse("<record id=\"2\"><summary>Test 2</summary></record>");

        var digest1 = TaskArchivalDescriptor.ComputeRecordSetDigest(new[] { rec1, rec2 });
        var digest2 = TaskArchivalDescriptor.ComputeRecordSetDigest(new[] { rec1, rec2 });

        Assert.IsNotNull(digest1);
        Assert.AreEqual(64, digest1.Length);
        Assert.AreEqual(digest1, digest2);
    }
}
