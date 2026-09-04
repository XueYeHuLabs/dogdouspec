using System.CommandLine;
using System.Globalization;
using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Iterations;
using DogdouSpec.Core.Revisions;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Cli.Commands;

public static class IterationCommand
{
    public static Command BuildCommand()
    {
        var iterationCmd = new Command("iteration", "Manage and inspect DogdouSpec iterations");

        var listCmd = BuildListCommand();
        var createCmd = BuildCreateCommand();
        var readinessCmd = BuildReadinessCommand();
        var confirmCmd = BuildConfirmCommand();
        var activateCmd = BuildActivateCommand();
        var completeCmd = BuildCompleteCommand();
        var summaryCmd = SummaryCommand.BuildCommand();
        var criterionCmd = BuildCriterionCommand();

        iterationCmd.Add(listCmd);
        iterationCmd.Add(createCmd);
        iterationCmd.Add(readinessCmd);
        iterationCmd.Add(confirmCmd);
        iterationCmd.Add(activateCmd);
        iterationCmd.Add(completeCmd);
        iterationCmd.Add(summaryCmd);
        iterationCmd.Add(criterionCmd);

        return iterationCmd;
    }

    private static Command BuildListCommand()
    {
        var listCmd = new Command("list", "List date-prefixed iterations in workspace");

        var workspaceRootOption = new Option<string?>("--workspace-root")
        {
            Description = "Explicit path to workspace root or project directory containing .dogdouspec"
        };

        var formatOption = new Option<string?>("--format")
        {
            Description = "Output format (xml or human)"
        };
        formatOption.AcceptOnlyFromAmong("xml", "human");

        listCmd.Add(workspaceRootOption);
        listCmd.Add(formatOption);

        listCmd.SetAction(parseResult =>
        {
            var workspaceRoot = parseResult.GetValue(workspaceRootOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = WorkspaceCommand.ResolveFormat(formatArg);

            var (discoverSuccess, discoveredRoot, discoverError) = WorkspaceDiscovery.FindWorkspaceRoot(
                workspaceRoot,
                Environment.CurrentDirectory);

            if (!discoverSuccess || discoverError != null)
            {
                var envelope = new DiagnosticsEnvelope("iteration list", discoverError!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (success, result, diagnostics) = IterationLister.List(discoveredRoot);

            if (!success || diagnostics.Count > 0)
            {
                var envelope = new DiagnosticsEnvelope("iteration list", diagnostics);
                Console.Error.Write(envelope.Format(format));
                return envelope.GetExitCode();
            }

            if (result != null)
            {
                Console.Out.Write(result.Format(format));
            }

            return 0;
        });

        return listCmd;
    }

    private static Command BuildCreateCommand()
    {
        var createCmd = new Command("create", "Atomically create a new feature or research iteration (mutating)");

        var idOption = new Option<string>("--id")
        {
            Description = "Iteration identifier following TimeFirstId grammar (YYYYMMDD-name or YYYYMMDDTHHmmssZ-name)",
            Required = true
        };

        var kindOption = new Option<string>("--kind")
        {
            Description = "Iteration kind (feature or research)",
            Required = true
        };
        kindOption.AcceptOnlyFromAmong("feature", "research");

        var activateOption = new Option<bool>("--activate")
        {
            Description = "Create iteration in active state immediately (with initial requirement approved)"
        };

        var criterionOption = new Option<string[]>("--criterion")
        {
            Description = "Acceptance criterion text (repeatable)",
            AllowMultipleArgumentsPerToken = false
        };

        var workspaceRootOption = new Option<string?>("--workspace-root")
        {
            Description = "Explicit path to workspace root or project directory containing .dogdouspec"
        };

        var formatOption = new Option<string?>("--format")
        {
            Description = "Output format (xml or human)"
        };
        formatOption.AcceptOnlyFromAmong("xml", "human");

        createCmd.Add(idOption);
        createCmd.Add(kindOption);
        createCmd.Add(activateOption);
        createCmd.Add(criterionOption);
        createCmd.Add(workspaceRootOption);
        createCmd.Add(formatOption);

        createCmd.SetAction(parseResult =>
        {
            var id = parseResult.GetValue(idOption);
            var kind = parseResult.GetValue(kindOption);
            var activate = parseResult.GetValue(activateOption);
            var criteria = parseResult.GetValue(criterionOption);
            var workspaceRoot = parseResult.GetValue(workspaceRootOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = WorkspaceCommand.ResolveFormat(formatArg);

            if (string.IsNullOrWhiteSpace(id))
            {
                var envelope = new DiagnosticsEnvelope("iteration create", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--id option is required."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (string.IsNullOrWhiteSpace(kind))
            {
                var envelope = new DiagnosticsEnvelope("iteration create", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--kind option is required."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (isIdValid, _, idGrammarError) = WorkspaceDiscovery.ValidateIterationId(id);
            if (!isIdValid || idGrammarError != null)
            {
                var envelope = new DiagnosticsEnvelope("iteration create", idGrammarError!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (activate)
            {
                if (criteria == null || criteria.Length == 0)
                {
                    var envelope = new DiagnosticsEnvelope("iteration create", Diagnostic.Error(
                        DiagnosticCodes.CriterionUndefined,
                        "Iteration creation with --activate requires at least one defined --criterion. Cannot activate with default placeholder criteria."));
                    Console.Error.Write(envelope.Format(format));
                    return 5;
                }

                for (var i = 0; i < criteria.Length; i++)
                {
                    var (isValid, reason) = IterationCriterionPolicy.Validate(criteria[i], $"index-{i + 1}");
                    if (!isValid)
                    {
                        var envelope = new DiagnosticsEnvelope("iteration create", Diagnostic.Error(
                            DiagnosticCodes.CriterionUndefined,
                            reason ?? "Acceptance criterion text is undefined."));
                        Console.Error.Write(envelope.Format(format));
                        return 5;
                    }
                }
            }

            var (discoverSuccess, discoveredRoot, discoverError) = WorkspaceDiscovery.FindWorkspaceRoot(
                workspaceRoot,
                Environment.CurrentDirectory);

            if (!discoverSuccess || discoverError != null)
            {
                var envelope = new DiagnosticsEnvelope("iteration create", discoverError!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (success, envelopeResult, diagnostics) = IterationCreator.Create(
                discoveredRoot,
                id,
                kind,
                activate: activate,
                criteria: criteria);

            if (!success || diagnostics.Count > 0)
            {
                var diagEnvelope = new DiagnosticsEnvelope("iteration create", diagnostics);
                Console.Error.Write(diagEnvelope.Format(format));
                return diagEnvelope.GetExitCode();
            }

            if (envelopeResult != null)
            {
                Console.Out.Write(envelopeResult.Format(format));
            }

            return 0;
        });

        return createCmd;
    }

    private static Command BuildReadinessCommand()
    {
        var readinessCmd = new Command("readiness", "Assess and report iteration technical readiness for activation or completion phase");

        var iterationOption = new Option<string>("--iteration")
        {
            Description = "Iteration identifier following YYYYMMDD-name grammar",
            Required = true
        };

        var phaseOption = new Option<string>("--phase")
        {
            Description = "Readiness phase to evaluate (activation or completion)",
            Required = true
        };
        phaseOption.AcceptOnlyFromAmong("activation", "completion");

        var workspaceRootOption = new Option<string?>("--workspace-root")
        {
            Description = "Explicit path to workspace root or project directory containing .dogdouspec"
        };

        var formatOption = new Option<string?>("--format")
        {
            Description = "Output format (xml or human)"
        };
        formatOption.AcceptOnlyFromAmong("xml", "human");

        readinessCmd.Add(iterationOption);
        readinessCmd.Add(phaseOption);
        readinessCmd.Add(workspaceRootOption);
        readinessCmd.Add(formatOption);

        readinessCmd.SetAction(parseResult =>
        {
            var iterationId = parseResult.GetValue(iterationOption);
            var phase = parseResult.GetValue(phaseOption);
            var workspaceRoot = parseResult.GetValue(workspaceRootOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = WorkspaceCommand.ResolveFormat(formatArg);

            if (string.IsNullOrWhiteSpace(iterationId))
            {
                var envelope = new DiagnosticsEnvelope("iteration readiness", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--iteration option is required."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (string.IsNullOrWhiteSpace(phase))
            {
                var envelope = new DiagnosticsEnvelope("iteration readiness", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--phase option is required."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (discoverSuccess, discoveredRoot, discoverError) = WorkspaceDiscovery.FindWorkspaceRoot(
                workspaceRoot,
                Environment.CurrentDirectory);

            if (!discoverSuccess || discoverError != null)
            {
                var envelope = new DiagnosticsEnvelope("iteration readiness", discoverError!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (success, result, diagnostics) = IterationReadiness.Assess(
                discoveredRoot,
                iterationId,
                phase);

            if (!success || diagnostics.Count > 0)
            {
                var envelope = new DiagnosticsEnvelope("iteration readiness", diagnostics);
                Console.Error.Write(envelope.Format(format));
                return envelope.GetExitCode();
            }

            if (result != null)
            {
                Console.Out.Write(result.Format(format));
            }

            return 0;
        });

        return readinessCmd;
    }

    private static Command BuildConfirmCommand()
    {
        var confirmCmd = new Command("confirm", "Atomically confirm iteration product decisions and lifecycle (mutating); --dry-run validates without writing");

        var stdinOption = new Option<bool>("--stdin")
        {
            Description = "Read iteration-confirmation XML request from standard input (mutually exclusive with --file; exactly one required)"
        };

        var fileOption = new Option<string?>("--file")
        {
            Description = "Path to file containing iteration-confirmation XML request (mutually exclusive with --stdin; exactly one required)"
        };

        var iterationOption = new Option<string?>("--iteration")
        {
            Description = "Iteration identifier; must match request attribute if both specified"
        };

        var expectedRevisionOption = new Option<int?>("--expected-revision")
        {
            Description = "Expected spec.xml revision; must match request attribute if both specified"
        };

        var expectedTasksRevisionOption = new Option<int?>("--expected-tasks-revision")
        {
            Description = "Expected tasks.xml revision; must match request attribute if both specified"
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Validate mutation preconditions and report prospective revision without writing"
        };

        var workspaceRootOption = new Option<string?>("--workspace-root")
        {
            Description = "Explicit path to workspace root or project directory containing .dogdouspec"
        };

        var formatOption = new Option<string?>("--format")
        {
            Description = "Output format (xml or human)"
        };
        formatOption.AcceptOnlyFromAmong("xml", "human");

        confirmCmd.Add(stdinOption);
        confirmCmd.Add(fileOption);
        confirmCmd.Add(iterationOption);
        confirmCmd.Add(expectedRevisionOption);
        confirmCmd.Add(expectedTasksRevisionOption);
        confirmCmd.Add(dryRunOption);
        confirmCmd.Add(workspaceRootOption);
        confirmCmd.Add(formatOption);

        confirmCmd.SetAction(parseResult =>
        {
            var hasStdin = parseResult.GetValue(stdinOption);
            var filePath = parseResult.GetValue(fileOption);
            var iterationId = parseResult.GetValue(iterationOption);
            var expectedRevision = parseResult.GetValue(expectedRevisionOption);
            var expectedTasksRevision = parseResult.GetValue(expectedTasksRevisionOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var workspaceRoot = parseResult.GetValue(workspaceRootOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = WorkspaceCommand.ResolveFormat(formatArg);

            if (hasStdin && !string.IsNullOrWhiteSpace(filePath))
            {
                var envelope = new DiagnosticsEnvelope("iteration confirm", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "Specify either --stdin or --file, not both."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (!hasStdin && string.IsNullOrWhiteSpace(filePath))
            {
                var envelope = new DiagnosticsEnvelope("iteration confirm", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "Either --stdin or --file must be specified."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            string requestXml;
            if (hasStdin)
            {
                requestXml = Console.In.ReadToEnd();
            }
            else
            {
                if (!File.Exists(filePath))
                {
                    var envelope = new DiagnosticsEnvelope("iteration confirm", Diagnostic.Error(
                        DiagnosticCodes.InvalidArgument,
                        $"Iteration confirmation XML file '{filePath}' does not exist."));
                    Console.Error.Write(envelope.Format(format));
                    return 2;
                }

                try
                {
                    requestXml = File.ReadAllText(filePath!);
                }
                catch (Exception ex)
                {
                    var envelope = new DiagnosticsEnvelope("iteration confirm", Diagnostic.Error(
                        DiagnosticCodes.InvalidArgument,
                        $"Failed to read file '{filePath}': {ex.Message}"));
                    Console.Error.Write(envelope.Format(format));
                    return 2;
                }
            }

            var (discoverSuccess, discoveredRoot, discoverError) = WorkspaceDiscovery.FindWorkspaceRoot(
                workspaceRoot,
                Environment.CurrentDirectory);

            if (!discoverSuccess || discoverError != null)
            {
                var envelope = new DiagnosticsEnvelope("iteration confirm", discoverError!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            XDocument requestDoc;
            try
            {
                using var sr = new StringReader(requestXml);
                using var reader = SecureXmlReaderFactory.CreateReader(sr);
                requestDoc = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            }
            catch (XmlException xmlEx)
            {
                var envelope = new DiagnosticsEnvelope("iteration confirm", Diagnostic.Error(
                    DiagnosticCodes.XmlParseError,
                    $"Failed to parse iteration-confirmation XML request: {xmlEx.Message}"));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var rootEl = requestDoc.Root;
            if (rootEl == null || rootEl.Name.LocalName != "iteration-confirmation")
            {
                var envelope = new DiagnosticsEnvelope("iteration confirm", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "Root element must be iteration-confirmation."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (requestResolved, finalXml, resolutionError) = IterationConfirmationRequestResolver.Reconcile(
                discoveredRoot,
                requestDoc,
                iterationId,
                expectedRevision,
                expectedTasksRevision);
            if (!requestResolved || resolutionError != null || string.IsNullOrWhiteSpace(finalXml))
            {
                var envelope = new DiagnosticsEnvelope(
                    "iteration confirm",
                    resolutionError ?? Diagnostic.Error(
                        DiagnosticCodes.InvalidArgument,
                        "Iteration confirmation request could not be resolved."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (success, envelopeResult, diagnostics) = IterationConfirmer.Confirm(
                discoveredRoot,
                finalXml,
                dryRun: dryRun);

            if (!success || diagnostics.Count > 0)
            {
                var diagEnvelope = new DiagnosticsEnvelope("iteration confirm", diagnostics);
                Console.Error.Write(diagEnvelope.Format(format));
                return diagEnvelope.GetExitCode();
            }

            if (envelopeResult != null)
            {
                Console.Out.Write(envelopeResult.Format(format));
            }

            return 0;
        });

        return confirmCmd;
    }

    private static Command BuildActivateCommand()
    {
        var cmd = new Command("activate", "Conveniently activate a draft or replanning iteration (mutating)");

        var iterationOption = new Option<string?>("--iteration")
        {
            Description = "Iteration identifier (omitted auto-resolves if exactly one candidate iteration exists)"
        };

        var autoApproveOption = new Option<bool>("--auto-approve")
        {
            Description = "Automatically approve all proposed requirements and accept proposed design decisions"
        };

        var summaryOption = new Option<string?>("--summary")
        {
            Description = "Confirmation summary rationale (optional)"
        };

        var actorOption = new Option<string?>("--actor")
        {
            Description = "Actor attribution (defaults to 'owner')"
        };

        var expectedSpecRevOption = new Option<int?>("--expected-spec-revision")
        {
            Description = "Expected revision of spec.xml (omitted auto-resolves current revision)"
        };

        var expectedTasksRevOption = new Option<int?>("--expected-tasks-revision")
        {
            Description = "Expected revision of tasks.xml (omitted auto-resolves current revision)"
        };

        var workspaceRootOption = new Option<string?>("--workspace-root")
        {
            Description = "Explicit path to workspace root or project directory containing .dogdouspec"
        };

        var formatOption = new Option<string?>("--format")
        {
            Description = "Output format (xml or human)"
        };
        formatOption.AcceptOnlyFromAmong("xml", "human");

        cmd.Add(iterationOption);
        cmd.Add(autoApproveOption);
        cmd.Add(summaryOption);
        cmd.Add(actorOption);
        cmd.Add(expectedSpecRevOption);
        cmd.Add(expectedTasksRevOption);
        cmd.Add(workspaceRootOption);
        cmd.Add(formatOption);

        cmd.SetAction(parseResult =>
        {
            var explicitIterId = parseResult.GetValue(iterationOption);
            var autoApprove = parseResult.GetValue(autoApproveOption);
            var summary = parseResult.GetValue(summaryOption) ?? "Iteration activation.";
            var actor = parseResult.GetValue(actorOption) ?? "owner";
            var specRev = parseResult.GetValue(expectedSpecRevOption);
            var tasksRev = parseResult.GetValue(expectedTasksRevOption);
            var workspaceRoot = parseResult.GetValue(workspaceRootOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = WorkspaceCommand.ResolveFormat(formatArg);

            var (discoverSuccess, discoveredRoot, discoverError) = WorkspaceDiscovery.FindWorkspaceRoot(
                workspaceRoot,
                Environment.CurrentDirectory);

            if (!discoverSuccess || discoverError != null)
            {
                var envelope = new DiagnosticsEnvelope("iteration activate", discoverError!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var iterId = ResolveIterationId(explicitIterId, discoveredRoot);
            if (string.IsNullOrWhiteSpace(iterId))
            {
                var envelope = new DiagnosticsEnvelope("iteration activate", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--iteration is required when multiple or zero candidate iterations exist."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var specPath = Path.Combine(discoveredRoot, iterId, "spec.xml");
            var tasksPath = Path.Combine(discoveredRoot, iterId, "tasks.xml");

            if (!File.Exists(specPath))
            {
                var envelope = new DiagnosticsEnvelope("iteration activate", Diagnostic.Error(
                    DiagnosticCodes.DocumentNotFound,
                    $"spec.xml not found for iteration '{iterId}'."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            XDocument specDoc;
            try
            {
                specDoc = XDocument.Load(specPath);
            }
            catch (Exception ex)
            {
                var envelope = new DiagnosticsEnvelope("iteration activate", Diagnostic.Error(
                    DiagnosticCodes.XmlParseError,
                    $"Failed to load spec.xml: {ex.Message}"));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (specRevOk, resolvedSpecRev, specRevErr) = DocumentRevisionResolver.ResolveExpectedRevision(
                discoveredRoot,
                $"{iterId}/spec.xml",
                specRev);
            if (!specRevOk || specRevErr != null)
            {
                var envelope = new DiagnosticsEnvelope("iteration activate", specRevErr!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }
            specRev = resolvedSpecRev;

            if (File.Exists(tasksPath) || tasksRev.HasValue)
            {
                var (tasksRevOk, resolvedTasksRev, tasksRevErr) = DocumentRevisionResolver.ResolveExpectedRevision(
                    discoveredRoot,
                    $"{iterId}/tasks.xml",
                    tasksRev);
                if (!tasksRevOk || tasksRevErr != null)
                {
                    var envelope = new DiagnosticsEnvelope("iteration activate", tasksRevErr!);
                    Console.Error.Write(envelope.Format(format));
                    return 2;
                }
                tasksRev = resolvedTasksRev;
            }

            var nowUtc = DateTimeOffset.UtcNow;
            var isoTime = nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            var confirmId = $"{nowUtc:yyyyMMddTHHmmssZ}-confirm-activate";

            var reqs = new StringBuilder();
            var proposedReqs = specDoc.Descendants("requirement")
                .Where(r => string.Equals((string?)r.Attribute("status"), "proposed", StringComparison.OrdinalIgnoreCase))
                .Select(r => (string?)r.Attribute("id"))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();

            if (proposedReqs.Count > 0)
            {
                reqs.AppendLine("  <requirements>");
                foreach (var reqId in proposedReqs)
                {
                    reqs.AppendLine(CultureInfo.InvariantCulture, $"    <requirement target=\"{reqId}\" decision=\"approved\"/>");
                }
                reqs.AppendLine("  </requirements>");
            }

            var design = new StringBuilder();
            var proposedDecisions = specDoc.Descendants("decision")
                .Where(d => string.Equals((string?)d.Attribute("status"), "proposed", StringComparison.OrdinalIgnoreCase))
                .Select(d => (string?)d.Attribute("id"))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();

            if (proposedDecisions.Count > 0)
            {
                design.AppendLine("  <design>");
                foreach (var decId in proposedDecisions)
                {
                    design.AppendLine(CultureInfo.InvariantCulture, $"    <decision target=\"{decId}\" decision=\"accepted\"/>");
                }
                design.AppendLine("  </design>");
            }

            var tasksRevLine = tasksRev.HasValue ? $"  expected_tasks_revision=\"{tasksRev.Value}\"\n" : "";
            var confirmXml = $"""
<?xml version="1.0" encoding="utf-8"?>
<iteration-confirmation
  id="{confirmId}"
  iteration="{iterId}"
  action="activate"
  expected_spec_revision="{specRev}"
{tasksRevLine}  actor="{actor}"
  decided_at="{isoTime}">
  <summary>{SecurityElement.Escape(summary)}</summary>
{reqs}{design}</iteration-confirmation>
""";

            var (success, envelopeResult, diagnostics) = IterationConfirmer.Confirm(
                discoveredRoot,
                confirmXml);

            if (!success || diagnostics.Count > 0)
            {
                var diagEnvelope = new DiagnosticsEnvelope("iteration activate", diagnostics);
                Console.Error.Write(diagEnvelope.Format(format));
                return diagEnvelope.GetExitCode();
            }

            if (envelopeResult != null)
            {
                Console.Out.Write(envelopeResult.Format(format));
            }

            return 0;
        });

        return cmd;
    }

    private static Command BuildCompleteCommand()
    {
        var cmd = new Command("complete", "Conveniently complete and archive an active iteration (mutating)");

        var iterationOption = new Option<string?>("--iteration")
        {
            Description = "Iteration identifier (omitted auto-resolves if exactly one candidate iteration exists)"
        };

        var acceptAllOption = new Option<bool>("--accept-all")
        {
            Description = "Accept all pending acceptance criteria"
        };

        var summaryOption = new Option<string?>("--summary")
        {
            Description = "Completion summary rationale (optional)"
        };

        var actorOption = new Option<string?>("--actor")
        {
            Description = "Actor attribution (defaults to 'owner')"
        };

        var expectedSpecRevOption = new Option<int?>("--expected-spec-revision")
        {
            Description = "Expected revision of spec.xml (omitted auto-resolves current revision)"
        };

        var expectedTasksRevOption = new Option<int?>("--expected-tasks-revision")
        {
            Description = "Expected revision of tasks.xml (omitted auto-resolves current revision)"
        };

        var workspaceRootOption = new Option<string?>("--workspace-root")
        {
            Description = "Explicit path to workspace root or project directory containing .dogdouspec"
        };

        var formatOption = new Option<string?>("--format")
        {
            Description = "Output format (xml or human)"
        };
        formatOption.AcceptOnlyFromAmong("xml", "human");

        cmd.Add(iterationOption);
        cmd.Add(acceptAllOption);
        cmd.Add(summaryOption);
        cmd.Add(actorOption);
        cmd.Add(expectedSpecRevOption);
        cmd.Add(expectedTasksRevOption);
        cmd.Add(workspaceRootOption);
        cmd.Add(formatOption);

        cmd.SetAction(parseResult =>
        {
            var explicitIterId = parseResult.GetValue(iterationOption);
            var acceptAll = parseResult.GetValue(acceptAllOption);
            var summary = parseResult.GetValue(summaryOption) ?? "Iteration completion.";
            var actor = parseResult.GetValue(actorOption) ?? "owner";
            var specRev = parseResult.GetValue(expectedSpecRevOption);
            var tasksRev = parseResult.GetValue(expectedTasksRevOption);
            var workspaceRoot = parseResult.GetValue(workspaceRootOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = WorkspaceCommand.ResolveFormat(formatArg);

            var (discoverSuccess, discoveredRoot, discoverError) = WorkspaceDiscovery.FindWorkspaceRoot(
                workspaceRoot,
                Environment.CurrentDirectory);

            if (!discoverSuccess || discoverError != null)
            {
                var envelope = new DiagnosticsEnvelope("iteration complete", discoverError!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var iterId = ResolveIterationId(explicitIterId, discoveredRoot);
            if (string.IsNullOrWhiteSpace(iterId))
            {
                var envelope = new DiagnosticsEnvelope("iteration complete", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--iteration is required when multiple or zero candidate iterations exist."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var specPath = Path.Combine(discoveredRoot, iterId, "spec.xml");
            var tasksPath = Path.Combine(discoveredRoot, iterId, "tasks.xml");

            if (!File.Exists(specPath))
            {
                var envelope = new DiagnosticsEnvelope("iteration complete", Diagnostic.Error(
                    DiagnosticCodes.DocumentNotFound,
                    $"spec.xml not found for iteration '{iterId}'."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            XDocument specDoc;
            try
            {
                specDoc = XDocument.Load(specPath);
            }
            catch (Exception ex)
            {
                var envelope = new DiagnosticsEnvelope("iteration complete", Diagnostic.Error(
                    DiagnosticCodes.XmlParseError,
                    $"Failed to load spec.xml: {ex.Message}"));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (specRevOk, resolvedSpecRev, specRevErr) = DocumentRevisionResolver.ResolveExpectedRevision(
                discoveredRoot,
                $"{iterId}/spec.xml",
                specRev);
            if (!specRevOk || specRevErr != null)
            {
                var envelope = new DiagnosticsEnvelope("iteration complete", specRevErr!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }
            specRev = resolvedSpecRev;

            var (tasksRevOk, resolvedTasksRev, tasksRevErr) = DocumentRevisionResolver.ResolveExpectedRevision(
                discoveredRoot,
                $"{iterId}/tasks.xml",
                tasksRev);
            if (!tasksRevOk || tasksRevErr != null)
            {
                var envelope = new DiagnosticsEnvelope("iteration complete", tasksRevErr!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }
            tasksRev = resolvedTasksRev;

            var nowUtc = DateTimeOffset.UtcNow;
            var isoTime = nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            var confirmId = $"{nowUtc:yyyyMMddTHHmmssZ}-confirm-complete";

            var acceptance = new StringBuilder();
            var pendingCriteria = specDoc.Descendants("criterion")
                .Where(c => string.Equals((string?)c.Attribute("decision"), "pending", StringComparison.OrdinalIgnoreCase) || c.Attribute("decision") == null)
                .Select(c => (string?)c.Attribute("id"))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();

            if (pendingCriteria.Count > 0)
            {
                acceptance.AppendLine("  <acceptance>");
                foreach (var critId in pendingCriteria)
                {
                    acceptance.AppendLine(CultureInfo.InvariantCulture, $"    <criterion target=\"{critId}\" decision=\"accepted\"/>");
                }
                acceptance.AppendLine("  </acceptance>");
            }

            var confirmXml = $"""
<?xml version="1.0" encoding="utf-8"?>
<iteration-confirmation
  id="{confirmId}"
  iteration="{iterId}"
  action="complete"
  expected_spec_revision="{specRev}"
  expected_tasks_revision="{tasksRev}"
  actor="{actor}"
  decided_at="{isoTime}">
  <summary>{SecurityElement.Escape(summary)}</summary>
{acceptance}</iteration-confirmation>
""";

            var (success, envelopeResult, diagnostics) = IterationConfirmer.Confirm(
                discoveredRoot,
                confirmXml);

            if (!success || diagnostics.Count > 0)
            {
                var diagEnvelope = new DiagnosticsEnvelope("iteration complete", diagnostics);
                Console.Error.Write(diagEnvelope.Format(format));
                return diagEnvelope.GetExitCode();
            }

            if (envelopeResult != null)
            {
                Console.Out.Write(envelopeResult.Format(format));
            }

            return 0;
        });

        return cmd;
    }

    private static Command BuildCriterionCommand()
    {
        var criterionCmd = new Command("criterion", "Author and manage iteration acceptance criteria (mutating)");

        int ExecuteCriterionAction(
            string commandName,
            string? explicitIterId,
            string? text,
            string? targetId,
            bool isAdd,
            int? expectedSpecRev,
            string? workspaceRoot,
            string? formatArg)
        {
            var format = WorkspaceCommand.ResolveFormat(formatArg);

            if (string.IsNullOrWhiteSpace(text))
            {
                var envelope = new DiagnosticsEnvelope(commandName, Diagnostic.Error(
                    DiagnosticCodes.CriterionUndefined,
                    "--text option is required and cannot be blank."));
                Console.Error.Write(envelope.Format(format));
                return 5;
            }

            var (discoverSuccess, discoveredRoot, discoverError) = WorkspaceDiscovery.FindWorkspaceRoot(
                workspaceRoot,
                Environment.CurrentDirectory);

            if (!discoverSuccess || discoverError != null)
            {
                var envelope = new DiagnosticsEnvelope(commandName, discoverError!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var iterId = ResolveIterationId(explicitIterId, discoveredRoot);
            if (string.IsNullOrWhiteSpace(iterId))
            {
                var envelope = new DiagnosticsEnvelope(commandName, Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--iteration is required when multiple or zero candidate iterations exist."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (success, envelopeResult, diagnostics) = isAdd
                ? IterationCriterionAuthor.Add(
                    discoveredRoot,
                    iterId,
                    text,
                    criterionId: targetId,
                    expectedSpecRevision: expectedSpecRev)
                : IterationCriterionAuthor.Define(
                    discoveredRoot,
                    iterId,
                    text,
                    criterionId: targetId,
                    expectedSpecRevision: expectedSpecRev);

            if (!success || diagnostics.Count > 0)
            {
                var diagEnvelope = new DiagnosticsEnvelope(commandName, diagnostics);
                Console.Error.Write(diagEnvelope.Format(format));
                return diagEnvelope.GetExitCode();
            }

            if (envelopeResult != null)
            {
                Console.Out.Write(envelopeResult.Format(format));
            }

            return 0;
        }

        Command BuildSetOrDefineCommand(string cmdName)
        {
            var cmd = new Command(cmdName, "Define or replace an acceptance criterion (mutating)");
            var iterOpt = new Option<string?>("--iteration") { Description = "Iteration identifier (omitted auto-resolves candidate iteration)" };
            var textOpt = new Option<string>("--text") { Description = "Acceptance criterion text", Required = true };
            var critIdOpt = new Option<string?>("--criterion-id") { Description = "Target criterion ID or 1-based index (omitted targets single undefined criterion or single criterion)" };
            var revOpt = new Option<int?>("--expected-spec-revision") { Description = "Expected revision of spec.xml (optimistic concurrency check)" };
            var wsOpt = new Option<string?>("--workspace-root") { Description = "Explicit path to workspace root or project directory containing .dogdouspec" };
            var fmtOpt = new Option<string?>("--format") { Description = "Output format (xml or human)" };
            fmtOpt.AcceptOnlyFromAmong("xml", "human");

            cmd.Add(iterOpt);
            cmd.Add(textOpt);
            cmd.Add(critIdOpt);
            cmd.Add(revOpt);
            cmd.Add(wsOpt);
            cmd.Add(fmtOpt);
            cmd.SetAction(parseResult =>
            {
                return ExecuteCriterionAction(
                    $"iteration criterion {cmdName}",
                    parseResult.GetValue(iterOpt),
                    parseResult.GetValue(textOpt),
                    parseResult.GetValue(critIdOpt),
                    isAdd: false,
                    parseResult.GetValue(revOpt),
                    parseResult.GetValue(wsOpt),
                    parseResult.GetValue(fmtOpt));
            });
            return cmd;
        }

        var setCmd = BuildSetOrDefineCommand("set");
        var defineCmd = BuildSetOrDefineCommand("define");

        // Subcommand: add
        var addCmd = new Command("add", "Add a new acceptance criterion (mutating)");
        var addIterOpt = new Option<string?>("--iteration") { Description = "Iteration identifier (omitted auto-resolves candidate iteration)" };
        var addTextOpt = new Option<string>("--text") { Description = "Acceptance criterion text", Required = true };
        var addCritIdOpt = new Option<string?>("--criterion-id") { Description = "Explicit criterion ID (omitted auto-generates deterministic ID)" };
        var addRevOpt = new Option<int?>("--expected-spec-revision") { Description = "Expected revision of spec.xml (optimistic concurrency check)" };
        var addWsOpt = new Option<string?>("--workspace-root") { Description = "Explicit path to workspace root or project directory containing .dogdouspec" };
        var addFmtOpt = new Option<string?>("--format") { Description = "Output format (xml or human)" };
        addFmtOpt.AcceptOnlyFromAmong("xml", "human");

        addCmd.Add(addIterOpt);
        addCmd.Add(addTextOpt);
        addCmd.Add(addCritIdOpt);
        addCmd.Add(addRevOpt);
        addCmd.Add(addWsOpt);
        addCmd.Add(addFmtOpt);
        addCmd.SetAction(parseResult =>
        {
            return ExecuteCriterionAction(
                "iteration criterion add",
                parseResult.GetValue(addIterOpt),
                parseResult.GetValue(addTextOpt),
                parseResult.GetValue(addCritIdOpt),
                isAdd: true,
                parseResult.GetValue(addRevOpt),
                parseResult.GetValue(addWsOpt),
                parseResult.GetValue(addFmtOpt));
        });

        criterionCmd.Add(setCmd);
        criterionCmd.Add(defineCmd);
        criterionCmd.Add(addCmd);

        return criterionCmd;
    }

    internal static string? ResolveIterationId(string? explicitId, string workspaceRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitId))
            return explicitId;

        var (success, result, _) = IterationLister.List(workspaceRoot);
        if (!success || result == null || result.Iterations.Count == 0)
            return null;

        var active = result.Iterations.Where(i => string.Equals(i.Status, "active", StringComparison.OrdinalIgnoreCase)).ToList();
        if (active.Count == 1)
            return active[0].Id;

        var draft = result.Iterations.Where(i => string.Equals(i.Status, "draft", StringComparison.OrdinalIgnoreCase)).ToList();
        if (draft.Count == 1)
            return draft[0].Id;

        if (result.Iterations.Count == 1)
            return result.Iterations[0].Id;

        return null;
    }
}
