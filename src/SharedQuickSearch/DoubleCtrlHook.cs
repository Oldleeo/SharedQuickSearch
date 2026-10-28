using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace SharedQuickSearch;

/// <summary>
/// 双击 Ctrl 呼出。Windows 没有“登记双击某个键”的接口，只能装一个低级键盘钩子自己判断：
/// Ctrl 按下又松开（中间没按别的键、按住不超过 300 ms）算“点一下”，两次点之间不超过 350 ms 就算双击。
///
/// 隐私：钩子会经过所有按键，但这里只看是不是 Ctrl，不记录、不保存、不发送任何按键，
/// 也从不拦截按键（每一个都原样交给下一个程序）。默认关闭，在设置里打开。
/// 钩子装在一个专门的后台线程上（自己跑消息循环）：界面线程忙的时候，别的程序打字也不会被拖慢；
/// 回调里只做几次比较，远低于 Windows 的钩子超时（超时的话 Windows 会自动摘掉钩子）。
/// </summary>
public sealed class DoubleCtrlHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105;
    private const int VkControl = 0x11, VkLControl = 0xA2, VkRControl = 0xA3;
    private const long MaxHoldMs = 300;
    private const long MaxGapMs = 350;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct { public uint VkCode, ScanCode, Flags, Time; public IntPtr ExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out Msg message, IntPtr window, uint min, uint max, uint remove);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public int X, Y; }

    private const uint WmQuit = 0x0012;
    private Thread? _thread;
    private uint _threadId;

    private readonly HookProc _callback; // 必须一直引用着，否则被回收后 Windows 调到一个不存在的函数
    private readonly Dispatcher _dispatcher;
    private IntPtr _hook;
    private bool _ctrlDown;
    private long _downAt;
    private long _lastTapAt = long.MinValue / 2;

    public event Action? DoubleTapped;

    public DoubleCtrlHook(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _callback = OnKey;
    }

    /// <summary>钩子线程还活着（包括装得慢、还没装好的那种）：关闭时一定要叫它退出，打开时不再开第二个。</summary>
    public bool IsActive => _thread is { IsAlive: true };

    /// <summary>打开 / 关闭。打开失败（比如被安全软件拦了）返回 false。</summary>
    public bool SetEnabled(bool enabled)
    {
        if (enabled == IsActive) return true;
        if (!enabled)
        {
            // 让钩子线程自己摘掉钩子、结束消息循环；确认线程真的退出了才忘掉它
            var stoppingThread = _thread;
            if (stoppingThread is null) return true;
            for (var attempt = 0; attempt < 10 && stoppingThread.IsAlive; attempt++)
            {
                if (_threadId != 0) PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
                stoppingThread.Join(200);
            }
            if (stoppingThread.IsAlive) return false;
            _thread = null;
            _threadId = 0;
            return true;
        }

        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var installed = false;
        var thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            // 先让这个线程有消息队列：不然刚打开就关掉时，WM_QUIT 发不进来，钩子会一直挂着
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            _hook = SetWindowsHookEx(WhKeyboardLl, _callback, GetModuleHandle(null), 0);
            installed = _hook != IntPtr.Zero;
            ready.TrySetResult(installed);
            if (!installed) return;
            // 低级钩子的回调靠这个线程的消息循环送进来
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }) { IsBackground = true, Name = "双击 Ctrl", Priority = ThreadPriority.AboveNormal };
        _thread = thread; // 先记下：就算 2 秒内没装好、之后才装上，关闭时也能找到它
        thread.Start();
        ready.Task.Wait(TimeSpan.FromSeconds(2));
        if (installed) return true;
        if (!thread.IsAlive) { _thread = null; _threadId = 0; }
        return false;
    }

    private IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0)
            {
                var message = (int)wParam;
                var vk = (int)Marshal.PtrToStructure<KbdLlHookStruct>(lParam).VkCode;
                var isCtrl = vk is VkControl or VkLControl or VkRControl;
                var now = Environment.TickCount64;
                if (message is WmKeyDown or WmSysKeyDown)
                {
                    if (!isCtrl) { _ctrlDown = false; _lastTapAt = long.MinValue / 2; } // Ctrl+C 之类的组合键不算
                    else if (!_ctrlDown) { _ctrlDown = true; _downAt = now; }           // 按住时的自动重复不重新计时
                }
                else if ((message is WmKeyUp or WmSysKeyUp) && isCtrl && _ctrlDown)
                {
                    _ctrlDown = false;
                    if (now - _downAt <= MaxHoldMs)
                    {
                        if (now - _lastTapAt <= MaxGapMs + MaxHoldMs)
                        {
                            _lastTapAt = long.MinValue / 2;
                            _dispatcher.BeginInvoke(() => DoubleTapped?.Invoke());
                        }
                        else _lastTapAt = now;
                    }
                    else _lastTapAt = long.MinValue / 2;
                }
            }
        }
        catch { }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose() => SetEnabled(false);
}
