namespace SharedQuickSearch;

/// <summary>
/// 自动更新的发布者公钥（ECDSA P-256）。只认这把钥匙签过名的 update.json。
/// 用 tools/UpdateSigner 生成：
///   dotnet run --project tools/UpdateSigner -- keygen
/// 它会把私钥存到你电脑上（不进仓库），并把公钥写进这个文件。
/// 公钥是空的时候，程序拒绝所有更新（宁可不更新，也不装没签名的东西）。
/// </summary>
public static class UpdateKey
{
    public const string PublicKeyPem = """
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEU5FX4Sm7OqFrfB8pr+N2gJ75smco
OZDxSVcf4OU1wrLe+7lz6Z7QTGKqlfeWns0rHCHuIPkXBw2jYUGEVAhBtw==
-----END PUBLIC KEY-----
""";
}
