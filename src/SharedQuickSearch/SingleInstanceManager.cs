namespace SharedQuickSearch;

public sealed class SingleInstanceManager : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private readonly EventWaitHandle? _exitEvent;
    private readonly CancellationTokenSource _cancel = new();
    public bool IsPrimary { get; }
    public event Action? ShowRequested;
    /// <summary>安装程序升级 / 卸载前请求退出。</summary>
    public event Action? ExitRequested;

    public SingleInstanceManager()
    {
        _mutex = new Mutex(true, "Local\\Oldlee.SharedQuickSearch.Singleton", out var created);
        IsPrimary = created;
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Oldlee.SharedQuickSearch.Show");
        if (IsPrimary)
        {
            _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Setup.InstallerEngine.ExitEventName);
            _ = Task.Run(Listen);
        }
    }

    private void Listen()
    {
        var handles = new WaitHandle[] { _showEvent, _exitEvent! };
        while (!_cancel.IsCancellationRequested)
        {
            var signaled = WaitHandle.WaitAny(handles, 1000);
            if (signaled == 0) ShowRequested?.Invoke();
            else if (signaled == 1) ExitRequested?.Invoke();
        }
    }

    /// <summary>第二次打开软件：把“抢前台”的权限让给已经在运行的那个进程，否则它弹出的搜索条拿不到焦点。</summary>
    public void SignalShow()
    {
        try { AllowSetForegroundWindow(-1); } catch { }
        _showEvent.Set();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    public void Dispose()
    {
        _cancel.Cancel();
        _showEvent.Dispose();
        _exitEvent?.Dispose();
        if (IsPrimary) try { _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
        _cancel.Dispose();
    }
}
