using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

internal enum UpdateManifestSignatureStatus
{
    /// <summary>客户端未内置公钥，签名校验未启用（兼容旧发布流程）。</summary>
    NotConfigured = 0,
    Valid,
    Missing,
    Invalid
}

/// <summary>
/// 校验更新清单签名，防止明文 HTTP 链路上的中间人同时替换更新包和 SHA256。
/// 签名覆盖版本号、下载地址、SHA256、大小、包类型和是否强制更新。
/// 发布端使用 scripts/sign-update-manifest.ps1 生成 latestAppSignature 字段。
/// </summary>
internal static class UpdateManifestSignatureVerifier
{
    /// <summary>
    /// 受信任的更新签名公钥（RSA XML 格式，只包含 Modulus 和 Exponent）。
    /// 用 scripts/new-update-signing-key.ps1 生成密钥对后，把输出的公钥粘贴到这里。
    /// 为空时不启用签名校验；一旦填入，所有没有签名或签名无效的程序更新都会被拒绝。
    /// </summary>
    internal const string TrustedPublicKeyXml = "";

    public static UpdateManifestSignatureStatus Verify(CloudUpdateManifest manifest)
    {
        return Verify(manifest, TrustedPublicKeyXml);
    }

    internal static UpdateManifestSignatureStatus Verify(CloudUpdateManifest manifest, string trustedPublicKeyXml)
    {
        if (string.IsNullOrWhiteSpace(trustedPublicKeyXml))
        {
            return UpdateManifestSignatureStatus.NotConfigured;
        }

        var signatureText = manifest.LatestAppSignature?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(signatureText))
        {
            return UpdateManifestSignatureStatus.Missing;
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureText);
        }
        catch (FormatException)
        {
            return UpdateManifestSignatureStatus.Invalid;
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.FromXmlString(trustedPublicKeyXml);
            var payload = Encoding.UTF8.GetBytes(BuildCanonicalPayload(manifest));
            return rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                ? UpdateManifestSignatureStatus.Valid
                : UpdateManifestSignatureStatus.Invalid;
        }
        catch (CryptographicException)
        {
            return UpdateManifestSignatureStatus.Invalid;
        }
    }

    /// <summary>
    /// 规范化签名内容。必须与 scripts/sign-update-manifest.ps1 中的 Get-CanonicalManifestPayload 保持一致。
    /// </summary>
    internal static string BuildCanonicalPayload(CloudUpdateManifest manifest)
    {
        var lines = new[]
        {
            "quickreply-update-manifest-v1",
            manifest.LatestAppVersion?.Trim() ?? string.Empty,
            manifest.LatestAppDownloadUrl?.Trim() ?? string.Empty,
            (manifest.LatestAppSha256?.Trim() ?? string.Empty).ToLowerInvariant(),
            manifest.LatestAppSizeBytes is > 0
                ? manifest.LatestAppSizeBytes.Value.ToString(CultureInfo.InvariantCulture)
                : string.Empty,
            (manifest.LatestAppPackageKind?.Trim() ?? string.Empty).ToLowerInvariant(),
            manifest.LatestAppMandatory ? "true" : "false"
        };

        return string.Join("\n", lines);
    }
}
