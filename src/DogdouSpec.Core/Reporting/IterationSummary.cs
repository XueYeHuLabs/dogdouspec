namespace DogdouSpec.Core.Reporting;

public sealed record TaskSummaryItem(
    string Id,
    string Title,
    string Status,
    string? Agent,
    IReadOnlyList<string> CoveredCriteria,
    bool IsBlocked,
    string? BlockedReason);

public sealed record BlockerSummaryItem(
    string TaskId,
    string TaskTitle,
    string DependencyTaskId,
    string DependencyStatus);

public sealed record GatingSummaryItem(
    string Kind,
    string Id,
    string Title,
    string Status);

public sealed record IterationSummary(
    string IterationId,
    string Kind,
    string Status,
    int SpecRevision,
    int TasksRevision,
    string Title,
    string Summary,
    int TotalTasks,
    int DoneTasks,
    int InProgressTasks,
    int VerificationTasks,
    int PendingTasks,
    int InactiveTasks,
    double ProgressPercentage,
    IReadOnlyList<TaskSummaryItem> Tasks,
    IReadOnlyList<BlockerSummaryItem> Blockers,
    IReadOnlyList<GatingSummaryItem> PendingGates,
    string RecommendedNextAction);
