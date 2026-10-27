using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Iterations;
using DogdouSpec.Core.Tasks;
using DogdouSpec.Core.Time;
using DogdouSpec.Core.Validation;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class IterationConstraintCoreTests
{
    private static string RepoRoot = null!;
    private string _tempDir = null!;
    private static readonly string[] WhitespaceConstraint = new[] { "   " };

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
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_ConstraintCoreTests_" + Guid.NewGuid().ToString("N"));
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
    public void AddConstraint_WithKind_AppendsSuccessfullyAndIncrementsRevision()
    {
        var ws = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";

        var (success, env, diags) = IterationConstraintAuthor.Add(
            ws,
            iterId,
            ".NET 10.0 runtime is required.",
            kind: "environment");

        Assert.IsTrue(success, string.Join("; ", diags.Select(d => d.Message)));
        Assert.IsNotNull(env);

        var specPath = Path.Combine(ws, iterId, "spec.xml");
        var specDoc = XDocument.Load(specPath);
        var constraintsEl = specDoc.Root?.Element("constraints");
        Assert.IsNotNull(constraintsEl);

        var constraint = constraintsEl.Elements("constraint").FirstOrDefault();
        Assert.IsNotNull(constraint);
        Assert.AreEqual("environment", constraint.Attribute("kind")?.Value);
        Assert.AreEqual(".NET 10.0 runtime is required.", constraint.Value);

        // Verify schema validation passes
        var valResult = SchemaValidator.Validate(ws, iterationId: iterId);
        Assert.IsTrue(valResult.IsValid, string.Join("; ", valResult.Diagnostics.Select(d => d.Message)));
    }

    [TestMethod]
    public void AddConstraint_AutoDetectKind_ExtractsKindAndText()
    {
        var ws = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";

        var (success1, _, diags1) = IterationConstraintAuthor.Add(
            ws,
            iterId,
            "configuration: LF line endings must be enforced.");
        Assert.IsTrue(success1, string.Join("; ", diags1.Select(d => d.Message)));

        var (success2, _, diags2) = IterationConstraintAuthor.Add(
            ws,
            iterId,
            "[toolchain] CMake 3.28 or later is required.");
        Assert.IsTrue(success2, string.Join("; ", diags2.Select(d => d.Message)));

        var (listSuccess, _, items, listDiags) = IterationConstraintAuthor.List(ws, iterId);
        Assert.IsTrue(listSuccess, string.Join("; ", listDiags.Select(d => d.Message)));
        Assert.AreEqual(2, items.Count);
        Assert.AreEqual("configuration", items[0].Kind);
        Assert.AreEqual("LF line endings must be enforced.", items[0].Text);
        Assert.AreEqual("toolchain", items[1].Kind);
        Assert.AreEqual("CMake 3.28 or later is required.", items[1].Text);
    }

    [TestMethod]
    public void AddConstraint_ExplicitId_ValidatesIdGrammarAndUniqueness()
    {
        var ws = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";

        // Bad ID grammar
        var (badSuccess, _, badDiags) = IterationConstraintAuthor.Add(
            ws,
            iterId,
            "Some constraint",
            constraintId: "invalid_id_grammar");
        Assert.IsFalse(badSuccess);
        Assert.IsTrue(badDiags.Any(d => d.Code == DiagnosticCodes.InvalidIdGrammar));

        // Valid ID
        var validId = "20260823-constraint-custom-env";
        var (ok1, _, diags1) = IterationConstraintAuthor.Add(
            ws,
            iterId,
            "Custom environment constraint",
            kind: "environment",
            constraintId: validId);
        Assert.IsTrue(ok1, string.Join("; ", diags1.Select(d => d.Message)));

        // Duplicate ID
        var (ok2, _, diags2) = IterationConstraintAuthor.Add(
            ws,
            iterId,
            "Another constraint with duplicate ID",
            constraintId: validId);
        Assert.IsFalse(ok2);
        Assert.IsTrue(diags2.Any(d => d.Code == DiagnosticCodes.DuplicateId));
    }

    [TestMethod]
    public void AddConstraint_OptimisticRevisionMismatch_ReturnsRevisionConflict()
    {
        var ws = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";

        var (success, _, diags) = IterationConstraintAuthor.Add(
            ws,
            iterId,
            "Constraint with stale revision",
            expectedSpecRevision: 999);

        Assert.IsFalse(success);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.RevisionConflict));
    }

    [TestMethod]
    public void RemoveConstraint_Existing_RemovesAndIncrementsRevision()
    {
        var ws = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";

        var (addOk, _, addDiags) = IterationConstraintAuthor.Add(
            ws,
            iterId,
            "Temporary constraint to remove",
            constraintId: "20260823-constraint-to-delete");
        Assert.IsTrue(addOk, string.Join("; ", addDiags.Select(d => d.Message)));

        var (remOk, _, remDiags) = IterationConstraintAuthor.Remove(
            ws,
            iterId,
            "20260823-constraint-to-delete");
        Assert.IsTrue(remOk, string.Join("; ", remDiags.Select(d => d.Message)));

        var (listOk, _, items, _) = IterationConstraintAuthor.List(ws, iterId);
        Assert.IsTrue(listOk);
        Assert.AreEqual(0, items.Count);

        // Verify <constraints> element is pruned when empty
        var specDoc = XDocument.Load(Path.Combine(ws, iterId, "spec.xml"));
        Assert.IsNull(specDoc.Root?.Element("constraints"));

        // Validation passes after removal
        var valResult = SchemaValidator.Validate(ws, iterationId: iterId);
        Assert.IsTrue(valResult.IsValid, string.Join("; ", valResult.Diagnostics.Select(d => d.Message)));
    }

    [TestMethod]
    public void AddConstraint_CompletedIteration_FailsWithOwnerDecisionRequired()
    {
        var ws = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";

        // Mark iteration completed
        var specPath = Path.Combine(ws, iterId, "spec.xml");
        var specDoc = XDocument.Load(specPath);
        specDoc.Root?.SetAttributeValue("status", "completed");
        specDoc.Save(specPath);

        var (success, _, diags) = IterationConstraintAuthor.Add(
            ws,
            iterId,
            "Cannot add to completed iteration",
            kind: "policy");

        Assert.IsFalse(success);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.OwnerDecisionRequired));
    }

    [TestMethod]
    public void IterationCreator_InvalidConstraints_FailsValidation()
    {
        var ws = CreateWorkspaceCopy();
        var iterId = "20260826-invalid-constraints";

        // Empty constraint item
        var (f1, _, d1) = IterationCreator.Create(ws, iterId, "feature", constraints: WhitespaceConstraint);
        Assert.IsFalse(f1);
        Assert.IsTrue(d1.Any(d => d.Code == DiagnosticCodes.InvalidArgument));

        // Excessively long constraint item (> 4096)
        var longText = new string('x', 4097);
        var longConstraints = new List<string> { longText };
        var (f2, _, d2) = IterationCreator.Create(ws, iterId, "feature", constraints: longConstraints);
        Assert.IsFalse(f2);
        Assert.IsTrue(d2.Any(d => d.Code == DiagnosticCodes.InvalidArgument));
    }

    [TestMethod]
    public void RemoveConstraint_NotFound_ReturnsResourceNotFound()
    {
        var ws = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";

        var (remOk, _, remDiags) = IterationConstraintAuthor.Remove(
            ws,
            iterId,
            "20260823-constraint-nonexistent");
        Assert.IsFalse(remOk);
        Assert.IsTrue(remDiags.Any(d => d.Code == DiagnosticCodes.ResourceNotFound));
    }

    [TestMethod]
    public void IterationCreator_WithConstraints_GeneratesValidConstraintsInSpecXml()
    {
        var ws = CreateWorkspaceCopy();
        var iterId = "20260825-constraints-test";
        var constraints = new[]
        {
            "environment: .NET 10.0 runtime is required.",
            "configuration: Deterministic builds enabled."
        };

        var (success, _, diags) = IterationCreator.Create(
            ws,
            iterId,
            "feature",
            constraints: constraints);

        Assert.IsTrue(success, string.Join("; ", diags.Select(d => d.Message)));

        var (listOk, _, items, listDiags) = IterationConstraintAuthor.List(ws, iterId);
        Assert.IsTrue(listOk, string.Join("; ", listDiags.Select(d => d.Message)));
        Assert.AreEqual(2, items.Count);
        Assert.AreEqual("environment", items[0].Kind);
        Assert.AreEqual(".NET 10.0 runtime is required.", items[0].Text);
        Assert.AreEqual("configuration", items[1].Kind);
        Assert.AreEqual("Deterministic builds enabled.", items[1].Text);

        var valResult = SchemaValidator.Validate(ws, iterationId: iterId);
        Assert.IsTrue(valResult.IsValid, string.Join("; ", valResult.Diagnostics.Select(d => d.Message)));
    }

    [TestMethod]
    public void TaskContextQuery_IncludesBothIterationAndTaskConstraintsWithScopeAndKind()
    {
        var ws = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";

        // Add an iteration-level constraint
        var (addOk, _, addDiags) = IterationConstraintAuthor.Add(
            ws,
            iterId,
            ".NET 10 required for entire iteration",
            kind: "environment",
            constraintId: "20260823-constraint-iter-dotnet");
        Assert.IsTrue(addOk, string.Join("; ", addDiags.Select(d => d.Message)));

        // Query task context for a task that has its own task-level constraints
        var taskId = "20260823-task-xpath-projection";
        var (ctxOk, ctxResult, ctxDiags) = TaskContextQuery.Query(ws, taskId, iterId);

        Assert.IsTrue(ctxOk, string.Join("; ", ctxDiags.Select(d => d.Message)));
        Assert.IsNotNull(ctxResult);

        // Verify constraints collection contains both iteration and task constraints
        var iterConstraint = ctxResult.Constraints.FirstOrDefault(c => c.Id == "20260823-constraint-iter-dotnet");
        Assert.IsNotNull(iterConstraint);
        Assert.AreEqual("iteration", iterConstraint.Scope);
        Assert.AreEqual("environment", iterConstraint.Kind);
        Assert.AreEqual(".NET 10 required for entire iteration", iterConstraint.Text);

        var taskConstraint = ctxResult.Constraints.FirstOrDefault(c => c.Scope == "task");
        Assert.IsNotNull(taskConstraint);

        // Verify XML serialization contains scope and kind attributes
        var xml = ctxResult.ToXmlString();
        StringAssert.Contains(xml, "scope=\"iteration\"");
        StringAssert.Contains(xml, "kind=\"environment\"");
        StringAssert.Contains(xml, "scope=\"task\"");

        // Verify Human serialization
        var human = ctxResult.ToHumanString();
        StringAssert.Contains(human, "(iteration, environment)");
    }
}
