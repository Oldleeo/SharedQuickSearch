using System.Collections.Concurrent;
using System.IO;

namespace SharedQuickSearch;

/// <summary>
/// 实时监听共享盘变化，650ms 去抖后批量交给索引。
/// 缓冲区开到 64 KB（默认 8 KB，大批量拷贝时很容易溢出丢事件）；
/// 一旦溢出或网络断开，触发 NeedsReconcile，由主窗口安排一次快速校准补齐。
/// </summary>
public sealed class IndexMonitorService : IDisposable
{
    private readonly FileIndexService _index;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly ConcurrentDictionary<string, byte> _changed = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Timer _debounce;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private IReadOnlyCollection<string> _exclusions = Array.Empty<string>();
    private readonly ConcurrentDictionary<string, string> _rootStates = new(StringComparer.OrdinalIgnoreCase);

    public event Action<int>? IndexChanged;
    /// <summary>监听丢了事件（缓冲溢出 / 网络中断），需要校准。参数是出问题的根目录。</summary>
    public event Action<string>? NeedsReconcile;
    public IReadOnlyDictionary<string, string> RootStates => _rootStates;

    public IndexMonitorService(FileIndexService index)
    {
        _index = index;
        _debounce = new System.Threading.Timer(_ => _ = FlushAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start(IEnumerable<string> roots, IReadOnlyCollection<string> exclusions)
    {
        lock (_gate) StartCore(roots, exclusions);
    }

    private void StartCore(IEnumerable<string> roots, IReadOnlyCollection<string> exclusions)
    {
        StopCore();
        _exclusions = exclusions;
        foreach (var root in PathRules.NormalizeDistinct(roots))
        {
            if (!Directory.Exists(root)) { _rootStates[root] = "离线（使用快照）"; continue; }
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite
                };
                watcher.Created += OnChanged;
                watcher.Changed += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += (_, e) => { Queue(e.OldFullPath); Queue(e.FullPath); };
                var capturedRoot = root;
                watcher.Error += (_, _) =>
                {
                    _rootStates[capturedRoot] = "监听中断，等待校准";
                    NeedsReconcile?.Invoke(capturedRoot);
                };
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
                _rootStates[root] = "实时监听中";
            }
            catch { _rootStates[root] = "无法监听（使用定时校准）"; }
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => Queue(e.FullPath);

    private void Queue(string path)
    {
        if (!PathRules.IsExcluded(path, _exclusions)) _changed[PathRules.Normalize(path)] = 0;
        _debounce.Change(650, Timeout.Infinite);
    }

    private async Task FlushAsync()
    {
        // 同一时间只跑一批；上一批没处理完时新来的变化留到下一批
        if (!await _flushGate.WaitAsync(0).ConfigureAwait(false)) { _debounce.Change(650, Timeout.Infinite); return; }
        try
        {
            var paths = _changed.Keys.ToArray();
            foreach (var path in paths) _changed.TryRemove(path, out _);
            if (paths.Length == 0) return;
            var changed = await _index.ApplyChangesAsync(paths, _exclusions).ConfigureAwait(false);
            if (changed > 0) IndexChanged?.Invoke(changed);
        }
        catch { }
        finally { _flushGate.Release(); }
    }

    public void Stop() { lock (_gate) StopCore(); }

    private void StopCore()
    {
        foreach (var watcher in _watchers)
        {
            try { watcher.EnableRaisingEvents = false; watcher.Dispose(); } catch { }
        }
        _watchers.Clear();
        _rootStates.Clear();
    }

    public void Dispose()
    {
        Stop();
        _debounce.Dispose();
    }
}
