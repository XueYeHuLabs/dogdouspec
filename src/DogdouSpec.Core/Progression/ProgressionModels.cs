using DogdouSpec.Core.Validation;

namespace DogdouSpec.Core.Progression;

public sealed record ProgressionDocumentRevision(
    string DocumentPath,
    int Revision);

public sealed record ProgressionFactSummary(
    int TotalTasks,
    int PendingTasks,
    int InProgressTasks,
    int VerificationTasks,
    int BlockedTasks,
    int DoneTasks,
    int CancelledTasks,
    int TransferredTasks,
    int SupersededTasks,
    int TerminalTasks,
    int NonTerminalTasks,
    int EligibleTasks,
    double CompletionPercentage,
    bool IsAllTerminal,
    bool IsDeliverySuccessful,
    bool IsTerminalIncomplete,
    int ActiveFindingsCount,
    int UnresolvedBlockersCount)
{
    public int InactiveTasks => CancelledTasks + TransferredTasks + SupersededTasks;
}

public sealed record ProgressionRecommendation(
    string ActionCategory,
    string ReasonCode,
    string Reason,
    string? TargetTaskId,
    string? TargetBlockingObject,
    string RequiredRole,
    string? FollowUpCommand,
    string? FollowUpLocator);

public sealed record ProgressionTaskCandidate(
    string TaskId,
    string Title,
    string Status,
    string? Agent,
    int Index,
    string ActionCategory,
    string ReasonCode,
    string Reason,
    string RequiredRole,
    IReadOnlyList<string> BlockingReasons,
    bool IsActionable);

public sealed record ProgressionAssessmentResult(
    string IterationId,
    string IterationStatus,
    int SpecRevision,
    int TasksRevision,
    IReadOnlyList<ProgressionDocumentRevision> DocumentRevisions,
    ProgressionFactSummary Facts,
    ProgressionRecommendation RecommendedAction,
    IReadOnlyList<ProgressionTaskCandidate> ActionableCandidates,
    IReadOnlyList<ProgressionTaskCandidate> AllTaskCandidates,
    bool IsExecutionTerminal,
    bool IsProductConfirmed,
    ParsedTask? PrimaryTask = null)
{
    public bool HasActionableCandidate => ActionableCandidates.Count > 0;
}
