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
