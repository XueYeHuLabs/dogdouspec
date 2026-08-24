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
        var workspaceCmd = new Command("workspace", "Manage and inspect DogdouSpec workspaces");

        var discoverCmd = BuildDiscoverCommand();
        var initCmd = BuildInitCommand();

        workspaceCmd.Add(discoverCmd);
        workspaceCmd.Add(initCmd);

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
        var initCmd = new Command("init", "Initialize a new .dogdouspec workspace atomically");

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
                Console.Out.WriteLine($"Initialized DogdouSpec workspace at: {initializedRoot}");
            }

            return 0;
        });

        return initCmd;
    }

    public static OutputFormat ResolveFormat(string? formatArgument)
    {
        if (string.Equals(formatArgument, "xml", StringComparison.OrdinalIgnoreCase)) return OutputFormat.Xml;
        if (string.Equals(formatArgument, "human", StringComparison.OrdinalIgnoreCase)) return OutputFormat.Human;
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
}
