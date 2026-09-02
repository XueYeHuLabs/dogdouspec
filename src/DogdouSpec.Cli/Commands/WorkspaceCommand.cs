using System.CommandLine;
using System.Text;
using System.Xml;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Cli.Commands;

public static class WorkspaceCommand
{
    public static Command BuildCommand()
    {
        var workspaceCmd = new Command("workspace", "Manage authoritative DogdouSpec workspace state; Git checkpointing remains caller-controlled");

        var discoverCmd = BuildDiscoverCommand();
        var initCmd = BuildInitCommand();
        var unlockCmd = BuildUnlockCommand();
        var vcsStatusCmd = BuildVcsStatusCommand();
        var checkpointPlanCmd = BuildCheckpointPlanCommand();

        workspaceCmd.Add(discoverCmd);
        workspaceCmd.Add(initCmd);
        workspaceCmd.Add(unlockCmd);
        workspaceCmd.Add(vcsStatusCmd);
        workspaceCmd.Add(checkpointPlanCmd);

        return workspaceCmd;
    }

    private static Command BuildDiscoverCommand()
    {
        var discoverCmd = new Command("discover", "Discover nearest ancestor .dogdouspec workspace directory");

        var workspaceRootOption = new Option<string?>("--workspace-root")
        {
            Description = "Explicit path to workspace root or project directory containing .dogdouspec"
        };

        var formatOption = new Option<string?>("--format")
        {
            Description = "Output format (xml or human)"
        };
        formatOption.AcceptOnlyFromAmong("xml", "human");

        discoverCmd.Add(workspaceRootOption);
        discoverCmd.Add(formatOption);

        discoverCmd.SetAction(parseResult =>
        {
            var workspaceRoot = parseResult.GetValue(workspaceRootOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = ResolveFormat(formatArg);

            var (success, discoveredRoot, error) = WorkspaceDiscovery.FindWorkspaceRoot(
                workspaceRoot,
                Environment.CurrentDirectory);

            if (!success || error != null)
            {
                var envelope = new DiagnosticsEnvelope("workspace discover", error!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (format == OutputFormat.Xml)
            {
                var xml = FormatWorkspaceXml(discoveredRoot);
                Console.Out.Write(xml);
            }
            else
            {
                Console.Out.WriteLine($"Workspace root: {discoveredRoot}");
            }

            return 0;
        });

        return discoverCmd;
    }

    private static Command BuildInitCommand()
    {
        var initCmd = new Command("init", "Initialize authoritative .dogdouspec state atomically; version managed files in Git-backed governed work");

        var workspaceRootOption = new Option<string?>("--workspace-root")
        {
            Description = "Explicit path where .dogdouspec should be initialized"
        };

        var formatOption = new Option<string?>("--format")
        {
            Description = "Output format (xml or human)"
        };
        formatOption.AcceptOnlyFromAmong("xml", "human");

        initCmd.Add(workspaceRootOption);
        initCmd.Add(formatOption);

        initCmd.SetAction(parseResult =>
        {
            var workspaceRoot = parseResult.GetValue(workspaceRootOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = ResolveFormat(formatArg);

            var (success, initializedRoot, error) = WorkspaceInitializer.Initialize(
                workspaceRoot,
                Environment.CurrentDirectory);

            if (!success || error != null)
            {
                var envelope = new DiagnosticsEnvelope("workspace init", error!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (format == OutputFormat.Xml)
            {
                var xml = FormatWorkspaceInitializedXml(initializedRoot);
                Console.Out.Write(xml);
            }
            else
            {
                var agentSkillDir = Path.Combine(Path.GetDirectoryName(initializedRoot)!, ".agents", "skills", "dogdouspec");
                Console.Out.WriteLine($"Initialized DogdouSpec workspace at: {initializedRoot}");
                Console.Out.WriteLine($"Skill files written to:              {agentSkillDir}");
                Console.Out.WriteLine($".gitignore updated:                  /.dogdouspec/_tmp/ added (if not already present)");
                Console.Out.WriteLine();
                Console.Out.WriteLine("Next steps:");
                Console.Out.WriteLine("  Run 'dogdouspec skill guide' to view setup guidance and AGENTS.md recommendations.");
                Console.Out.WriteLine("  Checkpoint managed .dogdouspec/ files in Git when ready (DogdouSpec never commits automatically).");
            }

            return 0;
        });

        return initCmd;
    }

    private static Command BuildUnlockCommand()
    {
        var unlockCmd = new Command("unlock", "Inspect, recover, and safely release stale workspace locks");

        var forceOption = new Option<bool>("--force")
        {
            Description = "Force release even if lock appears to be held by an active process"
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

        unlockCmd.Add(forceOption);
        unlockCmd.Add(workspaceRootOption);
        unlockCmd.Add(formatOption);

        unlockCmd.SetAction(parseResult =>
        {
            var force = parseResult.GetValue(forceOption);
            var workspaceRoot = parseResult.GetValue(workspaceRootOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = ResolveFormat(formatArg);

            var (success, discoveredRoot, error) = WorkspaceDiscovery.FindWorkspaceRoot(
                workspaceRoot,
                Environment.CurrentDirectory);

            if (!success || error != null)
            {
                var envelope = new DiagnosticsEnvelope("workspace unlock", error!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (released, _, releaseError) = DogdouSpec.Core.Transactions.WorkspaceLock.ReleaseStaleLock(discoveredRoot, force);
            if (!released)
            {
                var envelope = new DiagnosticsEnvelope("workspace unlock", releaseError!);
                Console.Error.Write(envelope.Format(format));
                return releaseError?.Code == DiagnosticCodes.LockConflict ? 1 : 2;
            }

            var (recoverySuccess, recoveryError) = DogdouSpec.Core.Transactions.StartupRecovery.Run(discoveredRoot);
            if (format == OutputFormat.Xml)
            {
                var xml = FormatWorkspaceUnlockedXml(discoveredRoot, force);
                Console.Out.Write(xml);
            }
            else
            {
                if (recoverySuccess && recoveryError == null)
                {
                    Console.Out.WriteLine($"Workspace unlocked and startup recovery completed at: {discoveredRoot}{(force ? " (forced)" : "")}");
                }
                else
                {
                    Console.Out.WriteLine($"Workspace unlocked at: {discoveredRoot}{(force ? " (forced)" : "")}");
                }
            }

            if (!recoverySuccess || recoveryError != null)
            {
                var warningDiag = Diagnostic.Warning(
                    recoveryError?.Code ?? DiagnosticCodes.RecoveryFailed,
                    $"Lock was released, but startup recovery encountered an issue: {recoveryError?.Message}. Run 'dogdouspec validate' to inspect workspace health.");
                var warningEnvelope = new DiagnosticsEnvelope("workspace unlock", warningDiag);
                Console.Error.Write(warningEnvelope.Format(format));
            }

            return 0;
        });

        return unlockCmd;
    }

    private static Command BuildVcsStatusCommand()
    {
        var cmd = new Command("vcs-status", "Inspect tracked, untracked, and modified state of authoritative .dogdouspec files (read-only)");

        var workspaceRootOption = new Option<string?>("--workspace-root")
        {
            Description = "Explicit path to workspace root or project directory containing .dogdouspec"
        };

        var formatOption = new Option<string?>("--format")
        {
            Description = "Output format (xml or human)"
        };
        formatOption.AcceptOnlyFromAmong("xml", "human");

        cmd.Add(workspaceRootOption);
        cmd.Add(formatOption);

        cmd.SetAction(parseResult =>
        {
            var workspaceRoot = parseResult.GetValue(workspaceRootOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = ResolveFormat(formatArg);

            var (success, discoveredRoot, error) = WorkspaceDiscovery.FindWorkspaceRoot(
                workspaceRoot,
                Environment.CurrentDirectory);

            if (!success || error != null)
            {
                var envelope = new DiagnosticsEnvelope("workspace vcs-status", error!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (statusOk, result, diagnostics) = WorkspaceVcsStatus.CheckStatus(discoveredRoot);
            if (!statusOk || diagnostics.Count > 0 || result == null)
            {
                var envelope = new DiagnosticsEnvelope("workspace vcs-status", diagnostics);
                Console.Error.Write(envelope.Format(format));
                return envelope.GetExitCode();
            }

            Console.Out.Write(result.Format(format));
            return 0;
        });

        return cmd;
    }

    private static Command BuildCheckpointPlanCommand()
    {
        var cmd = new Command("checkpoint-plan", "Report uncheckpointed authoritative files and advisory Git commands (read-only)");

        var workspaceRootOption = new Option<string?>("--workspace-root")
        {
            Description = "Explicit path to workspace root or project directory containing .dogdouspec"
        };

        var formatOption = new Option<string?>("--format")
        {
            Description = "Output format (xml or human)"
        };
        formatOption.AcceptOnlyFromAmong("xml", "human");

        cmd.Add(workspaceRootOption);
        cmd.Add(formatOption);

        cmd.SetAction(parseResult =>
        {
            var workspaceRoot = parseResult.GetValue(workspaceRootOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = ResolveFormat(formatArg);

            var (success, discoveredRoot, error) = WorkspaceDiscovery.FindWorkspaceRoot(
                workspaceRoot,
                Environment.CurrentDirectory);

            if (!success || error != null)
            {
                var envelope = new DiagnosticsEnvelope("workspace checkpoint-plan", error!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (planOk, result, diagnostics) = WorkspaceVcsStatus.CreateCheckpointPlan(discoveredRoot);
            if (!planOk || diagnostics.Count > 0 || result == null)
            {
                var envelope = new DiagnosticsEnvelope("workspace checkpoint-plan", diagnostics);
                Console.Error.Write(envelope.Format(format));
                return envelope.GetExitCode();
            }

            Console.Out.Write(result.Format(format));
            return 0;
        });

        return cmd;
    }

    public static OutputFormat ResolveFormat(string? formatArgument)
    {
        if (string.Equals(formatArgument, "xml", StringComparison.OrdinalIgnoreCase)) return OutputFormat.Xml;
        if (string.Equals(formatArgument, "human", StringComparison.OrdinalIgnoreCase)) return OutputFormat.Human;
        if (string.Equals(formatArgument, "markdown", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(formatArgument, "md", StringComparison.OrdinalIgnoreCase)) return OutputFormat.Markdown;
        if (string.Equals(formatArgument, "json", StringComparison.OrdinalIgnoreCase)) return OutputFormat.Json;
        return Console.IsOutputRedirected ? OutputFormat.Xml : OutputFormat.Human;
    }

    private static string FormatWorkspaceXml(string root)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            OmitXmlDeclaration = false,
            Encoding = new UTF8Encoding(false),
            NewLineHandling = NewLineHandling.Replace,
            NewLineChars = "\n"
        };

        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("workspace");
            writer.WriteAttributeString("root", root);
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    private static string FormatWorkspaceInitializedXml(string root)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            OmitXmlDeclaration = false,
            Encoding = new UTF8Encoding(false),
            NewLineHandling = NewLineHandling.Replace,
            NewLineChars = "\n"
        };

        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("workspace");
            writer.WriteAttributeString("initialized", "true");
            writer.WriteAttributeString("root", root);
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    private static string FormatWorkspaceUnlockedXml(string root, bool forced)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            OmitXmlDeclaration = false,
            Encoding = new UTF8Encoding(false),
            NewLineHandling = NewLineHandling.Replace,
            NewLineChars = "\n"
        };

        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("workspace");
            writer.WriteAttributeString("unlocked", "true");
            writer.WriteAttributeString("forced", forced ? "true" : "false");
            writer.WriteAttributeString("root", root);
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }
}
