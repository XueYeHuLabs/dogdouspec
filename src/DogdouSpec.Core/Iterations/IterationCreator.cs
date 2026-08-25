using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Schema;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Resources;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Time;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Iterations;

/// <summary>
/// Mutating creator for new feature and research iteration directories.
/// Stages documents, validates prospectively, acquires writer lock, recovers, and atomically publishes directory.
/// </summary>
public static class IterationCreator
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Create(
        string workspaceRoot,
        string iterationId,
        string kind,
        IClock? clock = null,
        IFaultInjector? faultInjector = null,
        string version = "1.0")
    {
        clock ??= SystemClock.Instance;

        // 1. Validate ID grammar
        var (isIdValid, normalizedId, idError) = WorkspaceDiscovery.ValidateIterationId(iterationId);
        if (!isIdValid || idError != null)
        {
            return (false, null, new[] { idError ?? Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Invalid iteration ID.") });
        }

        // 2. Validate kind
        if (!string.Equals(kind, "feature", StringComparison.Ordinal) &&
            !string.Equals(kind, "research", StringComparison.Ordinal))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Iteration kind must be 'feature' or 'research', but got '{kind}'.") });
        }

        // 3. Acquire writer lock
        var (lockAcquired, wsLock, lockError) = WorkspaceLock.Acquire(workspaceRoot);
        if (!lockAcquired || wsLock == null)
        {
            return (false, null, new[] { lockError! });
        }

        using (wsLock)
        {
            // 4. Startup recovery
            var (recSuccess, recError) = StartupRecovery.Run(workspaceRoot);
            if (!recSuccess || recError != null)
            {
                return (false, null, new[] { recError! });
            }

            // 5. Target existence check
            var targetIterDir = Path.Combine(workspaceRoot, normalizedId);
            if (Directory.Exists(targetIterDir))
            {
                return (false, null, new[] { Diagnostic.Error(
                    DiagnosticCodes.IterationAlreadyExists,
                    $"Iteration directory '{normalizedId}' already exists in workspace '{workspaceRoot}'. Refusing to overwrite.") });
            }

            // 6. Generate draft documents with deterministic IDs
            var nowUtc = clock.UtcNow;
            var isoTime = nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            var firstDash = normalizedId.IndexOf('-');
            var timePrefix = normalizedId.Substring(0, firstDash);
            var slug = normalizedId.Substring(firstDash + 1);

            string specContent;
            if (string.Equals(kind, "feature", StringComparison.Ordinal))
            {
                specContent = GenerateFeatureSpecXml(normalizedId, timePrefix, slug, isoTime);
            }
            else
            {
                specContent = GenerateResearchSpecXml(normalizedId, timePrefix, slug, isoTime);
            }

            var tasksContent = GenerateTasksXml(normalizedId, timePrefix, slug);

            // 7. Prospective validation against prospective workspace view
            var specRelPath = $"{normalizedId}/spec.xml";
            var tasksRelPath = $"{normalizedId}/tasks.xml";

            var prospectiveDocs = new[]
            {
                new ProspectiveDocument(specRelPath, specContent, IsNew: true),
                new ProspectiveDocument(tasksRelPath, tasksContent, IsNew: true)
            };

            var valResult = SchemaValidator.ValidateProspective(workspaceRoot, prospectiveDocs, version);
            if (!valResult.IsValid)
            {
                return (false, null, valResult.Diagnostics);
            }

            // 8. Staging directory under _tmp
            var txId = $"{nowUtc:yyyyMMddTHHmmssZ}-create-{Guid.NewGuid():N}";
            var tmpCreateDir = Path.Combine(workspaceRoot, "_tmp", $"create_{txId}");
            var stagedIterDir = Path.Combine(tmpCreateDir, normalizedId);

            try
            {
                Directory.CreateDirectory(stagedIterDir);

                faultInjector?.InjectFaultIfMatched(FaultPhase.BeforeStaging);

                var stagedSpecPath = Path.Combine(stagedIterDir, "spec.xml");
                using (var fs = new FileStream(stagedSpecPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var sw = new StreamWriter(fs, Utf8NoBom))
                {
                    sw.Write(specContent);
                    sw.Flush();
                    fs.Flush(true);
                }

                var stagedTasksPath = Path.Combine(stagedIterDir, "tasks.xml");
                using (var fs = new FileStream(stagedTasksPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var sw = new StreamWriter(fs, Utf8NoBom))
                {
                    sw.Write(tasksContent);
                    sw.Flush();
                    fs.Flush(true);
                }

                faultInjector?.InjectFaultIfMatched(FaultPhase.AfterStagingBeforeValidation);

                // Write creation marker in 'prepared' state and flush
                var markerPath = Path.Combine(tmpCreateDir, "marker.xml");
                WriteCreateMarkerXml(markerPath, txId, normalizedId, "prepared", isoTime);

                faultInjector?.InjectFaultIfMatched(FaultPhase.AfterValidationBeforeCommitMarker);

                // Update creation marker to 'publishing' state and flush
                WriteCreateMarkerXml(markerPath, txId, normalizedId, "publishing", isoTime);

                faultInjector?.InjectFaultIfMatched(FaultPhase.AfterCommitMarkerBeforePublish);

                // 9. Atomic publication via Directory.Move (same filesystem volume)
                Directory.Move(stagedIterDir, targetIterDir);

                // Update creation marker to 'committed' state and flush
                WriteCreateMarkerXml(markerPath, txId, normalizedId, "committed", isoTime);

                faultInjector?.InjectFaultIfMatched(FaultPhase.AfterPublishBeforeCleanup);

                // 10. Validate final created iteration before cleanup
                var finalVal = SchemaValidator.Validate(workspaceRoot, iterationId: normalizedId, version: version);
                if (!finalVal.IsValid)
                {
                    return (false, null, finalVal.Diagnostics);
                }

                // 11. Clean up creation staging dir
                PathSecurity.SafeDeleteCliTempEntry(workspaceRoot, tmpCreateDir);

                var mutatedDocs = new[]
                {
                    new MutatedDocument(specRelPath, 1),
                    new MutatedDocument(tasksRelPath, 1)
                };

                return (true, new MutationEnvelope("iteration create", mutatedDocs), Array.Empty<Diagnostic>());
            }
            catch (Exception ex)
            {
                if (Directory.Exists(tmpCreateDir))
                {
                    PathSecurity.SafeDeleteCliTempEntry(workspaceRoot, tmpCreateDir);
                }

                return (false, null, new[] { Diagnostic.Error(
                    DiagnosticCodes.CommitFailed,
                    $"Failed to create iteration directory '{normalizedId}': {ex.Message}") });
            }
        }
    }

    private static void WriteCreateMarkerXml(string markerPath, string txId, string iterationId, string state, string isoTime)
    {
        var markerXml = $"""
<?xml version="1.0" encoding="utf-8"?>
<create-marker id="{txId}" iteration_id="{iterationId}" state="{state}" created_at="{isoTime}"/>
""";
        using var fs = new FileStream(markerPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var sw = new StreamWriter(fs, Utf8NoBom);
        sw.Write(markerXml);
        sw.Flush();
        fs.Flush(true);
    }

    private static string GenerateFeatureSpecXml(string id, string date, string slug, string isoTime) =>
$"""
<?xml version="1.0" encoding="utf-8"?>
<iteration
  id="{id}"
  schema_version="1.0"
  revision="1"
  kind="feature"
  status="draft"
  created_at="{isoTime}"
  updated_at="{isoTime}">
  <index>
    <summary>Draft feature iteration {slug}.</summary>
    <term key="kind" value="feature"/>
    <term key="iteration" value="{id}"/>
    <term key="status" value="draft"/>
  </index>
  <product>
    <objective>Objective pending definition for {id}.</objective>
    <deliverables>
      <deliverable id="{date}-deliv-{slug}">
        <index>
          <summary>Initial deliverable placeholder for {id}.</summary>
          <term key="kind" value="deliverable"/>
        </index>
        <description>Deliverable pending definition.</description>
      </deliverable>
    </deliverables>
    <scope>
      <included/>
      <excluded/>
    </scope>
    <requirements>
      <requirement id="{date}-req-{slug}" status="proposed">
        <index>
          <summary>Initial proposed requirement for {id}.</summary>
          <term key="kind" value="requirement"/>
        </index>
        <statement>Requirement statement pending definition.</statement>
        <rationale>Rationale pending definition.</rationale>
      </requirement>
    </requirements>
    <acceptance>
      <criterion id="{date}-crit-{slug}" decision="pending">Product criterion pending definition.</criterion>
    </acceptance>
  </product>
  <confirmations/>
</iteration>

""".Replace("\r\n", "\n");

    private static string GenerateResearchSpecXml(string id, string date, string slug, string isoTime) =>
$"""
<?xml version="1.0" encoding="utf-8"?>
<iteration
  id="{id}"
  schema_version="1.0"
  revision="1"
  kind="research"
  status="draft"
  created_at="{isoTime}"
  updated_at="{isoTime}">
  <index>
    <summary>Draft research work {slug}.</summary>
    <term key="kind" value="research"/>
    <term key="iteration" value="{id}"/>
    <term key="status" value="draft"/>
  </index>
  <research>
    <objective>Research objective pending definition for {id}.</objective>
    <questions>
      <question id="{date}-q-{slug}" status="open">
        <index>
          <summary>Initial research question for {id}.</summary>
          <term key="kind" value="question"/>
        </index>
        <statement>Research question pending definition.</statement>
        <rationale>Rationale pending definition.</rationale>
      </question>
    </questions>
    <method>Research method pending definition.</method>
    <boundaries/>
    <outputs/>
    <acceptance>
      <criterion id="{date}-crit-{slug}" decision="pending">Research criterion pending definition.</criterion>
    </acceptance>
  </research>
  <confirmations/>
</iteration>

""".Replace("\r\n", "\n");

    private static string GenerateTasksXml(string id, string date, string slug) =>
$"""
<?xml version="1.0" encoding="utf-8"?>
<tasks
  id="{date}-tasks-{slug}"
  iteration="{id}"
  schema_version="1.0"
  revision="1">
  <index>
    <summary>Tasks for iteration {id}.</summary>
    <term key="iteration" value="{id}"/>
  </index>
</tasks>

""".Replace("\r\n", "\n");
}
