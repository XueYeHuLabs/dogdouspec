namespace DogdouSpec.Core.Progression;

/// <summary>
/// Authoritative action categories for progression recommendations.
/// </summary>
public static class ProgressionActionCategories
{
    public const string StartWork = "start-work";
    public const string ContinueWork = "continue-work";
    public const string VerifyWork = "verify-work";
    public const string ReviewRequired = "review-required";
    public const string ResolveFindings = "resolve-findings";
    public const string ResumeTask = "resume-task";
    public const string WaitDependency = "wait-dependency";
    public const string WaitExternal = "wait-external";
    public const string OwnerDecision = "owner-decision";
    public const string NoTasks = "no-tasks";
    public const string ExecutionTerminal = "execution-terminal";
}
