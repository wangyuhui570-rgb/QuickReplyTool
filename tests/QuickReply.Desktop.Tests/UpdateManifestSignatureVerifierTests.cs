using System.Security.Cryptography;
using System.Text;
using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class UpdateManifestSignatureVerifierTests
{
    [Fact]
    public void Verify_ReturnsNotConfigured_WhenNoTrustedKey()
    {
        var manifest = CreateManifest();

        Assert.Equal(UpdateManifestSignatureStatus.NotConfigured, UpdateManifestSignatureVerifier.Verify(manifest, string.Empty));
    }

    [Fact]
    public void Verify_AcceptsValidSignature()
    {
        using var rsa = RSA.Create(2048);
        var manifest = Sign(CreateManifest(), rsa);

        Assert.Equal(UpdateManifestSignatureStatus.Valid, UpdateManifestSignatureVerifier.Verify(manifest, rsa.ToXmlString(false)));
    }

    [Fact]
    public void Verify_RejectsMissingSignature_WhenKeyConfigured()
    {
        using var rsa = RSA.Create(2048);

        Assert.Equal(UpdateManifestSignatureStatus.Missing, UpdateManifestSignatureVerifier.Verify(CreateManifest(), rsa.ToXmlString(false)));
    }

    [Theory]
    [InlineData("url")]
    [InlineData("sha")]
    [InlineData("version")]
    [InlineData("mandatory")]
    public void Verify_RejectsTamperedManifest(string tamperedField)
    {
        using var rsa = RSA.Create(2048);
        var signed = Sign(CreateManifest(), rsa);
        var tampered = tamperedField switch
        {
            "url" => Clone(signed, url: "http://evil.example.com/QuickReplyTool-AppUpdate-v2.2.0.zip"),
            "sha" => Clone(signed, sha: new string('0', 64)),
            "version" => Clone(signed, version: "9.9.9"),
            _ => Clone(signed, mandatory: true)
        };

        Assert.Equal(UpdateManifestSignatureStatus.Invalid, UpdateManifestSignatureVerifier.Verify(tampered, rsa.ToXmlString(false)));
    }

    [Fact]
    public void Verify_RejectsSignatureFromUntrustedKey()
    {
        using var trusted = RSA.Create(2048);
        using var attacker = RSA.Create(2048);
        var manifest = Sign(CreateManifest(), attacker);

        Assert.Equal(UpdateManifestSignatureStatus.Invalid, UpdateManifestSignatureVerifier.Verify(manifest, trusted.ToXmlString(false)));
    }

    [Fact]
    public void Verify_RejectsGarbageSignature()
    {
        using var rsa = RSA.Create(2048);
        var manifest = Clone(CreateManifest(), signature: "not-base64!!");

        Assert.Equal(UpdateManifestSignatureStatus.Invalid, UpdateManifestSignatureVerifier.Verify(manifest, rsa.ToXmlString(false)));
    }

    // 跨语言测试向量：由 scripts/new-update-signing-key.ps1 + sign-update-manifest.ps1
    // 在 Windows PowerShell 5.1 上真实生成（测试专用密钥，与正式发布无关）。
    // 如果 PowerShell 和 C# 的规范化逻辑不一致，这个测试会失败。
    private const string PowerShellTestPublicKeyXml = "<RSAKeyValue><Modulus>uODyMy/jTv64WyIo99d6fdn03NGR000zZD5F2quPy3BuiZpAN04k+daTNGHa6pH5bd1hd+mq/9itXET+GVyEdauNJXLKO2eBaHIrHbCoIzQjQ2CZh2MHkJ9MoAocp2JL4uuDhJmbhT6xY8n4qQtQ+z6lsxGSG6XDNfG3obDbcRXMK4dZJ3hQ8VgPZeaDS3kclVhlU0KOrIEURCJQdE1CRiX6PixyMivg5kM6VYxE/wMCs+Czb3FGU3xZW6gu5B3+bhPpcntuxiiqphMz2x+R/RjudGDflUhV3/+8QwT+o1czHPLW4TsV8AqwGSmMrurMTLMYIjZowN3whCpglikXdQ==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

    private const string PowerShellSignedManifestJson = """
        { "latestAppVersion": "2.2.0", "latestAppDownloadUrl": "http://45.207.201.102/downloads/QuickReplyTool-AppUpdate-v2.2.0.zip", "latestAppPackageKind": "zip_ota", "latestAppSha256": "ABCDEF0123456789", "latestAppSizeBytes": 123456, "latestAppMandatory": false, "latestAppNotes": "测试", "latestAppSignature": "p+zKn/rBET71HsFDC6IFV9/iUBjTXJMCJBQ0XORs9MeOIkPwFZJlhYxacgzw53JrSEyOklNKCk7vJyElrtgIRbKgw7AAXp66TWGri6ZgLn5Wsc8KJNHreYLqTVaFmnLDrtKw1yEc1yh55/gYLuri6oOnlbYXU6XuRGhl9AzeRAKJra9HUQTl4PsoG84UMNuA22mCFuLKxP1TdtGmRa1gMIF9BBOEpFpND/YZtY90R9btyCqO4ZjqHAQQIm+ag5xQZgIVVK3cRBYn+yRVtDPs5yOmsnRG7E5mV0jF4P2B4LvZzDQSa8Fdm1h+wmNqisBzSmklCLdn1yS0FGhvgpQdLw==" }
        """;

    [Fact]
    public void Verify_AcceptsManifestSignedByPowerShellScript()
    {
        var manifest = System.Text.Json.JsonSerializer.Deserialize<CloudUpdateManifest>(
            PowerShellSignedManifestJson,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        Assert.Equal(UpdateManifestSignatureStatus.Valid, UpdateManifestSignatureVerifier.Verify(manifest, PowerShellTestPublicKeyXml));
        Assert.Equal(
            UpdateManifestSignatureStatus.Invalid,
            UpdateManifestSignatureVerifier.Verify(Clone(manifest, url: "http://evil.example.com/x.zip"), PowerShellTestPublicKeyXml));
    }

    [Fact]
    public void BuildCanonicalPayload_NormalizesShaAndKind()
    {
        var payload = UpdateManifestSignatureVerifier.BuildCanonicalPayload(CreateManifest());

        Assert.Equal(
            "quickreply-update-manifest-v1\n2.2.0\nhttps://updates.example.com/QuickReplyTool-AppUpdate-v2.2.0.zip\nabcdef0123\n2048\nzip_ota\nfalse",
            payload);
    }

    private static CloudUpdateManifest CreateManifest() => new()
    {
        LatestAppVersion = "2.2.0",
        LatestAppDownloadUrl = "https://updates.example.com/QuickReplyTool-AppUpdate-v2.2.0.zip",
        LatestAppPackageKind = "ZIP_OTA",
        LatestAppSha256 = "ABCDEF0123",
        LatestAppSizeBytes = 2048
    };

    private static CloudUpdateManifest Sign(CloudUpdateManifest manifest, RSA rsa)
    {
        var payload = Encoding.UTF8.GetBytes(UpdateManifestSignatureVerifier.BuildCanonicalPayload(manifest));
        var signature = rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return Clone(manifest, signature: Convert.ToBase64String(signature));
    }

    private static CloudUpdateManifest Clone(
        CloudUpdateManifest source,
        string? url = null,
        string? sha = null,
        string? version = null,
        bool? mandatory = null,
        string? signature = null) => new()
    {
        LatestAppVersion = version ?? source.LatestAppVersion,
        LatestAppDownloadUrl = url ?? source.LatestAppDownloadUrl,
        LatestAppPackageKind = source.LatestAppPackageKind,
        LatestAppSha256 = sha ?? source.LatestAppSha256,
        LatestAppSizeBytes = source.LatestAppSizeBytes,
        LatestAppMandatory = mandatory ?? source.LatestAppMandatory,
        LatestAppSignature = signature ?? source.LatestAppSignature
    };
}
