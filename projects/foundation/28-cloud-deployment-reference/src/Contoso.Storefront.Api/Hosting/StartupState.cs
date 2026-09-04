namespace Contoso.Storefront.Api.Hosting;

public enum ApplicationStartupStatus
{
    Starting,
    Ready,
    Failed
}

public sealed class StartupState
{
    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _status = (int)ApplicationStartupStatus.Starting;

    public ApplicationStartupStatus Status =>
        (ApplicationStartupStatus)Volatile.Read(ref _status);
    public string Detail { get; private set; } = "Startup dependency checks have not completed.";

    public void MarkReady(string detail)
    {
        Detail = detail;
        Volatile.Write(ref _status, (int)ApplicationStartupStatus.Ready);
        _ready.TrySetResult();
    }

    public void MarkFailed(string detail)
    {
        Detail = detail;
        Volatile.Write(ref _status, (int)ApplicationStartupStatus.Failed);
        _ready.TrySetCanceled();
    }

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken) =>
        _ready.Task.WaitAsync(cancellationToken);
}
