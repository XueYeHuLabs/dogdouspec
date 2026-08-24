namespace DogdouSpec.Core.Transactions;

/// <summary>
/// Disposable lock interface representing an exclusive workspace writer lock.
/// </summary>
public interface IWorkspaceLock : IDisposable
{
    string LockFilePath { get; }
}
