using System.Globalization;
using System.Text;
using System.Xml;
using DogdouSpec.Core.Formatting;

namespace DogdouSpec.Core.Iterations;

/// <summary>
/// Technical check result within a readiness assessment.
/// </summary>
public sealed record ReadinessTechnicalCheck(
    string Name,
    string Result,
    string? Message = null);

/// <summary>
/// Pending product decisions summary within a readiness assessment.
/// </summary>
public sealed record ReadinessProductDecisions(
    int PendingRequirements,
    int PendingDesignDecisions,
    int PendingAcceptanceCriteria,
    int PendingQuestions)
{
    public int Total => PendingRequirements + PendingDesignDecisions + PendingAcceptanceCriteria + PendingQuestions;
}

/// <summary>
/// Required owner confirmation action indicated by a readiness assessment.
/// </summary>
public sealed record ReadinessRequiredAction(
    string Action,
    string Command = "iteration confirm",
    string Actor = "owner");

/// <summary>
/// Result container for read-only iteration readiness assessment.
/// </summary>
public sealed class IterationReadinessResult
{
    public string IterationId { get; }
    public string Phase { get; }
    public int SpecRevision { get; }
    public int TasksRevision { get; }
    public bool TechnicallyReady { get; }
    public bool OwnerConfirmationRequired { get; }
    public IReadOnlyList<ReadinessTechnicalCheck> TechnicalChecks { get; }
    public ReadinessProductDecisions ProductDecisions { get; }
    public ReadinessRequiredAction RequiredAction { get; }

    public IterationReadinessResult(
        string iterationId,
        string phase,
        int specRevision,
        int tasksRevision,
        bool technicallyReady,
        bool ownerConfirmationRequired,
        IReadOnlyList<ReadinessTechnicalCheck> technicalChecks,
        ReadinessProductDecisions productDecisions,
        ReadinessRequiredAction requiredAction)
    {
        IterationId = iterationId ?? string.Empty;
        Phase = phase ?? string.Empty;
        SpecRevision = specRevision;
        TasksRevision = tasksRevision;
        TechnicallyReady = technicallyReady;
        OwnerConfirmationRequired = ownerConfirmationRequired;
        TechnicalChecks = technicalChecks ?? Array.Empty<ReadinessTechnicalCheck>();
        ProductDecisions = productDecisions;
        RequiredAction = requiredAction;
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
            writer.WriteStartElement("readiness");
            writer.WriteAttributeString("iteration", IterationId);
            writer.WriteAttributeString("phase", Phase);
            writer.WriteAttributeString("spec_revision", SpecRevision.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("tasks_revision", TasksRevision.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("technically_ready", TechnicallyReady ? "true" : "false");
            writer.WriteAttributeString("owner_confirmation_required", OwnerConfirmationRequired ? "true" : "false");

            writer.WriteStartElement("technical");
            foreach (var check in TechnicalChecks)
            {
                writer.WriteStartElement("check");
                writer.WriteAttributeString("name", check.Name);
                writer.WriteAttributeString("result", check.Result);
                if (!string.IsNullOrWhiteSpace(check.Message))
                {
                    writer.WriteString(check.Message);
                }
                writer.WriteEndElement();
            }
            writer.WriteEndElement(); // </technical>

            writer.WriteStartElement("product");
            writer.WriteAttributeString("pending_requirements", ProductDecisions.PendingRequirements.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("pending_design_decisions", ProductDecisions.PendingDesignDecisions.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("pending_acceptance_criteria", ProductDecisions.PendingAcceptanceCriteria.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("pending_questions", ProductDecisions.PendingQuestions.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement(); // </product>

            writer.WriteStartElement("required_action");
            writer.WriteAttributeString("action", RequiredAction.Action);
            writer.WriteAttributeString("command", RequiredAction.Command);
            writer.WriteEndElement(); // </required_action>

            writer.WriteEndElement(); // </readiness>
            writer.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    public string ToHumanString()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Iteration Readiness: {IterationId} (Phase: {Phase})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Spec Revision: {SpecRevision}, Tasks Revision: {TasksRevision}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Technically Ready: {(TechnicallyReady ? "true" : "false")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Owner Confirmation Required: {(OwnerConfirmationRequired ? "true" : "false")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Required Confirm Action: {RequiredAction.Action} (via '{RequiredAction.Command}')");

        sb.AppendLine();
        sb.AppendLine("Technical Checks:");
        if (TechnicalChecks.Count == 0)
        {
            sb.AppendLine("  (No technical checks recorded)");
        }
        else
        {
            foreach (var check in TechnicalChecks)
            {
                var prefix = check.Result.ToUpperInvariant();
                var msg = string.IsNullOrWhiteSpace(check.Message) ? string.Empty : $": {check.Message}";
                sb.AppendLine(CultureInfo.InvariantCulture, $"  [{prefix}] {check.Name}{msg}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Pending Product Decisions:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  - Requirements pending: {ProductDecisions.PendingRequirements}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  - Design decisions pending: {ProductDecisions.PendingDesignDecisions}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  - Acceptance criteria pending: {ProductDecisions.PendingAcceptanceCriteria}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  - Research questions pending: {ProductDecisions.PendingQuestions}");

        return sb.ToString();
    }

    public string Format(OutputFormat format) =>
        format == OutputFormat.Xml ? ToXmlString() : ToHumanString();
}
