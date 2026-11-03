using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace SharedQuickSearch;

/// <summary>
/// 视频取帧：用程序里自带的精简版 ffmpeg（LGPL，只编进了取帧要用的解码器，见 third_party/ffmpeg），不依赖 Windows 有没有装解码扩展。
///
/// 为什么比 Windows 自己生成快：Windows 会跳到视频 10% 左右的位置解码，网络盘上要来回读很多次；
/// 这里先取第一帧，通常只读文件开头和目录信息，一个视频 0.4–0.7 MB。第一帧是黑场（淡入开头）时，再取 1.5 秒处。
/// 实测（本机 SSD，1080p）：H.264 第一帧读 0.7 MB，HEVC 0.4 MB，MKV 0.6 MB；ProRes 每帧本身就大，约 3 MB。
///
/// - 最多同时 3 个（单独排队，不和图片的缩略图抢）；单个最多 20 秒，超时结束进程。
/// - 只读视频文件，输出走管道，不在共享盘上写任何东西。
/// - 手机竖拍的视频按旋转信息自动转正。
/// 这个类不依赖 WPF：返回 BMP 字节，由调用方解码。
/// </summary>
public static class VideoFrameExtractor
{
    public const string ResourceName = "ffmpeg.exe.gz";
    private static readonly SemaphoreSlim Gate = new(3);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(25);
    private const int MaxOutputBytes = 16 * 1024 * 1024;
    private static readonly object ExtractLock = new();
    private static volatile string? _toolPath;
    private static volatile bool _toolChecked;
    private static long _retryExtractAfterTicks;
    private static int _warming; // 解压失败（磁盘满、文件被占用）：过几分钟再试，不是整次运行都放弃
    // 连续启动失败（公司策略不让运行用户目录里的程序、杀毒软件拦了）：这次运行不再用 ffmpeg，全部交给 Windows
    private static int _startFailures;
    private const int MaxStartFailures = 3;

    /// <summary>测试或便携场景可以直接指定 ffmpeg 的位置（不从程序里解压）。</summary>
    public static string? OverridePath { get; set; }

    public static bool Supports(string extension) => FileTypes.Videos.Contains(extension);

    /// <summary>程序里带了 ffmpeg（编译时 third_party/ffmpeg 里有它），或者指定了位置。</summary>
    /// <summary>
    /// 能不能用。不阻塞：ffmpeg 还在后台解压、核对时返回 false（这几秒的视频先交给 Windows），
    /// 所以界面线程上也可以放心调用。
    /// </summary>
    public static bool IsAvailable => Volatile.Read(ref _startFailures) < MaxStartFailures && ToolPath(wait: false) is not null;

    /// <summary>
    /// 可能能用（还在解压的也算）：后台预热排队时用它，免得开机那几秒里 ffmpeg 还没解压好，整轮预热都跳过视频。
    /// 真正取帧时 ExtractAsync 会再判断，用不了就交给 Windows。
    /// </summary>
    public static bool MayBeAvailable =>
        Volatile.Read(ref _startFailures) < MaxStartFailures
        && (OverridePath is not null || !_toolChecked || _toolPath is not null || Interlocked.Read(ref _retryExtractAfterTicks) != long.MaxValue);

    /// <summary>
    /// 取一帧，缩到长边不超过 maxEdge，返回 BMP 字节；取不到返回 null。
    /// background：后台预热用，进程优先级降到最低，不影响前台。
    /// </summary>
    public static async Task<byte[]?> ExtractAsync(string videoPath, int maxEdge, bool background, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _startFailures) >= MaxStartFailures) return null;
        // 拿 ffmpeg 的位置可能要等后台解压完：放到线程池上等，调用方在界面线程上也不会卡
        if (await Task.Run(() => ToolPath(wait: true)).ConfigureAwait(false) is not { } tool) return null;
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var watch = Stopwatch.StartNew();
        byte[]? result = null;
        try
        {
            result = await ExtractCoreAsync(tool, videoPath, maxEdge, background, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            Gate.Release();
            if (!cancellationToken.IsCancellationRequested)
            {
                if (result is not null) { Interlocked.Increment(ref _ok); Interlocked.Add(ref _okMs, watch.ElapsedMilliseconds); }
                else { Interlocked.Increment(ref _failed); LogFailure(videoPath, watch.ElapsedMilliseconds); }
            }
        }
    }

    private static async Task<byte[]?> ExtractCoreAsync(string tool, string videoPath, int maxEdge, bool background, CancellationToken cancellationToken)
    {
        {
            // 一个视频总共最多占 25 秒，坏文件不会长时间占着名额
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TotalBudget);
            var (first, firstState) = await RunAsync(tool, BuildArguments(videoPath, maxEdge, 0, limitProbe: true), background, budget.Token, cancellationToken).ConfigureAwait(false);
            // 超时说明是网络或文件本身的问题，再试也一样：不重试
            if (firstState == RunState.TimedOut) return null;
            // ffmpeg 很快就报错退出了（探测范围太小、少数文件认不出来）：放开探测再试一次
            if (first is null)
            {
                (first, _) = await RunAsync(tool, BuildArguments(videoPath, maxEdge, 0, limitProbe: false), background, budget.Token, cancellationToken).ConfigureAwait(false);
                if (first is null) return null;
            }
            if (!IsMostlyDark(first)) return first;
            // 第一帧是黑场：取 1.5 秒处；视频不到 1.5 秒取不到，就还用第一帧
            var (later, _) = await RunAsync(tool, BuildArguments(videoPath, maxEdge, 1.5, limitProbe: false), background, budget.Token, cancellationToken).ConfigureAwait(false);
            return later is not null && !IsMostlyDark(later) ? later : first;
        }
    }

    // ================= 诊断：设置 → 缩略图里显示，失败的写进本机日志 =================

    private static long _ok, _okMs, _failed;
    private static volatile string _lastError = "";

    /// <summary>“已就绪 · 成功 12 张，平均 180 ms · 失败 0” 这样的一行。</summary>
    public static string Describe()
    {
        var state = Volatile.Read(ref _startFailures) >= MaxStartFailures ? "启动不了（可能被安全软件或公司策略拦了），视频交给 Windows"
            : OverridePath is not null ? "已就绪（测试）"
            : !_toolChecked ? "正在准备"
            : _toolPath is not null ? "已就绪"
            : Interlocked.Read(ref _retryExtractAfterTicks) == long.MaxValue ? "这个版本没带 ffmpeg，视频交给 Windows"
            : "准备失败，几分钟后再试";
        var ok = Interlocked.Read(ref _ok);
        var failed = Interlocked.Read(ref _failed);
        var average = ok == 0 ? "" : $"，平均 {Interlocked.Read(ref _okMs) / ok} ms";
        return $"{state} · 成功 {ok:N0} 张{average} · 失败 {failed:N0}";
    }

    /// <summary>最近一次失败时 ffmpeg 说了什么（设置里鼠标悬停看）。</summary>
    public static string LastError => _lastError;

    private static void LogFailure(string videoPath, long elapsedMs)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Oldlee", "共享速搜");
            Directory.CreateDirectory(folder);
            var log = Path.Combine(folder, "video-frames.log");
            if (File.Exists(log) && new FileInfo(log).Length > 256 * 1024) File.Delete(log);
            File.AppendAllText(log, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 取帧失败 {elapsedMs} ms：{videoPath}\n  {_lastError}\n");
        }
        catch { }
    }

    /// <summary>ffmpeg 参数（逐个传，不拼命令行，路径里有空格、中文、引号都没问题）。</summary>
    public static IReadOnlyList<string> BuildArguments(string videoPath, int maxEdge, double seekSeconds, bool limitProbe)
    {
        var args = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-threads", "1" };
        // 只读够认出视频格式的量：目录信息在文件里，不用为了“分析”多读几 MB
        if (limitProbe) args.AddRange(new[] { "-probesize", "64k", "-analyzeduration", "0" });
        if (seekSeconds > 0) args.AddRange(new[] { "-ss", seekSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) });
        args.AddRange(new[] { "-i", videoPath, "-map", "0:v:0", "-frames:v", "1", "-an", "-sn", "-dn" });
        var edge = Math.Clamp(maxEdge, 32, 4096);
        args.AddRange(new[] { "-vf", $"scale=w={edge}:h={edge}:force_original_aspect_ratio=decrease:flags=bilinear,format=bgr24" });
        args.AddRange(new[] { "-c:v", "bmp", "-f", "image2pipe", "pipe:1" });
        return args;
    }

    private enum RunState { Ok, Failed, TimedOut }

    /// <param name="budget">这一个视频剩下的时间（含调用方取消）。</param>
    /// <param name="cancellationToken">调用方自己的取消：取消了要往外抛，不当成失败。</param>
    private static async Task<(byte[]? Bmp, RunState State)> RunAsync(string tool, IReadOnlyList<string> arguments, bool background, CancellationToken budget, CancellationToken cancellationToken)
    {
        if (budget.IsCancellationRequested) { cancellationToken.ThrowIfCancellationRequested(); return (null, RunState.TimedOut); }
        var start = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) { Interlocked.Increment(ref _startFailures); return (null, RunState.Failed); }
            Volatile.Write(ref _startFailures, 0);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _startFailures);
            _lastError = "ffmpeg 启动不了：" + ex.Message;
            return (null, RunState.Failed);
        }
        try { if (OperatingSystem.IsWindows()) process.PriorityClass = background ? ProcessPriorityClass.Idle : ProcessPriorityClass.BelowNormal; } catch { }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(budget);
        timeout.CancelAfter(Timeout);
        // 读管道时的取消在 Windows 上不会打断已经开始的读：超时 / 取消时直接结束进程，
        // 管道随之关闭，卡住的读拿到“读完了”自己返回。这样卡在共享盘上的 ffmpeg 一定会被停掉。
        using var killOnTimeout = timeout.Token.Register(() => Kill(process));
        try
        {
            // 错误输出也要读掉，不然缓冲区满了 ffmpeg 会卡住
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            using var output = new MemoryStream();
            var buffer = new byte[64 * 1024];
            var stream = process.StandardOutput.BaseStream;
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > MaxOutputBytes) { Kill(process); return (null, RunState.Failed); }
            }
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var errorText = "";
            try { errorText = await errors.ConfigureAwait(false); } catch { }
            if (timeout.IsCancellationRequested) throw new OperationCanceledException(timeout.Token);
            if (process.ExitCode != 0) _lastError = $"ffmpeg 退出码 {process.ExitCode}：{Shorten(errorText)}";
            var ok = process.ExitCode == 0 && output.Length > 54 && LooksLikeBmp(output.GetBuffer());
            return ok ? (output.ToArray(), RunState.Ok) : (null, RunState.Failed);
        }
        catch (Exception ex) when (ex is OperationCanceledException || timeout.IsCancellationRequested)
        {
            Kill(process);
            cancellationToken.ThrowIfCancellationRequested();
            _lastError = $"超过 {Timeout.TotalSeconds:0} 秒没取到（共享盘慢或文件有问题）";
            return (null, RunState.TimedOut);
        }
        catch
        {
            Kill(process);
            return (null, RunState.Failed);
        }
    }

    private static string Shorten(string text)
    {
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    private static bool LooksLikeBmp(byte[] data) => data.Length > 54 && data[0] == (byte)'B' && data[1] == (byte)'M';

    /// <summary>
    /// 这一帧是不是几乎全黑（淡入开头、片头黑场）。按 BMP 里的像素算平均亮度，每隔几个像素抽一个，很快。
    /// 只认 24 位 BMP（我们自己让 ffmpeg 输出的就是这种）；认不出来当作不黑。
    /// </summary>
    public static bool IsMostlyDark(byte[] bmp, double threshold = 18)
    {
        if (!LooksLikeBmp(bmp) || bmp.Length < 54) return false;
        var offset = BitConverter.ToInt32(bmp, 10);
        var width = BitConverter.ToInt32(bmp, 18);
        var height = Math.Abs(BitConverter.ToInt32(bmp, 22));
        var bits = BitConverter.ToInt16(bmp, 28);
        if (bits != 24 || width <= 0 || height <= 0 || offset < 54) return false;
        var stride = (width * 3 + 3) & ~3;
        if (offset + (long)stride * height > bmp.Length) return false;
        long sum = 0, count = 0;
        var step = Math.Max(1, Math.Min(width, height) / 32);
        for (var y = 0; y < height; y += step)
        {
            var row = offset + y * stride;
            for (var x = 0; x < width; x += step)
            {
                var p = row + x * 3;
                sum += (bmp[p] * 114 + bmp[p + 1] * 587 + bmp[p + 2] * 299) / 1000; // BGR → 亮度
                count++;
            }
        }
        return count > 0 && sum / (double)count < threshold;
    }

    // ================= 自带的 ffmpeg =================

    /// <param name="wait">false：别的线程正在解压时不等，直接返回 null（界面线程用）。</param>
    private static string? ToolPath(bool wait)
    {
        if (OverridePath is { } overridden) return File.Exists(overridden) ? overridden : null;
        if (_toolChecked && (_toolPath is not null || DateTime.UtcNow.Ticks < Interlocked.Read(ref _retryExtractAfterTicks))) return _toolPath;
        // 不等的调用（可能在界面线程上）：绝不自己解压，交给后台去做，这次先当作不能用
        if (!wait)
        {
            // 同一时间只排一个后台任务，滚动时成百上千次调用不会堆出一堆任务
            if (Interlocked.CompareExchange(ref _warming, 1, 0) == 0)
                _ = Task.Run(() => { try { ToolPath(wait: true); } finally { Volatile.Write(ref _warming, 0); } });
            return null;
        }
        var taken = false;
        try
        {
            Monitor.Enter(ExtractLock, ref taken);
            if (_toolChecked && (_toolPath is not null || DateTime.UtcNow.Ticks < Interlocked.Read(ref _retryExtractAfterTicks))) return _toolPath;
            try
            {
                _toolPath = ExtractEmbedded();
                // 程序里根本没带 ffmpeg：不会变，以后不用再看
                if (_toolPath is null) Interlocked.Exchange(ref _retryExtractAfterTicks, long.MaxValue);
            }
            catch
            {
                _toolPath = null;
                Interlocked.Exchange(ref _retryExtractAfterTicks, (DateTime.UtcNow + TimeSpan.FromMinutes(5)).Ticks);
            }
            _toolChecked = true;
            return _toolPath;
        }
        finally { if (taken) Monitor.Exit(ExtractLock); }
    }

    /// <summary>
    /// 把程序里压缩存着的 ffmpeg 解到 %LocalAppData%\Oldlee\共享速搜\tools\ffmpeg-&lt;哈希&gt;.exe。
    /// 文件名带内容哈希：换了版本自然是新文件；已经存在的每次启动重新核对一次哈希，被改过就重新解。
    /// </summary>
    private static string? ExtractEmbedded()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (resource is null) return null; // 编译时没有带 ffmpeg：视频照旧用 Windows 生成
        byte[] data;
        using (var gzip = new GZipStream(resource, CompressionMode.Decompress))
        using (var memory = new MemoryStream())
        {
            gzip.CopyTo(memory);
            data = memory.ToArray();
        }
        var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Oldlee", "共享速搜", "tools");
        var path = Path.Combine(folder, $"ffmpeg-{hash[..12]}.exe");
        if (File.Exists(path))
        {
            try
            {
                using var existing = File.OpenRead(path);
                if (Convert.ToHexString(SHA256.HashData(existing)).ToLowerInvariant() == hash) return path;
            }
            catch { }
        }
        Directory.CreateDirectory(folder);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temporary, data);
        File.Move(temporary, path, overwrite: true);
        // 旧版本解出来的 ffmpeg 删掉
        foreach (var old in Directory.EnumerateFiles(folder, "ffmpeg-*.exe"))
            if (!old.Equals(path, StringComparison.OrdinalIgnoreCase)) try { File.Delete(old); } catch { }
        return path;
    }
}
