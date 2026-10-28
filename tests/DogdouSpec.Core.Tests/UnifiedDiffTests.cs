using DogdouSpec.Core.Formatting;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class UnifiedDiffTests
{
    [TestMethod]
    public void Replacement_UsesUnifiedRangesAndContext()
    {
        Assert.AreEqual("--- local/file.md\n+++ embedded/file.md\n@@ -1,3 +1,3 @@\n a\n-old\n+new\n z\n",
            UnifiedDiff.Create("file.md", "a\nold\nz\n", "a\nnew\nz\n"));
    }

    [TestMethod]
    public void NewAndDeletedFiles_UseDevNullAndZeroRanges()
    {
        Assert.AreEqual("--- /dev/null\n+++ embedded/file.md\n@@ -0,0 +1,1 @@\n+new\n", UnifiedDiff.Create("file.md", null, "new\n"));
        Assert.AreEqual("--- local/file.md\n+++ /dev/null\n@@ -1,1 +0,0 @@\n-old\n", UnifiedDiff.Create("file.md", "old\n", null));
    }

    [TestMethod]
    public void LineEndingsAndFinalNewline_AreHandledExplicitly()
    {
        Assert.AreEqual(string.Empty, UnifiedDiff.Create("file.md", "text\r\n", "text\n"));
        Assert.AreEqual("--- local/file.md\n+++ embedded/file.md\n@@ -1,1 +1,1 @@\n-text\n\\ No newline at end of file\n+text\n",
            UnifiedDiff.Create("file.md", "text", "text\n"));
        Assert.IsTrue(UnifiedDiff.Create("schema.xsd", "text\r\n", "text\n", false).Contains("-text\r\n", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DistantChanges_ProduceSeparateHunks()
    {
        var lines = Enumerable.Range(1, 30).Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var before = string.Join('\n', lines) + "\n";
        lines[1] = "changed two";
        lines[28] = "changed twenty-nine";
        var diff = UnifiedDiff.Create("file.md", before, string.Join('\n', lines) + "\n");
        Assert.AreEqual(2, diff.Split('\n').Count(line => line.StartsWith("@@", StringComparison.Ordinal)));
        Assert.IsTrue(diff.Contains("@@ -1,5 +1,5 @@", StringComparison.Ordinal));
        Assert.IsTrue(diff.Contains("@@ -26,5 +26,5 @@", StringComparison.Ordinal));
    }

    [TestMethod]
    public void LargeReplacement_StillContainsTheCompleteChanges()
    {
        var before = string.Concat(Enumerable.Repeat("old\n", 2100));
        var after = string.Concat(Enumerable.Repeat("new\n", 2100));
        var diff = UnifiedDiff.Create("file.md", before, after);
        Assert.IsTrue(diff.Contains("@@ -1,2100 +1,2100 @@", StringComparison.Ordinal));
        Assert.AreEqual(2100, diff.Split('\n').Count(line => line == "-old"));
        Assert.AreEqual(2100, diff.Split('\n').Count(line => line == "+new"));
    }
}
