namespace DogdouSpec.Core.Diagnostics;

/// <summary>
/// Machine-readable stable diagnostic codes.
/// </summary>
public static class DiagnosticCodes
{
    public const string WorkspaceNotFound = "WORKSPACE_NOT_FOUND";
    public const string WorkspaceAlreadyExists = "WORKSPACE_ALREADY_EXISTS";
    public const string ManagedStateExists = "MANAGED_STATE_EXISTS";
    public const string DocumentNotFound = "DOCUMENT_NOT_FOUND";
    public const string IterationNotFound = "ITERATION_NOT_FOUND";
    public const string ResourceNotFound = "RESOURCE_NOT_FOUND";
    public const string UnsupportedVersion = "UNSUPPORTED_VERSION";
    public const string InvalidArgument = "INVALID_ARGUMENT";
    public const string InvalidPath = "INVALID_PATH";
    public const string PathTraversalDetected = "PATH_TRAVERSAL_DETECTED";
    public const string PathEscapeDetected = "PATH_ESCAPE_DETECTED";
    public const string DtdProhibited = "DTD_PROHIBITED";
    public const string XmlParseError = "XML_PARSE_ERROR";
    public const string SchemaValidationError = "SCHEMA_VALIDATION_ERROR";
    public const string SchemaNotFound = "SCHEMA_NOT_FOUND";
    public const string UnknownDocumentType = "UNKNOWN_DOCUMENT_TYPE";
    public const string LimitExceeded = "LIMIT_EXCEEDED";
    public const string InitializationFailed = "INITIALIZATION_FAILED";

    // Semantic validation diagnostic codes
    // Identity and document ownership
    public const string DuplicateId = "DUPLICATE_ID";
    public const string InvalidIdGrammar = "INVALID_ID_GRAMMAR";
    public const string IterationIdMismatch = "ITERATION_ID_MISMATCH";
    public const string TasksIterationMismatch = "TASKS_ITERATION_MISMATCH";
    public const string WorkKindMismatch = "WORK_KIND_MISMATCH";

    // References
    public const string DanglingReference = "DANGLING_REFERENCE";
    public const string AmbiguousReference = "AMBIGUOUS_REFERENCE";
    public const string ReferenceScopeViolation = "REFERENCE_SCOPE_VIOLATION";
    public const string ReferenceScopeNotNarrowest = "REFERENCE_SCOPE_NOT_NARROWEST";
    public const string InvalidReferenceTargetType = "INVALID_REFERENCE_TARGET_TYPE";

    // Confirmation targets
    public const string ContradictoryConfirmationDecision = "CONTRADICTORY_CONFIRMATION_DECISION";
    public const string DuplicateConfirmationTarget = "DUPLICATE_CONFIRMATION_TARGET";

    // Scoped context validation
    public const string SemanticContextIncomplete = "SEMANTIC_CONTEXT_INCOMPLETE";

    // Task graph and terminal predicates
    public const string DependencyCycle = "DEPENDENCY_CYCLE";
    public const string TaskCriterionNotTerminal = "TASK_CRITERION_NOT_TERMINAL";
    public const string TaskCompletedAtMissing = "TASK_COMPLETED_AT_MISSING";
    public const string TaskCompletionRecordMissing = "TASK_COMPLETION_RECORD_MISSING";
    public const string TaskCriterionNotCovered = "TASK_CRITERION_NOT_COVERED";
    public const string TaskActiveFindingBlocksCompletion = "TASK_ACTIVE_FINDING_BLOCKS_COMPLETION";
    public const string TaskNonDoneHasCompletedAt = "TASK_NON_DONE_HAS_COMPLETED_AT";
    public const string TaskPendingHasStartedAt = "TASK_PENDING_HAS_STARTED_AT";
    public const string TaskTransitionConflict = "TASK_TRANSITION_CONFLICT";
    public const string TaskImmutable = "TASK_IMMUTABLE";
    public const string TaskRevisionNotAllowed = "TASK_REVISION_NOT_ALLOWED";
    public const string TaskReviewRequired = "TASK_REVIEW_REQUIRED";
    public const string TaskReviewActorConflict = "TASK_REVIEW_ACTOR_CONFLICT";
    public const string TaskReviewImplementerUnknown = "TASK_REVIEW_IMPLEMENTER_UNKNOWN";
    public const string TaskReviewStateInvalid = "TASK_REVIEW_STATE_INVALID";
    public const string IterationReplanningExecutionFrozen = "ITERATION_REPLANNING_EXECUTION_FROZEN";

    // Protected product state provenance and completion
    public const string MissingConfirmationProvenance = "MISSING_CONFIRMATION_PROVENANCE";
    public const string IterationCompletionPredicateFailed = "ITERATION_COMPLETION_PREDICATE_FAILED";
    public const string IterationCompletedAtMissing = "ITERATION_COMPLETED_AT_MISSING";
    public const string WaiverRationaleMissing = "WAIVER_RATIONALE_MISSING";
    public const string OwnerDecisionRequired = "OWNER_DECISION_REQUIRED";
    public const string RequirementSuccessorMissing = "REQUIREMENT_SUCCESSOR_MISSING";
    public const string ChangeApplicationInvalid = "CHANGE_APPLICATION_INVALID";

    // Atomic write, locking, and recovery
    public const string LockConflict = "LOCK_CONFLICT";
    public const string RevisionConflict = "REVISION_CONFLICT";
    public const string CardinalityConflict = "CARDINALITY_CONFLICT";
    public const string IdempotencyConflict = "IDEMPOTENCY_CONFLICT";
    public const string IterationAlreadyExists = "ITERATION_ALREADY_EXISTS";
    public const string FilesystemError = "FILESYSTEM_ERROR";
    public const string RecoveryFailed = "RECOVERY_FAILED";
    public const string CommitFailed = "COMMIT_FAILED";
}
