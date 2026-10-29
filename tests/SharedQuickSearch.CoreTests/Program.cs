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
// 全组缩略图缓存放在共享盘目录里：里面的文件不能被当成素材索引
var teamCache = Path.Combine(root, PathRules.TeamCacheFolderName, "thumbs-v1", "ab");
Directory.CreateDirectory(teamCache);
Touch(Path.Combine(teamCache, "ab14-5cache.sqt"));

var indexPath = Path.Combine(Path.GetTempPath(), "sqs-core-index-" + Guid.NewGuid().ToString("N") + ".json");
try
{
    var index = new FileIndexService(indexPath);
    var built = await index.RebuildAsync(new[] { root }, new[] { excluded }, null, CancellationToken.None);
    Check("扫描：条目数（排除目录、忽略 Thumbs.db 和 .共享速搜缓存）", built.EntryCount == 8, built.EntryCount.ToString());
    Check("扫描：.共享速搜缓存 里的文件一个都不进索引", !index.SnapshotEntries().Any(x => PathRules.IsInTeamCache(x.FullPath)));
    var cacheChange = await index.ApplyChangesAsync(new[] { Path.Combine(teamCache, "ab14-5cache.sqt") }, new[] { excluded });
    Check("增量：.共享速搜缓存 里的变化被忽略", cacheChange == 0 && !index.SnapshotEntries().Any(x => PathRules.IsInTeamCache(x.FullPath)), cacheChange.ToString());
    Check("缓存目录识别：本身、里面算；名字相近的不算",
        PathRules.IsInTeamCache(@"\\NAS\设计部\.共享速搜缓存") && PathRules.IsInTeamCache(@"\\NAS\设计部\.共享速搜缓存\thumbs-v1\a.sqt")
        && !PathRules.IsInTeamCache(@"\\NAS\设计部\.共享速搜缓存备份\a.psd") && !PathRules.IsInTeamCache(@"\\NAS\设计部\共享速搜缓存\a.psd"));
    Check("缓存目录识别：相似前缀后嵌套的缓存仍被排除",
        PathRules.IsInTeamCache(@"\\NAS\设计部\.共享速搜缓存备份\.共享速搜缓存\thumbs-v1\a.sqt"));
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

    // 1.9 迁移：旧索引里文件名带全角 ／ ＼ 的条目路径是错的，载入时丢掉，等完整扫描补回
    var legacyPath = Path.Combine(root, "legacy-index.json");
    var legacyEntries = System.Text.Json.JsonSerializer.Deserialize<List<FileEntry>>(json)!;
    var bad = legacyEntries.First(x => !x.IsDirectory);
    bad.Name = "图片／旧.psd";
    File.WriteAllText(legacyPath, System.Text.Json.JsonSerializer.Serialize(legacyEntries));
    var migratedPath = Path.Combine(Path.GetTempPath(), $"sqs-migrated-{Guid.NewGuid():N}.json");
    var legacy = new FileIndexService(migratedPath, legacyPath);
    var legacyCount = await legacy.LoadAsync(new[] { excluded });
    Check("迁移：全角分隔符的旧条目载入时被丢掉", legacyCount == legacyEntries.Count - 1, $"{legacyCount} / {legacyEntries.Count}");
    Check("迁移：存成新文件、保留旧文件供回退", File.Exists(migratedPath) && File.Exists(legacyPath));
    // 新格式里文件名本来就带 ／ 的（1.10 以后扫出来的是对的）：再次启动不能被当成旧数据丢掉
    var modern = System.Text.Json.JsonSerializer.Deserialize<List<FileEntry>>(File.ReadAllText(migratedPath))!;
    modern.First(x => !x.IsDirectory).Name = "海报／横版.psd";
    File.WriteAllText(migratedPath, System.Text.Json.JsonSerializer.Serialize(modern));
    var again = new FileIndexService(migratedPath, legacyPath);
    Check("迁移：新格式文件不再做全角检查（不会每次启动都重扫）", await again.LoadAsync(new[] { excluded }) == modern.Count);
    try { File.Delete(migratedPath); } catch { }

    // 共享索引导入：本机没有索引时直接可搜，按自己的排除规则过滤；已经有索引时不覆盖
    var importExcluded = Path.Combine(keep, "14-5 产品资料");
    var importer = new FileIndexService(Path.Combine(Path.GetTempPath(), $"sqs-import-{Guid.NewGuid():N}.json"));
    var imported = await importer.ImportAsync(modern.Select(x => new FileEntry { Name = x.Name, FullPath = x.FullPath, ParentPath = x.ParentPath, Extension = x.Extension, IsDirectory = x.IsDirectory, RootPath = x.RootPath, Size = x.Size }).ToList(), new[] { importExcluded });
    Check("导入：按自己的排除规则过滤", imported > 0 && imported < modern.Count && !importer.SnapshotEntries().Any(x => PathRules.IsSameOrChild(x.FullPath, importExcluded)), $"{imported} / {modern.Count}");
    Check("导入：已经有索引时不覆盖", await importer.ImportAsync(modern, Array.Empty<string>()) == 0 && importer.Count == imported);

    // 并发：扫描进行中到达的变更不会丢
    var bulk = Path.Combine(root, "bulk");
    Directory.CreateDirectory(bulk);
    for (var d = 0; d < 40; d++)
    {
        var dir = Path.Combine(bulk, $"d{d:00}");
        Directory.CreateDirectory(dir);
        for (var i = 0; i < 60; i++) Touch(Path.Combine(dir, $"f{i}.jpg"));
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
    // Keep this test independent of deferred NTFS timestamp updates from the preceding concurrent-write test.
    var directoryBaseline = new DateTime(2025, 1, 2, 12, 0, 0, DateTimeKind.Utc);
    foreach (var directory in Directory.EnumerateDirectories(bulk)) Directory.SetLastWriteTimeUtc(directory, directoryBaseline);
    await index.RebuildAsync(new[] { root }, new[] { excluded }, null, CancellationToken.None);
    Touch(Path.Combine(bulk, "d05", "新的一张.jpg"));
    Directory.SetLastWriteTimeUtc(Path.Combine(bulk, "d05"), directoryBaseline.AddMinutes(1));
    var quick = await index.RebuildAsync(new[] { root }, new[] { excluded }, null, CancellationToken.None, quick: true);
    Check("快速校准：发现新增文件", Hits("新的一张") == 1);

    // 文件夹预览：直接子项计数 + 从子文件夹里挑图（只查索引）
    var bulkPreview = index.FolderPreview(bulk, 3);
    Check("文件夹预览：只有子文件夹时往下找图", bulkPreview.Samples.Count == 3 && bulkPreview.FolderCount == 40 && bulkPreview.FileCount == 0,
        $"samples={bulkPreview.Samples.Count} folders={bulkPreview.FolderCount} files={bulkPreview.FileCount}");
    var d05 = index.FolderPreview(Path.Combine(bulk, "d05"), 3);
    Check("文件夹预览：最近修改的排前面", d05.Samples.Count == 3 && d05.Samples[0].Name == "新的一张.jpg" && d05.FileCount == 61, $"first={d05.Samples.FirstOrDefault()?.Name} files={d05.FileCount}");
    Check("文件夹预览：不存在的目录返回空", index.FolderPreview(Path.Combine(root, "没有这个"), 3).Samples.Count == 0);
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

// ================= 全组缓存：路径统一、自动找文件夹、共享索引 =================
{
    Check("路径：共享根目录", UncPaths.ShareRoot(@"\\NAS\设计部\产品图\2026") == @"\\NAS\设计部" && UncPaths.ShareRoot(@"C:\设计部") is null);
    Check("路径：前缀替换按路径段，不区分大小写",
        UncPaths.Rewrite(@"\\nas\设计部\a.psd", @"\\NAS\设计部", @"Z:\设计部") == @"Z:\设计部\a.psd"
        && UncPaths.Rewrite(@"\\NAS\设计部", @"\\NAS\设计部", @"Z:\") == @"Z:"
        && UncPaths.Rewrite(@"\\NAS\设计部2\a.psd", @"\\NAS\设计部", @"Z:\") is null);

    var candidates = TeamCacheLocator.Candidates(new[] { @"\\NAS\设计部\产品图", @"\\NAS\设计部\包装\2026", @"C:\本机素材" });
    Check("自动找：先看共享根目录，再一层层往下；本机目录不参与",
        candidates.SequenceEqual(new[] { @"\\NAS\设计部", @"\\NAS\设计部\产品图", @"\\NAS\设计部\包装", @"\\NAS\设计部\包装\2026" }), string.Join(" ; ", candidates));

    var lab = Path.Combine(Path.GetTempPath(), "sqs-team-" + Guid.NewGuid().ToString("N"));
    var noWrite = Path.Combine(lab, "不存在的共享根");
    var writable = Path.Combine(lab, "设计部");
    Directory.CreateDirectory(writable);
    try
    {
        Check("自动找：没有就不建（create=false）", await TeamCacheLocator.FindOrCreateInAsync(new[] { noWrite, writable }, create: false) is null);
        var created = await TeamCacheLocator.FindOrCreateInAsync(new[] { noWrite, writable }, create: true);
        Check("自动找：第一个建不了，就建在下一个能建的地方", created == Path.Combine(writable, PathRules.TeamCacheFolderName) && Directory.Exists(created!), created ?? "null");
        Directory.CreateDirectory(noWrite);
        Check("自动找：已经有的优先，不会再建一个", await TeamCacheLocator.FindOrCreateInAsync(new[] { noWrite, writable }, create: true) == created
            && !Directory.Exists(Path.Combine(noWrite, PathRules.TeamCacheFolderName)));

        // 共享索引：A 用 \\NAS\设计部 发布，B 映射成 Z:\设计部 下载
        var shareA = new[] { new RootMapping(@"\\NAS\设计部", @"\\NAS\设计部") };
        var shareB = new[] { new RootMapping(@"Z:\设计部", @"\\NAS\设计部") };
        FileEntry E(string relative, bool dir = false) => new()
        {
            Name = relative.Split('\\').Last(), FullPath = @"\\NAS\设计部\" + relative,
            ParentPath = (@"\\NAS\设计部\" + relative)[..(@"\\NAS\设计部\" + relative).LastIndexOf('\\')],
            IsDirectory = dir, RootPath = @"\\NAS\设计部", Extension = dir ? "" : Path.GetExtension(relative)
        };
        var published = new[] { E("14-5", true), E(@"14-5\主图.psd"), E(@"14-5\白底图.jpg"), E(@".共享速搜缓存\thumbs-v1\ab\x.sqt") };
        var cacheFolder = created!;
        Check("共享索引：没人发过时要发", TeamIndexShare.ShouldPublish(cacheFolder, shareA, Array.Empty<string>(), TimeSpan.FromHours(6)));
        var count = TeamIndexShare.Publish(cacheFolder, published, shareA, Array.Empty<string>(), "1.12.0");
        Check("共享索引：发布时去掉缓存文件夹里的东西", count == 3, count.ToString());
        Check("共享索引：6 小时内不重复发布", !TeamIndexShare.ShouldPublish(cacheFolder, shareA, Array.Empty<string>(), TimeSpan.FromHours(6)));

        var fetched = TeamIndexShare.TryFetch(cacheFolder, shareB, Array.Empty<string>());
        Check("共享索引：映射盘的同事也能用，路径换成他自己的写法",
            fetched is { } got && got.Entries.Count == 3 && got.Entries.All(x => x.FullPath.StartsWith(@"Z:\设计部\") && x.RootPath == @"Z:\设计部"),
            fetched is null ? "null" : string.Join(" ; ", fetched.Value.Entries.Select(x => x.FullPath)));
        Check("共享索引：索引目录不一样的不用", TeamIndexShare.TryFetch(cacheFolder, new[] { new RootMapping(@"\\NAS\产品部", @"\\NAS\产品部") }, Array.Empty<string>()) is null);

        TeamIndexShare.Publish(cacheFolder, published, shareA, new[] { @"\\NAS\设计部\归档" }, "1.12.0");
        File.Delete(Directory.EnumerateFiles(Path.Combine(cacheFolder, TeamIndexShare.FolderName), "*.meta.json").First(x => !File.ReadAllText(x).Contains("归档")));
        Check("共享索引：发布者排除了、我没排除的，不用（会少文件）", TeamIndexShare.TryFetch(cacheFolder, shareB, Array.Empty<string>()) is null);
        Check("共享索引：我排除得更多，可以用", TeamIndexShare.TryFetch(cacheFolder, shareB, new[] { @"\\NAS\设计部\归档", @"\\NAS\设计部\旧版" }) is not null);
        // 权限：拿来的索引里，前两层我看不到的文件夹整枝去掉
        var visibleSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"Z:\设计部\14-5", @"Z:\设计部\公共" };
        var mine = new List<FileEntry>
        {
            new() { FullPath = @"Z:\设计部\14-5", IsDirectory = true }, new() { FullPath = @"Z:\设计部\14-5\主图.psd" },
            new() { FullPath = @"Z:\设计部\财务", IsDirectory = true }, new() { FullPath = @"Z:\设计部\财务\工资表.xlsx" },
            new() { FullPath = @"Z:\设计部\财务\2026", IsDirectory = true }, new() { FullPath = @"Z:\设计部\财务\2026\奖金.xlsx" },
            new() { FullPath = @"Z:\设计部\公共", IsDirectory = true }, new() { FullPath = @"Z:\设计部\公共\logo.ai" },
        };
        var (seen, hiddenFolders) = TeamIndexShare.FilterVisible(mine, new[] { @"Z:\设计部" }, visibleSet.Contains);
        Check("共享索引：我看不到的文件夹（含子文件夹和文件）整枝去掉", hiddenFolders >= 1 && seen.Count == 4 && !seen.Any(x => x.FullPath.Contains("财务")),
            string.Join(" ; ", seen.Select(x => x.FullPath)));
        var prefixSet = new List<FileEntry>(mine) { new() { FullPath = @"Z:\设计部\财务公开\说明.txt" } };
        var (prefixVisible, _) = TeamIndexShare.FilterVisible(prefixSet, new[] { @"Z:\设计部" }, visibleSet.Contains);
        Check("共享索引：权限预查按路径段，不误删相似前缀", prefixVisible.Any(x => x.FullPath.Contains("财务公开")));
        var (failedVisible, _) = TeamIndexShare.FilterVisible(mine, new[] { @"Z:\设计部" }, _ => throw new UnauthorizedAccessException());
        Check("共享索引：预查权限异常不放行该目录", failedVisible.Count == 0);
        Check("共享索引：拒绝点路径越界和控制字符", UncPaths.Rewrite(@"\\NAS\设计部\..\其它\secret.psd", @"\\NAS\设计部", @"Z:\设计部") is null &&
            UncPaths.Rewrite("\\\\NAS\\设计部\\bad\nname.psd", @"\\NAS\设计部", @"Z:\设计部") is null);
        Check("共享索引：有一个目录在本机磁盘上就不参与", TeamIndexShare.MapRoots(new[] { @"\\NAS\设计部", @"C:\本机" }, x => x.StartsWith(@"\\") ? x : null) is null);
    }
    finally { try { Directory.Delete(lab, true); } catch { } }
}

// ================= 自动更新：update.json 解析和安装包校验 =================
{
    var hash = new string('a', 64);
    bool Rejects(string json) { try { UpdateRules.Parse(json); return false; } catch (UpdateException) { return true; } }
    var ok = UpdateRules.Parse($$"""{"Version":"1.10.1","URL":"https://github.com/x/y/SharedQuickSearch-Setup.exe","sha256":"{{hash.ToUpperInvariant()}}","size":123}""");
    Check("更新：update.json 字段不分大小写，SHA-256 统一成小写", ok.Version == "1.10.1" && ok.Sha256 == hash && ok.Size == 123);
    Check("更新：本机 / 共享盘路径可以当下载地址", !Rejects($$"""{"version":"1.10.1","url":"\\\\nas\\共享\\SharedQuickSearch-Setup.exe","sha256":"{{hash}}"}"""));
    Check("更新：拒绝 http 下载地址", Rejects($$"""{"version":"1.10.1","url":"http://x/a.exe","sha256":"{{hash}}"}"""));
    Check("更新：拒绝 ftp 之类的其它协议", Rejects($$"""{"version":"1.10.1","url":"ftp://x/a.exe","sha256":"{{hash}}"}"""));
    Check("更新：拒绝缺 SHA-256 或长度不对", Rejects("""{"version":"1.10.1","url":"https://x/a.exe"}""") && Rejects("""{"version":"1.10.1","url":"https://x/a.exe","sha256":"abc"}"""));
    Check("更新：拒绝坏版本号和坏 JSON", Rejects($$"""{"version":"新版","url":"https://x/a.exe","sha256":"{{hash}}"}""") && Rejects("<html>404</html>"));
    Check("更新：拒绝超过 400 MB 的安装包", Rejects($$"""{"version":"1.10.1","url":"https://x/a.exe","sha256":"{{hash}}","size":999999999999}"""));

    Check("更新：网上取的 update.json 不能指向共享盘 / 别的机器上的路径", Rejects2($$"""{"version":"1.10.1","url":"\\\\attacker\\x.exe","sha256":"{{hash}}"}"""));
    bool Rejects2(string json) { try { UpdateRules.Parse(json, fromFileFeed: false); return false; } catch (UpdateException) { return true; } }
    Check("更新：版本号统一成三段", UpdateRules.NormalizeVersion("1.10") == "1.10.0" && UpdateRules.NormalizeVersion("v1.10.1") == "1.10.1" && UpdateRules.NormalizeVersion("1.10.1.0") == "1.10.1");
    Check("更新：四段版本号（第四段不是 0）不接受", UpdateRules.NormalizeVersion("1.10.1.5") is null && Rejects($$"""{"version":"1.10.1.5","url":"https://x/a.exe","sha256":"{{hash}}"}"""));
    Check("更新：1.10.0 比 1.9.9 新，1.10 等于 1.10.0", UpdateRules.Compare("1.10.0", "1.9.9") > 0 && UpdateRules.Compare("1.10", "1.10.0") == 0);

    // 发布者签名：只认程序里那把公钥签过的 update.json
    using (var publisher = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256))
    using (var stranger = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256))
    {
        var publicPem = publisher.ExportSubjectPublicKeyInfoPem();
        var bytes = System.Text.Encoding.UTF8.GetBytes($$"""{"version":"1.10.1","url":"https://x/a.exe","sha256":"{{hash}}"}""");
        var signature = Convert.ToBase64String(publisher.SignData(bytes, System.Security.Cryptography.HashAlgorithmName.SHA256));
        Check("签名：发布者签的，验证通过", UpdateRules.VerifySignature(bytes, signature, publicPem));
        Check("待更新缓存：从签名原始字节恢复版本", UpdateRules.ReadSignedManifest(bytes, signature, publicPem).Version == "1.10.1");
        var tampered = (byte[])bytes.Clone();
        tampered[^3] ^= 1;
        Check("签名：update.json 改了一个字节，拒绝", !UpdateRules.VerifySignature(tampered, signature, publicPem));
        bool pendingRejected = false;
        try { UpdateRules.ReadSignedManifest(tampered, signature, publicPem); } catch (UpdateException) { pendingRejected = true; }
        Check("待更新缓存：篡改清单不能在重启时绕过签名", pendingRejected);
        var forged = Convert.ToBase64String(stranger.SignData(bytes, System.Security.Cryptography.HashAlgorithmName.SHA256));
        Check("签名：别的钥匙签的，拒绝", !UpdateRules.VerifySignature(bytes, forged, publicPem));
        Check("签名：没有签名、签名乱码、没有公钥，都拒绝",
            !UpdateRules.VerifySignature(bytes, "", publicPem) && !UpdateRules.VerifySignature(bytes, "不是base64", publicPem) && !UpdateRules.VerifySignature(bytes, signature, ""));
    }

    var package = Path.Combine(Path.GetTempPath(), $"sqs-update-{Guid.NewGuid():N}.exe");
    try
    {
        File.WriteAllBytes(package, System.Text.Encoding.UTF8.GetBytes("pretend installer"));
        var realHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(package))).ToLowerInvariant();
        var good = new UpdateManifest("1.10.1", "https://x/a.exe", realHash, new FileInfo(package).Length, null);
        Check("更新：大小和 SHA-256 都对，校验通过", UpdateRules.VerifyContent(package, good) is null);
        Check("更新：SHA-256 不对，拒绝", UpdateRules.VerifyContent(package, good with { Sha256 = hash }) is not null);
        Check("更新：大小不对（没下完），拒绝", UpdateRules.VerifyContent(package, good with { Size = good.Size + 1 }) is not null);
        Check("更新：文件不存在，拒绝", UpdateRules.VerifyContent(package + ".missing", good) is not null);
    }
    finally { try { File.Delete(package); } catch { } }
}

// File replacement and startup acknowledgement are tested without touching the real installation, registry or NAS.
var transactionRoot = Path.Combine(Path.GetTempPath(), "sqs-transaction-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(transactionRoot);
try
{
    var exe = Path.Combine(transactionRoot, "app.exe");
    File.WriteAllText(exe, "old");
    using (var transaction = new UpdateFileTransaction(exe))
    {
        File.WriteAllText(exe, "broken-new");
        transaction.Rollback();
        Check("更新回滚：恢复旧程序字节", File.ReadAllText(exe) == "old");
    }
    using (var transaction = new UpdateFileTransaction(exe))
    {
        File.WriteAllText(exe, "healthy-new");
        transaction.Commit();
        Check("更新提交：健康版本保留，旧备份清理", File.ReadAllText(exe) == "healthy-new" && !File.Exists(transaction.Backup));
    }
    using (var transaction = new UpdateFileTransaction(exe)) File.WriteAllText(exe, "failed-before-commit");
    Check("更新回滚：异常退出事务自动恢复", File.ReadAllText(exe) == "healthy-new");
    var absent = Path.Combine(transactionRoot, "new-app.exe");
    using (var transaction = new UpdateFileTransaction(absent)) File.WriteAllText(absent, "broken-first-install");
    Check("更新回滚：首次安装失败只移除新程序", !File.Exists(absent) && File.Exists(exe));
    var token = Guid.NewGuid().ToString("N");
    var receipt = Path.Combine(transactionRoot, $"health-{token}.json");
    UpdateHealth.Acknowledge(receipt, token, transactionRoot, "1.10.0", 123);
    Check("启动确认：令牌、版本、进程均匹配", UpdateHealth.Matches(receipt, token, "1.10.0", 123));
    Check("启动确认：拒绝其它进程或版本", !UpdateHealth.Matches(receipt, token, "1.10.0", 456) && !UpdateHealth.Matches(receipt, token, "1.11.0", 123));
    Check("启动确认：拒绝更新目录以外的路径", !UpdateHealth.IsValidLocation(Path.Combine(transactionRoot, "..", $"health-{token}.json"), token, transactionRoot));
}
finally { Directory.Delete(transactionRoot, recursive: true); }

Console.WriteLine(failures.Count == 0 ? "ALL PASS" : $"{failures.Count} FAILED: {string.Join(", ", failures)}");
return failures.Count == 0 ? 0 : 1;
