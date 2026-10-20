using System.Security.Cryptography;
using CreativeFlow.Infrastructure.Options;
using CreativeFlow.Infrastructure.Security;

namespace CreativeFlow.UnitTests.Ai;

public sealed class WorkflowApiKeyServiceTests
{
    /// <summary>用测试配置、时钟与替身依赖组装被测服务。</summary>
    private static WorkflowApiKeyService CreateService(string? externalSecret = null) => new(
        new ExternalWorkflowApiOptions { ApiKeyHashSecret = externalSecret ?? string.Empty },
        new AiOptions { EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) });

    /// <summary>回归场景：Generates A One Time Secret And Stores Only Its Hash Metadata。</summary>
    [Fact]
    public void GeneratesAOneTimeSecretAndStoresOnlyItsHashMetadata()
    {
        var service = CreateService();

        var material = service.Generate();

        Assert.StartsWith("cf_live_", material.Plaintext, StringComparison.Ordinal);
        Assert.Equal(material.Prefix, service.ReadPrefix(material.Plaintext));
        Assert.Equal(material.Plaintext[^4..], material.Hint);
        Assert.Equal(32, material.Hash.Length);
        Assert.True(service.Verify(material.Plaintext, material.Hash));
        Assert.False(service.Verify(material.Plaintext + "x", material.Hash));
    }

    /// <summary>回归场景：Generates Independent Keys And Hashes。</summary>
    [Fact]
    public void GeneratesIndependentKeysAndHashes()
    {
        var service = CreateService();

        var first = service.Generate();
        var second = service.Generate();

        Assert.NotEqual(first.Plaintext, second.Plaintext);
        Assert.NotEqual(first.Hash, second.Hash);
    }

    /// <summary>回归场景：Rejects Malformed Configured Hash Secrets。</summary>
    [Fact]
    public void RejectsMalformedConfiguredHashSecrets()
    {
        Assert.Throws<InvalidOperationException>(() => CreateService("not-base64"));
        Assert.Throws<InvalidOperationException>(() => CreateService(Convert.ToBase64String([1, 2, 3])));
    }
}
