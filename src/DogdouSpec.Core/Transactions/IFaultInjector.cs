namespace DogdouSpec.Core.Transactions;

/// <summary>
/// Execution phases where fault/crash injection can occur for testing atomic recovery.
/// </summary>
public enum FaultPhase
{
    None,
    BeforeStaging,
    AfterStagingBeforeValidation,
    AfterValidationBeforeCommitMarker,
    AfterCommitMarkerBeforePublish,
    DuringMultiFileCommitAfterFirstFile,
    AfterPublishBeforeCleanup
}

/// <summary>
/// Injectable fault/crash simulation hook for write transaction testing.
/// </summary>
public interface IFaultInjector
{
    void InjectFaultIfMatched(FaultPhase phase);
}

/// <summary>
/// Test implementation of IFaultInjector that throws an exception when the targeted phase is reached.
/// </summary>
public sealed class TestFaultInjector : IFaultInjector
{
    private readonly FaultPhase _targetPhase;
    private readonly Exception? _customException;

    public TestFaultInjector(FaultPhase targetPhase, Exception? customException = null)
    {
        _targetPhase = targetPhase;
        _customException = customException;
    }

    public void InjectFaultIfMatched(FaultPhase phase)
    {
        if (phase == _targetPhase)
        {
            throw _customException ?? new IOException($"Simulated crash/interruption at fault injection phase: {phase}");
        }
    }
}
