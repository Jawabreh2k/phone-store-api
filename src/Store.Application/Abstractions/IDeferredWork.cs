namespace Store.Application.Abstractions;

/// <summary>
/// Runs work after the request has done its real job. Production queues it on a background service;
/// tests run it immediately so assertions still see the side effect. Failures stay inside the work.
/// </summary>
public interface IDeferredWork
{
    void Run(Func<IServiceProvider, CancellationToken, Task> work);
}
