using System.Globalization;
using System.Text;
using System.Xml;
using DogdouSpec.Core.Formatting;

namespace DogdouSpec.Core.Transactions;

/// <summary>
/// Mutated document entry in a mutation envelope.
/// </summary>
public sealed record MutatedDocument(
    string Path,
    int Revision,
    int? PreviousRevision = null);

public sealed record MutationTransition(string Name, string From, string To, string Document, int PreviousRevision, int Revision);

/// <summary>
/// Mutation result envelope returned by mutating commands (CLI contract Section 3).
/// </summary>
public sealed class MutationEnvelope
{
    public string Command { get; }
    public bool AlreadyApplied { get; }
    public bool DryRun { get; }
    public bool Partial { get; }
    public IReadOnlyList<MutationTransition> Transitions { get; }
    public IReadOnlyList<MutatedDocument> Documents { get; }

    public MutationEnvelope(string command, IReadOnlyList<MutatedDocument> documents, bool alreadyApplied = false,
        bool dryRun = false, IReadOnlyList<MutationTransition>? transitions = null, bool partial = false)
    {
        Command = command ?? string.Empty;
        Documents = documents ?? Array.Empty<MutatedDocument>();
        AlreadyApplied = alreadyApplied;
        DryRun = dryRun;
        Partial = partial;
        Transitions = transitions ?? Array.Empty<MutationTransition>();
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

        using var memoryStream = new MemoryStream();
        using (var writer = XmlWriter.Create(memoryStream, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("mutation");
            writer.WriteAttributeString("command", Command);
            writer.WriteAttributeString("already_applied", AlreadyApplied ? "true" : "false");
            if (DryRun) writer.WriteAttributeString("dry_run", "true");
            if (Partial) writer.WriteAttributeString("partial", "true");

            foreach (var doc in Documents)
            {
                writer.WriteStartElement("document");
                writer.WriteAttributeString("path", doc.Path);
                if (doc.PreviousRevision.HasValue)
                {
                    writer.WriteAttributeString("previous_revision", doc.PreviousRevision.Value.ToString(CultureInfo.InvariantCulture));
                }
                writer.WriteAttributeString("revision", doc.Revision.ToString(CultureInfo.InvariantCulture));
                writer.WriteEndElement();
            }

            foreach (var transition in Transitions)
            {
                writer.WriteStartElement("transition");
                writer.WriteAttributeString("name", transition.Name);
                writer.WriteAttributeString("from", transition.From);
                writer.WriteAttributeString("to", transition.To);
                writer.WriteAttributeString("document", transition.Document);
                writer.WriteAttributeString("previous_revision", transition.PreviousRevision.ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("revision", transition.Revision.ToString(CultureInfo.InvariantCulture));
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(memoryStream.ToArray()) + "\n";
    }

    public string ToHumanString()
    {
        var sb = new StringBuilder();
        var status = AlreadyApplied ? " (already applied)" : string.Empty;
        var heading = DryRun ? "DRY RUN, nothing written" : Partial ? "Partial mutation applied" : "Mutation applied";
        sb.AppendLine(CultureInfo.InvariantCulture, $"{heading} ({Command}){status}:");
        foreach (var doc in Documents)
        {
            if (doc.PreviousRevision.HasValue)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {doc.Path} (revision {doc.Revision}, previous {doc.PreviousRevision.Value})");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {doc.Path} (revision {doc.Revision})");
            }
        }
        foreach (var transition in Transitions)
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {transition.Name}: {transition.From} -> {transition.To} (revision {transition.PreviousRevision} -> {transition.Revision})");
        return sb.ToString();
    }

    public string Format(OutputFormat format) =>
        format == OutputFormat.Xml ? ToXmlString() : ToHumanString();
}
