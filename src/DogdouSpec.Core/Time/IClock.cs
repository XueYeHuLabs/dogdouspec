namespace DogdouSpec.Core.Time;

/// <summary>
/// Clock interface for obtaining current UTC time.
/// </summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

/// <summary>
/// Default system wall clock.
/// </summary>
public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>
/// Injectable deterministic clock for unit and integration testing.
/// </summary>
public sealed class TestClock : IClock
{
    public DateTime CurrentTime { get; set; }

    public TestClock(DateTime initialTime)
    {
        CurrentTime = initialTime;
    }

    public DateTime UtcNow => CurrentTime;

    public void Advance(TimeSpan delta)
    {
        CurrentTime = CurrentTime.Add(delta);
    }
}
