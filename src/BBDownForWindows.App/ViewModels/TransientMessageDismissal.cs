namespace BBDownForWindows.App.ViewModels;

internal sealed class TransientMessageDismissal(TimeSpan duration)
{
    private CancellationTokenSource? _pending;

    public void Schedule(Action dismiss)
    {
        Cancel();
        var cancellation = new CancellationTokenSource();
        _pending = cancellation;
        _ = DismissAsync(cancellation, dismiss);
    }

    public void Cancel()
    {
        var cancellation = Interlocked.Exchange(ref _pending, null);
        cancellation?.Cancel();
    }

    private async Task DismissAsync(CancellationTokenSource cancellation, Action dismiss)
    {
        try
        {
            await Task.Delay(duration, cancellation.Token);
            if (!ReferenceEquals(Interlocked.CompareExchange(ref _pending, null, cancellation), cancellation)) return;
            dismiss();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }
}
