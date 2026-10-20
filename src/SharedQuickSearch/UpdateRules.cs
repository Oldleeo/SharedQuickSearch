using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace SharedQuickSearch;

/// <summary>
/// 更新说明（update.json）：发布时由 GitHub Actions 生成，也可以放在自己的服务器或共享盘上。
/// <code>{ "version": "1.10.1", "url": "https://…/SharedQuickSearch-Setup.exe", "sha256": "…64 位…", "size": 82345678, "notes": "https://…" }</code>
/// </summary>
public sealed record UpdateManifest(string Version, string Url, string Sha256, long Size, string? Notes);

public sealed class UpdateException(string message) : Exception(message);

/// <summary>更新说明的解析和安装包内容校验。不依赖界面，后端测试直接测。</summary>
public static class UpdateRules
{
    public const long MaxPackageBytes = 400L * 1024 * 1024;

    /// <summary>Used for both downloaded manifests and persisted pending updates. Never trust state.json's parsed fields.</summary>
    public static UpdateManifest ReadSignedManifest(byte[] bytes, string signature, string publicKey, bool fromFileFeed = true)
    {
        if (bytes.Length > 64 * 1024 || !VerifySignature(bytes, signature, publicKey))
            throw new UpdateException("更新说明的签名无效，已忽略");
        return Parse(System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'), fromFileFeed);
    }

    public static bool IsHttps(string value) => value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    public static bool IsPlainHttp(string value) => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 校验发布者签名：update.json 的原始字节 + update.json.sig（Base64 的 ECDSA P-256 / SHA-256 签名）。
    /// 公钥写在程序里（UpdateKey.cs），私钥只在发布者手上（GitHub 仓库的 Secret、发布者自己的电脑）。
    /// 所以就算有人改了 GitHub 上的文件、或者往共享盘的更新源里放了别的 exe，没有私钥也签不出能通过的 update.json。
    /// </summary>
    public static bool VerifySignature(byte[] manifestBytes, string signatureBase64, string publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPem) || string.IsNullOrWhiteSpace(signatureBase64)) return false;
        try
        {
            var signature = Convert.FromBase64String(signatureBase64.Trim());
            using var key = System.Security.Cryptography.ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            return key.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256);
        }
        catch (FormatException) { return false; }
        catch (CryptographicException) { return false; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>版本号统一成三段（1.10 → 1.10.0）；四段且第四段不是 0 的不接受，免得和程序的版本号对不上而反复更新。</summary>
    public static string? NormalizeVersion(string? value)
    {
        if (!Version.TryParse(value?.Trim().TrimStart('v', 'V'), out var version)) return null;
        if (version.Revision > 0) return null;
        return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    /// <summary>比较三段版本号：a 比 b 新返回正数。认不出的版本号当作 0.0.0。</summary>
    public static int Compare(string? a, string? b)
    {
        static Version Of(string? v) => Version.TryParse(NormalizeVersion(v), out var parsed) ? parsed : new Version(0, 0, 0);
        return Of(a).CompareTo(Of(b));
    }

    /// <param name="fromFileFeed">update.json 本身来自文件路径时，才允许下载地址也是文件路径。</param>
    public static UpdateManifest Parse(string json, bool fromFileFeed = true)
    {
        UpdateManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<UpdateManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { throw new UpdateException("update.json 格式不对"); }
        var normalized = NormalizeVersion(manifest?.Version);
        if (manifest is null || normalized is null) throw new UpdateException("update.json 里没有有效的版本号");
        if (string.IsNullOrWhiteSpace(manifest.Url)) throw new UpdateException("update.json 里没有下载地址");
        if (IsPlainHttp(manifest.Url)) throw new UpdateException("下载地址必须是 https");
        if (!IsHttps(manifest.Url) && manifest.Url.Contains("://", StringComparison.Ordinal)) throw new UpdateException("下载地址只能是 https 或文件路径");
        // 网上取来的 update.json 不能指向 \\某台机器\x.exe：打开那种路径时 Windows 会把登录凭据的哈希发过去
        if (!fromFileFeed && !IsHttps(manifest.Url)) throw new UpdateException("网上的更新说明只能用 https 下载地址");
        if (manifest.Sha256 is null || manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit)) throw new UpdateException("update.json 里没有有效的 SHA-256");
        if (manifest.Size < 0 || manifest.Size > MaxPackageBytes) throw new UpdateException("安装包大小不对");
        return manifest with { Version = normalized, Sha256 = manifest.Sha256.ToLowerInvariant(), Url = manifest.Url.Trim() };
    }

    /// <summary>大小和 SHA-256。返回 null 表示通过，否则是给用户看的原因。</summary>
    public static string? VerifyContent(string path, UpdateManifest manifest)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return "安装包不见了";
        if (manifest.Size > 0 && info.Length != manifest.Size) return "安装包大小对不上，可能没下载完整";
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return hash == manifest.Sha256 ? null : "安装包校验没通过（SHA-256 不一致），已丢弃";
    }
}
