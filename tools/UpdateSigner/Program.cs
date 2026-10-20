using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharedQuickSearch;

// 用法：
//   keygen   [--key <私钥文件>]                     生成钥匙：私钥存到你电脑上，公钥写进 src/SharedQuickSearch/UpdateKey.cs
//   manifest --exe <安装包> --version <x.y.z> --url <下载地址> [--notes <链接>] --out <update.json>
//   sign     <update.json> [--key <私钥文件>]        生成 update.json.sig；私钥也可以放在环境变量 UPDATE_SIGNING_KEY 里
// 签名前会用 UpdateKey.cs 里的公钥验一遍：钥匙对不上直接报错，免得发出去一个所有电脑都拒收的版本。

var repoRoot = FindRepoRoot();
var keyFileCs = Path.Combine(repoRoot, "src", "SharedQuickSearch", "UpdateKey.cs");
var defaultKey = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify), ".sharedquicksearch", "update-signing-key.pem");

try
{
    return args.FirstOrDefault() switch
    {
        "keygen" => KeyGen(Option("--key") ?? defaultKey),
        "manifest" => Manifest(Required("--exe"), Required("--version"), Required("--url"), Option("--notes") ?? "", Required("--out")),
        "sign" => Sign(args.Length > 1 && !args[1].StartsWith("--") ? args[1] : throw new ArgumentException("要签名的 update.json 路径"), Option("--key")),
        "verify" => Verify(Required("--manifest"), Required("--exe")),
        _ => Usage()
    };
}
catch (Exception ex) when (ex is ArgumentException or IOException or CryptographicException or InvalidOperationException)
{
    Console.Error.WriteLine("失败：" + ex.Message);
    return 1;
}

int KeyGen(string keyPath)
{
    if (File.Exists(keyPath)) throw new InvalidOperationException($"{keyPath} 已经存在。换钥匙会让已经装好的电脑全部拒收新版本；真要换，先手动挪走旧文件。");
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
    File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
    var publicPem = key.ExportSubjectPublicKeyInfoPem();
    WritePublicKey(publicPem);
    Console.WriteLine($"私钥：{keyPath}");
    Console.WriteLine("  · 不要提交到仓库，不要发给别人。丢了就没法再发更新（只能让大家手动重装）。备份到安全的地方。");
    Console.WriteLine("  · 用 GitHub 发布的话：仓库 Settings → Secrets and variables → Actions → New repository secret，");
    Console.WriteLine("    名字 UPDATE_SIGNING_KEY，内容是这个文件的全部文字。");
    Console.WriteLine($"公钥已写进：{keyFileCs}（这个要提交，编译进程序）");
    return 0;
}

int Manifest(string exe, string version, string url, string notes, string output)
{
    var normalized = UpdateRules.NormalizeVersion(version) ?? throw new ArgumentException($"版本号 {version} 不对");
    var info = new FileInfo(exe);
    if (!info.Exists) throw new ArgumentException($"找不到 {exe}");
    using var stream = info.OpenRead();
    var manifest = new UpdateManifest(normalized, url, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(), info.Length, notes);
    var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    // 用客户端同一套规则检查一遍（https / 文件路径、哈希长度、大小）
    UpdateRules.Parse(json, fromFileFeed: !UpdateRules.IsHttps(url));
    File.WriteAllText(output, json, new UTF8Encoding(false));
    Console.WriteLine(json);
    return 0;
}

int Sign(string manifestPath, string? keyPath)
{
    var pem = Environment.GetEnvironmentVariable("UPDATE_SIGNING_KEY");
    if (keyPath is not null || string.IsNullOrWhiteSpace(pem))
    {
        keyPath ??= defaultKey;
        if (!File.Exists(keyPath)) throw new InvalidOperationException($"找不到私钥 {keyPath}，也没有环境变量 UPDATE_SIGNING_KEY。先运行 keygen。");
        pem = File.ReadAllText(keyPath);
    }
    var bytes = File.ReadAllBytes(manifestPath);
    UpdateRules.Parse(Encoding.UTF8.GetString(bytes).TrimStart('﻿'));
    using var key = ECDsa.Create();
    key.ImportFromPem(pem);
    var signature = Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256));

    var embedded = ReadPublicKey();
    if (embedded.Length == 0) throw new InvalidOperationException("UpdateKey.cs 里还没有公钥，先运行 keygen。");
    if (!UpdateRules.VerifySignature(bytes, signature, embedded))
        throw new InvalidOperationException("这把私钥和程序里的公钥（UpdateKey.cs）不是一对：签出来的更新所有电脑都会拒收。");
    File.WriteAllText(manifestPath + ".sig", signature, Encoding.ASCII);
    Console.WriteLine($"已签名：{manifestPath}.sig");
    return 0;
}

int Verify(string manifestPath, string exe)
{
    var manifest = UpdateRules.ReadSignedManifest(File.ReadAllBytes(manifestPath), File.ReadAllText(manifestPath + ".sig"), ReadPublicKey());
    if (UpdateRules.VerifyContent(exe, manifest) is { } problem) throw new InvalidOperationException(problem);
    if (OperatingSystem.IsWindows())
    {
        var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
        if (version.ProductName != "共享速搜" || UpdateRules.Compare(version.FileVersion, manifest.Version) != 0)
            throw new InvalidOperationException("安装包产品名或版本与签名清单不符");
    }
    Console.WriteLine($"已验证发布签名、SHA-256、大小和版本：{manifest.Version}");
    return 0;
}

void WritePublicKey(string publicPem)
{
    var text = File.ReadAllText(keyFileCs);
    var replaced = Regex.Replace(text, @"public const string PublicKeyPem = (""""""[\s\S]*?""""""|""[^""]*"");",
        "public const string PublicKeyPem = \"\"\"\n" + publicPem.Trim().Replace("\r\n", "\n") + "\n\"\"\";");
    if (replaced == text) throw new InvalidOperationException("UpdateKey.cs 的格式认不出来，没有写入公钥");
    File.WriteAllText(keyFileCs, replaced, new UTF8Encoding(false));
}

string ReadPublicKey()
{
    var match = Regex.Match(File.ReadAllText(keyFileCs), @"-----BEGIN PUBLIC KEY-----[\s\S]*?-----END PUBLIC KEY-----");
    return match.Success ? match.Value : "";
}

string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

string Required(string name) => Option(name) ?? throw new ArgumentException($"缺少参数 {name}");

static string FindRepoRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SharedQuickSearch.sln"))) directory = directory.Parent;
    return directory?.FullName ?? Directory.GetCurrentDirectory();
}

static int Usage()
{
    Console.WriteLine("""
        共享速搜 · 更新签名工具
          keygen   [--key <私钥文件>]
          manifest --exe <安装包> --version <x.y.z> --url <下载地址> [--notes <链接>] --out <update.json>
          sign     <update.json> [--key <私钥文件>]
          verify   --manifest <update.json> --exe <安装包>
        """);
    return 2;
}
