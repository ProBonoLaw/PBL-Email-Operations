using EmailOperations.Core;
using Xunit;

namespace EmailOperations.Tests;

public class SingleRequestGateTests
{
    [Fact]
    public async Task Second_request_waits_until_first_finishes()
    {
        using var gate = new SingleRequestGate();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = gate.RunAsync(async () =>
        {
            firstStarted.SetResult();
            await finishFirst.Task;
        }, default);
        await firstStarted.Task;
        var second = gate.RunAsync(() =>
        {
            secondStarted.SetResult();
            return Task.CompletedTask;
        }, default);

        Assert.False(secondStarted.Task.IsCompleted);
        finishFirst.SetResult();
        await Task.WhenAll(first, second);
        Assert.True(secondStarted.Task.IsCompletedSuccessfully);
    }
}
