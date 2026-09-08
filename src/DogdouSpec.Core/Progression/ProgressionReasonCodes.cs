namespace DogdouSpec.Core.Progression;

/// <summary>
/// Authoritative reason codes for progression facts and decisions.
/// </summary>
public static class ProgressionReasonCodes
{
    public const string ActiveTaskInProgress = "ACTIVE_TASK_IN_PROGRESS";
    public const string ActiveTaskVerification = "ACTIVE_TASK_VERIFICATION";
    public const string ActiveTaskReviewRequired = "ACTIVE_TASK_REVIEW_REQUIRED";
    public const string ActiveTaskReviewChangesRequested = "ACTIVE_TASK_REVIEW_CHANGES_REQUESTED";
    public const string ActiveTaskActiveFindings = "ACTIVE_TASK_ACTIVE_FINDINGS";
    public const string PendingTaskReady = "PENDING_TASK_READY";
    public const string PendingTaskDependenciesUnsatisfied = "PENDING_TASK_DEPENDENCIES_UNSATISFIED";
    public const string DependencyCycleDetected = "DEPENDENCY_CYCLE_DETECTED";
    public const string DependencyMissingTarget = "DEPENDENCY_MISSING_TARGET";
    public const string RequirementNotApproved = "REQUIREMENT_NOT_APPROVED";
    public const string TasksBlocked = "TASKS_BLOCKED";
    public const string TasksTerminalIncomplete = "TASKS_TERMINAL_INCOMPLETE";
    public const string AllTasksDone = "ALL_TASKS_DONE";
    public const string IterationDraft = "ITERATION_DRAFT";
    public const string IterationReplanning = "ITERATION_REPLANNING";
    public const string IterationCompleted = "ITERATION_COMPLETED";
    public const string IterationSuperseded = "ITERATION_SUPERSEDED";
    public const string IterationCancelled = "ITERATION_CANCELLED";
    public const string NoTasks = "NO_TASKS";
}
