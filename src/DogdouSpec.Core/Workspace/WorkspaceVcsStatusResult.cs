using System.Globalization;
using System.Text;
using System.Xml;
using DogdouSpec.Core.Formatting;

namespace DogdouSpec.Core.Workspace;

public sealed record WorkspaceVcsFileStatus(
    string RelativePath,
    string Status, // "clean" | "untracked" | "modified" | "staged" | "deleted" | "ignored"
    bool IsAuthoritative);

public sealed class WorkspaceVcsStatusResult
{
    public string WorkspaceRoot { get; }
    public string RepositoryRoot { get; }
    public bool IsGitRepository { get; }
    public bool IsTransportReady { get; }
    public IReadOnlyList<WorkspaceVcsFileStatus> ManagedFiles { get; }
    public IReadOnlyList<string> UncheckpointedFiles { get; }

    public WorkspaceVcsStatusResult(
        string workspaceRoot,
        string repositoryRoot,
        bool isGitRepository,
        bool isTransportReady,
        IReadOnlyList<WorkspaceVcsFileStatus> managedFiles,
        IReadOnlyList<string> uncheckpointedFiles)
    {
        WorkspaceRoot = workspaceRoot;
        RepositoryRoot = repositoryRoot;
        IsGitRepository = isGitRepository;
        IsTransportReady = isTransportReady;
        ManagedFiles = managedFiles ?? Array.Empty<WorkspaceVcsFileStatus>();
        UncheckpointedFiles = uncheckpointedFiles ?? Array.Empty<string>();
    }

    public string ToXmlString()
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
            writer.WriteStartElement("workspace-vcs-status");
            writer.WriteAttributeString("workspace_root", WorkspaceRoot);
            writer.WriteAttributeString("is_git", IsGitRepository ? "true" : "false");
            writer.WriteAttributeString("transport_ready", IsTransportReady ? "true" : "false");
            writer.WriteAttributeString("managed_files_count", ManagedFiles.Count.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("uncheckpointed_count", UncheckpointedFiles.Count.ToString(CultureInfo.InvariantCulture));

            writer.WriteStartElement("files");
            foreach (var file in ManagedFiles)
            {
                writer.WriteStartElement("file");
                writer.WriteAttributeString("path", file.RelativePath);
                writer.WriteAttributeString("status", file.Status);
                writer.WriteAttributeString("authoritative", file.IsAuthoritative ? "true" : "false");
                writer.WriteEndElement();
            }
            writer.WriteEndElement();

            if (UncheckpointedFiles.Count > 0)
            {
                writer.WriteStartElement("uncheckpointed");
                foreach (var uncheck in UncheckpointedFiles)
                {
                    writer.WriteElementString("file", uncheck);
                }
                writer.WriteEndElement();
            }

            writer.WriteEndElement(); // </workspace-vcs-status>
            writer.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    public string ToHumanString()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Workspace VCS Status (Read-Only):");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Workspace Root:    {WorkspaceRoot}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Git Repository:    {(IsGitRepository ? "Yes" : "No")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Transport Ready:   {(IsTransportReady ? "YES (All authoritative files checkpointed)" : "NO (Uncheckpointed authoritative files exist)")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Managed Files:     {ManagedFiles.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Uncheckpointed:    {UncheckpointedFiles.Count}");
        sb.AppendLine();

        if (ManagedFiles.Count > 0)
        {
            sb.AppendLine("Managed Files:");
            foreach (var f in ManagedFiles)
            {
                var tag = f.IsAuthoritative ? "[AUTH]" : "[SUPP]";
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {tag} [{f.Status.ToUpperInvariant(),-10}] {f.RelativePath}");
            }
            sb.AppendLine();
        }

        if (UncheckpointedFiles.Count > 0)
        {
            sb.AppendLine("Uncheckpointed Files (Requires caller Git checkpoint before handoff):");
            foreach (var u in UncheckpointedFiles)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  ! {u}");
            }
        }

        return sb.ToString();
    }

    public string Format(OutputFormat format) =>
        format == OutputFormat.Xml ? ToXmlString() : ToHumanString();
}

public sealed class WorkspaceCheckpointPlanResult
{
    public string WorkspaceRoot { get; }
    public string RepositoryRoot { get; }
    public bool IsGitRepository { get; }
    public bool IsSatisfied { get; }
    public IReadOnlyList<string> UncheckpointedFiles { get; }
    public string RecommendedCommitMessage { get; }

    public WorkspaceCheckpointPlanResult(
        string workspaceRoot,
        string repositoryRoot,
        bool isGitRepository,
        bool isSatisfied,
        IReadOnlyList<string> uncheckpointedFiles,
        string recommendedCommitMessage)
    {
        WorkspaceRoot = workspaceRoot;
        RepositoryRoot = repositoryRoot;
        IsGitRepository = isGitRepository;
        IsSatisfied = isSatisfied;
        UncheckpointedFiles = uncheckpointedFiles ?? Array.Empty<string>();
        RecommendedCommitMessage = recommendedCommitMessage;
    }

    public string ToXmlString()
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
            writer.WriteStartElement("checkpoint-plan");
            writer.WriteAttributeString("workspace_root", WorkspaceRoot);
            writer.WriteAttributeString("satisfied", IsSatisfied ? "true" : "false");
            writer.WriteAttributeString("uncheckpointed_count", UncheckpointedFiles.Count.ToString(CultureInfo.InvariantCulture));

            if (UncheckpointedFiles.Count > 0)
            {
                writer.WriteStartElement("uncheckpointed");
                foreach (var uncheck in UncheckpointedFiles)
                {
                    writer.WriteElementString("file", uncheck);
                }
                writer.WriteEndElement();
            }

            writer.WriteElementString("recommended_message", RecommendedCommitMessage);

            writer.WriteEndElement(); // </checkpoint-plan>
            writer.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    public string ToHumanString()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Workspace Checkpoint Plan (Read-Only Advisory):");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Workspace Root:    {WorkspaceRoot}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Status:            {(IsSatisfied ? "SATISFIED (Workspace is transport-ready)" : "ACTION REQUIRED (Uncheckpointed files exist)")}");
        sb.AppendLine();

        if (UncheckpointedFiles.Count == 0)
        {
            sb.AppendLine("  No uncheckpointed managed documents. Governance state is up to date.");
            return sb.ToString();
        }

        sb.AppendLine("Uncheckpointed Files:");
        foreach (var u in UncheckpointedFiles)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  + {u}");
        }
        sb.AppendLine();

        sb.AppendLine("Recommended Governance Checkpoint Action:");
        sb.AppendLine("  (Execute with repository write authority when ready)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  git add {string.Join(" ", UncheckpointedFiles)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  git commit -m \"{RecommendedCommitMessage}\"");

        return sb.ToString();
    }

    public string Format(OutputFormat format) =>
        format == OutputFormat.Xml ? ToXmlString() : ToHumanString();
}
