namespace EmailOperations.Core;

public sealed class SingleRequestGate : IDisposable
{
    private readonly SemaphoreSlim semaphore = new(1, 1);

    public async Task RunAsync(Func<Task> request, CancellationToken token)
    {
        await semaphore.WaitAsync(token);
        try { await request(); }
        finally { semaphore.Release(); }
    }

    public void Dispose() => semaphore.Dispose();
}
