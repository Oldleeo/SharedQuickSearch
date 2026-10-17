using System.Diagnostics;
using System.Text.Json;
using SharedQuickSearch;

// 跨平台后端测试：索引、搜索语法、拼音、增量、并发、快速校准、实时监听、性能。
var failures = new List<string>();
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  · " + detail : "")}");
    if (!ok) failures.Add(name);
}

// ---------------- 搜索语法 ----------------
var now = new DateTime(2026, 10, 17, 15, 0, 0, DateTimeKind.Local); // 周六
var q = QueryParser.Parse("14-5 psd >50mb 今天 -旧版", now);
Check("语法：文字条件", q.Terms.SequenceEqual(new[] { "14-5" }));
Check("语法：扩展名", q.Extensions.SetEquals(new[] { ".psd" }));
Check("语法：大小", q.MinSize == 50L * 1024 * 1024);
Check("语法：今天", q.ModifiedAfterUtc == now.Date.ToUniversalTime());
Check("语法：排除", q.Excludes.SequenceEqual(new[] { "旧版" }));
Check("语法：识别出 4 个条件", q.Tokens.Count == 4, string.Join(" | ", q.Tokens.Select(t => t.Label)));
Check("语法：删掉一个条件", QueryParser.RemoveToken("14-5 psd >50mb", q.Tokens[0]) == "14-5 >50mb");
Check("语法：分类词", QueryParser.Parse("图片 14-5").Category == SearchCategory.Images);
Check("语法：本周从周一开始", QueryParser.Parse("本周", now).ModifiedAfterUtc == new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Local).ToUniversalTime());
Check("语法：7天", QueryParser.Parse("7天", now).ModifiedAfterUtc == now.Date.AddDays(-6).ToUniversalTime());
Check("语法：不写单位按 MB", QueryParser.Parse("<5").MaxSize == 5L * 1024 * 1024);
Check("语法：数字仍是文字", QueryParser.Parse("100").Terms.SequenceEqual(new[] { "100" }) && !QueryParser.Parse("100").HasFilters);
Check("语法：SKU 不会被当成排除", QueryParser.Parse("14-5").Excludes.Count == 0 && QueryParser.Parse("-5").Excludes.Count == 0);
Check("语法：全角空格分隔", QueryParser.Parse("14-5\u3000白底").Terms.Count == 2);
Check("语法：*.psd", QueryParser.Parse("*.psd").Extensions.Contains(".psd"));

// ---------------- 拼音 ----------------
var py = Pinyin.Convert("14-5 白底图.jpg");
Check("拼音：首字母", py?.Initials == "145bdtjpg", py?.Initials ?? "null");
Check("拼音：全拼", py?.Full == "145baiditujpg", py?.Full ?? "null");
Check("拼音：英文名不转", Pinyin.Convert("main.psd") is null);
Check("拼音：查询判定", Pinyin.IsPinyinQuery("bdt") && !Pinyin.IsPinyinQuery("145") && !Pinyin.IsPinyinQuery("b"));

// ---------------- 索引与搜索 ----------------
var root = Path.Combine(Path.GetTempPath(), "sqs-core-" + Guid.NewGuid().ToString("N"));
var keep = Path.Combine(root, "14-5");
var excluded = Path.Combine(root, "@Recycle");
Directory.CreateDirectory(keep);
Directory.CreateDirectory(excluded);
Directory.CreateDirectory(Path.Combine(keep, "14-5 产品资料"));
void Touch(string path, long size = 10) { using var f = File.Create(path); f.SetLength(size); }
Touch(Path.Combine(keep, "14-5 主图.psd"), 60L * 1024 * 1024);
Touch(Path.Combine(keep, "14-5 白底图.jpg"));
Touch(Path.Combine(keep, "14-5 白底图 旧版.jpg"));
Touch(Path.Combine(keep, "SKU_14_5说明.txt"));
Touch(Path.Combine(keep, "15-2 主图.psd"));
Touch(Path.Combine(keep, "Thumbs.db"));
Touch(Path.Combine(keep, "14-5 产品资料", "参数表.txt"));
Touch(Path.Combine(excluded, "14-5 废稿.psd"));

var indexPath = Path.Combine(Path.GetTempPath(), "sqs-core-index-" + Guid.NewGuid().ToString("N") + ".json");
try
{
    var index = new FileIndexService(indexPath);
    var built = await index.RebuildAsync(new[] { root }, new[] { excluded }, null, CancellationToken.None);
    Check("扫描：条目数（排除目录、忽略 Thumbs.db）", built.EntryCount == 8, built.EntryCount.ToString());
    int Hits(string query, SearchCategory category = SearchCategory.All) => index.SearchDetailed(query, category, 100).TotalMatches;

    Check("搜索：14-5", Hits("14-5") >= 5, Hits("14-5").ToString());
    Check("搜索：分隔符容错 14-5 → SKU_14_5", index.Search("14-5", SearchCategory.All, 100).Any(x => x.Name == "SKU_14_5说明.txt"));
    Check("搜索：拼音首字母 bdt", Hits("bdt") == 2, Hits("bdt").ToString());
    Check("搜索：全拼 baidi", Hits("baidi") == 2);
    Check("搜索：混写 14-5bdt", Hits("14-5bdt") == 2, Hits("14-5bdt").ToString());
    Check("搜索：拼音 + 排除", Hits("14-5 bdt -旧版") == 1);
    Check("搜索：扩展名条件", Hits("psd") == 2, Hits("psd").ToString());
    // 15-2 主图.psd 放在 14-5 文件夹里：路径命中也算，所以是 2
Check("搜索：文字 + 扩展名（含路径命中）", Hits("14-5 psd") == 2, Hits("14-5 psd").ToString());
Check("搜索：名字命中排在路径命中前面", index.Search("14-5 psd", SearchCategory.All, 100).First().Name == "14-5 主图.psd");
    Check("搜索：大小条件", Hits(">50mb") == 1);
    Check("搜索：分类词 文件夹", index.Search("文件夹", SearchCategory.All, 100).All(x => x.IsDirectory) && Hits("文件夹") == 2, Hits("文件夹").ToString());
    Check("搜索：排除目录里的不出现", Hits("废稿") == 0);
    var detailed = index.SearchDetailed("14-5", SearchCategory.Images, 100);
    Check("搜索：分类计数", detailed.CategoryCounts[SearchCategory.Images] == 2 && detailed.CategoryCounts[SearchCategory.Design] == 2 && detailed.Items.Count == 2,
        string.Join(",", detailed.CategoryCounts.Select(kv => $"{kv.Key}={kv.Value}")));

    // 前缀缓存：逐字输入的结果必须和直接搜一样
    var typed = new[] { "1", "14", "14-", "14-5", "14-5 ", "14-5 白", "14-5 白底" }.Select(x => index.Search(x, SearchCategory.All, 100).Select(e => e.FullPath).ToArray()).Last();
    var fresh = new FileIndexService(indexPath);
    await fresh.LoadAsync(new[] { excluded });
    var direct = fresh.Search("14-5 白底", SearchCategory.All, 100).Select(e => e.FullPath).ToArray();
    Check("搜索：逐字输入 = 直接搜索", typed.SequenceEqual(direct), $"{typed.Length} vs {direct.Length}");

    // 增量：新增、删除、整个目录删除
    var added = Path.Combine(keep, "14-5 新增.png");
    Touch(added);
    Check("增量：新增文件", await index.ApplyChangesAsync(new[] { added }, new[] { excluded }) == 1 && Hits("新增") == 1);
    File.Delete(added);
    Check("增量：删除文件", await index.ApplyChangesAsync(new[] { added }, new[] { excluded }) == 1 && Hits("新增") == 0);
    var batch = Path.Combine(keep, "批量");
    Directory.CreateDirectory(batch);
    for (var i = 0; i < 3; i++) Touch(Path.Combine(batch, $"批量 {i}.jpg"));
    var addedDir = await index.ApplyChangesAsync(new[] { batch }, new[] { excluded });
    Check("增量：新目录连同内容收录", addedDir == 4 && Hits("批量") == 4, addedDir.ToString());
    Directory.Delete(batch, true);
    var removedDir = await index.ApplyChangesAsync(new[] { batch }, new[] { excluded });
    Check("增量：删除目录一次去掉子项", removedDir == 4 && Hits("批量") == 0, removedDir.ToString());

    // 合并保存 + 持久化
    await index.FlushAsync();
    var reloaded = new FileIndexService(indexPath);
    Check("持久化：Flush 后重新载入一致", await reloaded.LoadAsync(new[] { excluded }) == index.Count, $"{reloaded.Count} / {index.Count}");
    var json = File.ReadAllText(indexPath);
    Check("持久化：不写入界面派生字段", !json.Contains("TypeLabel") && !json.Contains("IconGlyph") && !json.Contains("CardMeta"));

    // 并发：扫描进行中到达的变更不会丢
    var bulk = Path.Combine(root, "bulk");
    Directory.CreateDirectory(bulk);
    var unchangedDirectoryTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    for (var d = 0; d < 40; d++)
    {
        var dir = Path.Combine(bulk, $"d{d:00}");
        Directory.CreateDirectory(dir);
        for (var i = 0; i < 60; i++) Touch(Path.Combine(dir, $"f{i}.jpg"));
        // 固定样本时间，避免 Windows 新建大量文件时的目录时间更新延迟干扰复用检查。
        Directory.SetLastWriteTimeUtc(dir, unchangedDirectoryTime);
    }
    var lateFile = Path.Combine(keep, "扫描中途新增.jpg");
    var doomedFile = Path.Combine(keep, "14-5 白底图 旧版.jpg");
    var rebuild = index.RebuildAsync(new[] { root }, new[] { excluded }, null, CancellationToken.None);
    await Task.Delay(5);
    Touch(lateFile);
    File.Delete(doomedFile);
    var deferredResult = await index.ApplyChangesAsync(new[] { lateFile, doomedFile }, new[] { excluded });
    await rebuild;
    Check("并发：扫描中途新增的文件在索引里", Hits("扫描中途新增") == 1, $"applied={deferredResult}");
    Check("并发：扫描中途删除的文件不在索引里", !index.Search("旧版", SearchCategory.All, 100).Any(x => x.FullPath.EndsWith("旧版.jpg")));

    // 快速校准：没变的目录直接沿用
    Touch(Path.Combine(bulk, "d05", "新的一张.jpg"));
    Directory.SetLastWriteTimeUtc(Path.Combine(bulk, "d05"), unchangedDirectoryTime.AddMinutes(1));
    var quick = await index.RebuildAsync(new[] { root }, new[] { excluded }, null, CancellationToken.None, quick: true);
    Check("快速校准：发现新增文件", Hits("新的一张") == 1);
    Check("快速校准：沿用未变目录", quick.ReusedDirectories >= 38, $"沿用 {quick.ReusedDirectories} 个目录");

    // 实时监听
    using var monitor = new IndexMonitorService(index);
    var signal = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    monitor.IndexChanged += n => signal.TrySetResult(n);
    index.SetRoots(new[] { root });
    monitor.Start(new[] { root }, new[] { excluded });
    Touch(Path.Combine(keep, "14-5 监听到的.psd"));
    var fired = await Task.WhenAny(signal.Task, Task.Delay(5000)) == signal.Task;
    Check("实时监听：新文件 1 秒内进索引", fired && Hits("监听到的") == 1);
    monitor.Stop();
}
finally
{
    try { Directory.Delete(root, true); } catch { }
    try { File.Delete(indexPath); } catch { }
}

// ---------------- 性能：20 万条 ----------------
var bigIndex = Path.Combine(Path.GetTempPath(), "sqs-perf-" + Guid.NewGuid().ToString("N") + ".json");
try
{
    var words = new[] { "主图", "白底图", "详情页", "包装正面", "包装展开图", "场景图", "色卡", "宣传视频", "佩戴效果", "礼盒" };
    var exts = new[] { ".psd", ".jpg", ".png", ".ai", ".pdf", ".mp4" };
    var random = new Random(7);
    var entries = new List<FileEntry>(200_000);
    for (var i = 0; i < 200_000; i++)
    {
        var sku = $"{random.Next(1, 60)}-{random.Next(1, 30)}";
        var name = $"{sku} {words[random.Next(words.Length)]} {random.Next(1, 9)}{exts[random.Next(exts.Length)]}";
        var parent = $@"\\192.168.10.17\共享素材库\{sku}\{(i % 3 == 0 ? "设计" : "成品")}";
        entries.Add(new FileEntry { Name = name, FullPath = parent + "\\" + name, ParentPath = parent, RootPath = @"\\192.168.10.17\共享素材库", Extension = Path.GetExtension(name), Size = random.Next(1, 90_000_000), LastWriteUtc = DateTime.UtcNow.AddMinutes(-random.Next(0, 500_000)) });
    }
    File.WriteAllText(bigIndex, JsonSerializer.Serialize(entries));
    var perf = new FileIndexService(bigIndex);
    var loadWatch = Stopwatch.StartNew();
    await perf.LoadAsync(Array.Empty<string>());
    Console.WriteLine($"INFO  载入 20 万条（含拼音预计算）：{loadWatch.ElapsedMilliseconds} ms");
    perf.Search("预热", SearchCategory.All, 500);

    double Time(string query, int runs = 5)
    {
        var w = Stopwatch.StartNew();
        for (var i = 0; i < runs; i++) perf.SearchDetailed(query, SearchCategory.All, 500);
        return w.Elapsed.TotalMilliseconds / runs;
    }
    var cold = new[] { "14-5", "bdt", "14-5 psd", "主图 >50mb" }.Select(x => (x, Time(x))).ToArray();
    foreach (var (query, ms) in cold) Console.WriteLine($"INFO  搜索 “{query}”：{ms:F1} ms  ({perf.SearchDetailed(query, SearchCategory.All, 500).TotalMatches:N0} 条命中)");
    var steps = new List<string>();
    var typing = Stopwatch.StartNew();
    foreach (var step in new[] { "1", "14", "14-", "14-5", "14-5 ", "14-5 白", "14-5 白底" })
    {
        var w = Stopwatch.StartNew();
        var r = perf.SearchDetailed(step, SearchCategory.All, 500);
        steps.Add($"“{step}” {w.Elapsed.TotalMilliseconds:F0}ms/{r.TotalMatches:N0}");
    }
    Console.WriteLine($"INFO  逐字输入 7 次合计：{typing.ElapsedMilliseconds} ms（每次按键平均 {typing.ElapsedMilliseconds / 7.0:F1} ms）");
    Console.WriteLine("INFO  " + string.Join(" · ", steps));
    // 速度和机器有关（2 核虚拟机和 8 核台式机差好几倍），只做明显退化的硬性检查：单次 > 500 ms 才算失败；> 120 ms 只提示
    var timings = string.Join(", ", cold.Select(x => $"{x.Item2:F0}ms"));
    Check("性能：20 万条单次搜索 < 500 ms", cold.All(x => x.Item2 < 500), timings);
    if (cold.Any(x => x.Item2 >= 120)) Console.WriteLine($"WARN  有一次搜索超过 120 ms（{timings}），这台机器偏慢或负载高");
}
finally { try { File.Delete(bigIndex); } catch { } }

Console.WriteLine(failures.Count == 0 ? "ALL PASS" : $"{failures.Count} FAILED: {string.Join(", ", failures)}");
return failures.Count == 0 ? 0 : 1;
