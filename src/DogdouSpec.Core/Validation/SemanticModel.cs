using System.Xml.Linq;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Validation;

/// <summary>
/// Represents any element in a managed document bearing an @id attribute.
/// </summary>
public sealed record IndexedObject(
    string Id,
    string ElementName,
    string? ParentElementName,
    ManagedDocument Document,
    int? LineNumber,
    int? LinePosition,
    XElement Element);

/// <summary>
/// Represents a &lt;ref&gt; element in a managed document.
/// </summary>
public sealed record ParsedReference(
    string Scope,
    string Target,
    string Relation,
    ManagedDocument Document,
    string? ContainingObjectId,
    int? LineNumber,
    int? LinePosition,
    XElement Element);

/// <summary>
/// Represents an acceptance criterion in a task or specification.
/// </summary>
public sealed record ParsedCriterion(
    string Id,
    string? Status,
    string? Decision,
    ManagedDocument Document,
    int? LineNumber,
    int? LinePosition,
    XElement Element);

/// <summary>
/// Represents any element in a managed document bearing an @operation_id attribute.
/// </summary>
public sealed record IndexedOperationReceipt(
    string OperationId,
    string? RecordId,
    string ElementName,
    string? ParentElementName,
    ManagedDocument Document,
    string? ContainingTaskId,
    int? LineNumber,
    int? LinePosition,
    XElement Element);

/// <summary>
/// Represents a &lt;record&gt; element inside a task, requirement, decision, etc.
/// </summary>
public sealed record ParsedRecord(
    string Id,
    string Kind,
    string Status,
    string? CreatedAt,
    string? Actor,
    string? OperationId,
    IReadOnlyList<ParsedReference> Covers,
    IReadOnlyList<ParsedReference> Sources,
    ManagedDocument Document,
    int? LineNumber,
    int? LinePosition,
    XElement Element);

/// <summary>
/// Represents a &lt;task&gt; element in tasks.xml.
/// </summary>
public sealed record ParsedTask(
    string Id,
    string Status,
    string? StartedAt,
    string? CompletedAt,
    IReadOnlyList<ParsedCriterion> Criteria,
    IReadOnlyList<ParsedRecord> Records,
    IReadOnlyList<ParsedReference> Dependencies,
    IReadOnlyList<ParsedReference> Origin,
    ManagedDocument Document,
    int? LineNumber,
    int? LinePosition,
    XElement Element);

/// <summary>
/// Represents a targeted decision entry within a confirmation element.
/// </summary>
public sealed record ParsedConfirmationTarget(
    string Target,
    string Decision,
    int? LineNumber,
    int? LinePosition,
    XElement Element)
{
    public void Deconstruct(out string target, out string decision)
    {
        target = Target;
        decision = Decision;
    }
}

/// <summary>
/// Represents a &lt;confirmation&gt; element in spec.xml confirmations.
/// </summary>
public sealed record ParsedConfirmation(
    string Id,
    string Action,
    string Decision,
    string? Actor,
    string? DecidedAt,
    string Summary,
    string? Rationale,
    IReadOnlyList<ParsedConfirmationTarget> Requirements,
    IReadOnlyList<ParsedConfirmationTarget> Questions,
    IReadOnlyList<ParsedConfirmationTarget> DesignDecisions,
    IReadOnlyList<ParsedConfirmationTarget> AcceptanceCriteria,
    ManagedDocument Document,
    int? LineNumber,
    int? LinePosition,
    XElement Element);

/// <summary>
/// Represents a &lt;requirement&gt; element in spec.xml product requirements.
/// </summary>
public sealed record ParsedRequirement(
    string Id,
    string Status,
    ManagedDocument Document,
    int? LineNumber,
    int? LinePosition,
    XElement Element);

/// <summary>
/// Represents a &lt;question&gt; element in spec.xml research questions.
/// </summary>
public sealed record ParsedResearchQuestion(
    string Id,
    string Status,
    ManagedDocument Document,
    int? LineNumber,
    int? LinePosition,
    XElement Element);

/// <summary>
/// Represents a &lt;decision&gt; element in spec.xml design decisions.
/// </summary>
public sealed record ParsedDesignDecision(
    string Id,
    string Status,
    ManagedDocument Document,
    int? LineNumber,
    int? LinePosition,
    XElement Element);

/// <summary>
/// Represents a parsed spec.xml &lt;iteration&gt; document.
/// </summary>
public sealed record ParsedIteration(
    string Id,
    string Kind,
    string Status,
    string? CreatedAt,
    string? UpdatedAt,
    string? CompletedAt,
    bool HasProduct,
    bool HasResearch,
    IReadOnlyList<ParsedRequirement> Requirements,
    IReadOnlyList<ParsedResearchQuestion> Questions,
    IReadOnlyList<ParsedCriterion> AcceptanceCriteria,
    IReadOnlyList<ParsedDesignDecision> DesignDecisions,
    IReadOnlyList<ParsedConfirmation> Confirmations,
    ManagedDocument Document,
    int? LineNumber,
    int? LinePosition,
    XElement Element);

/// <summary>
/// Represents a parsed tasks.xml &lt;tasks&gt; document.
/// </summary>
public sealed record ParsedTasksDocument(
    string Id,
    string IterationAttribute,
    IReadOnlyList<ParsedTask> Tasks,
    ManagedDocument Document,
    int? LineNumber,
    int? LinePosition,
    XElement Element);
