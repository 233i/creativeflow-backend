using System.Net;
using Microsoft.Extensions.Configuration;
using CreativeFlow.Infrastructure;
using CreativeFlow.Infrastructure.Ai;
using CreativeFlow.Infrastructure.Ai.Embeddings;
using CreativeFlow.Infrastructure.Options;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>验证工作流 HTTPS 通配的真实注册配置、请求守卫、DNS 边界与其他模块隔离。</summary>
public sealed class WorkflowEndpointPolicyTests
{
    /// <summary>显式配置一次公网 HTTPS 通配后，四种聊天协议及不同端口无需逐端点授权。</summary>
    [Theory]
    [InlineData("https://first.example/v1/chat/completions")]
    [InlineData("https://second.example/v1/responses")]
    [InlineData("https://third.example/v1/messages")]
    [InlineData("https://fourth.example/v1beta/models/gemini-test:generateContent")]
    [InlineData("https://fifth.example:8443/api/chat/completions")]
    public void ExplicitWildcardAllowsPublicHttpsEndpoints(string endpoint)
    {
        var network = Network(["https://*"]);
        Assert.True(network.Enabled);
        network.Policy.ValidateEndpoint(new(endpoint));
        network.Policy.ValidateAddresses(new(endpoint), [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("2606:4700:4700::1111")]);
    }

    /// <summary>通配不接受明文、其他协议、URL 内凭据、查询参数或片段。</summary>
    [Theory]
    [InlineData("http://first.example/v1/chat/completions")]
    [InlineData("http://127.0.0.1:1234/v1/chat/completions")]
    [InlineData("ftp://first.example/model")]
    [InlineData("https://user:secret@first.example/v1/messages")]
    [InlineData("https://first.example/v1/responses?key=secret")]
    [InlineData("https://first.example/v1/responses#fragment")]
    public void WildcardRetainsUrlRestrictions(string endpoint) =>
        Assert.Throws<InvalidOperationException>(() => Network(["https://*"]).Policy.ValidateEndpoint(new(endpoint)));

    /// <summary>未配置、裸通配、域名通配和 HTTP 通配均不扩大默认精确许可范围。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("*")]
    [InlineData("http://*")]
    [InlineData("https://*.example")]
    [InlineData("https://first.example/*")]
    public void UnconfiguredAndUnsupportedPatternsFailClosed(string? endpoint) =>
        Assert.Throws<InvalidOperationException>(() => Network(endpoint is null ? [] : [endpoint]).Policy
            .ValidateEndpoint(new("https://first.example/v1/chat/completions")));

    /// <summary>相同 https://* 配置不会放宽默认共用策略，向量化和固定外部操作仍使用精确许可。</summary>
    [Fact]
    public void WildcardCannotOptOtherModulesIntoPublicAccess()
    {
        var policy = new EmbeddingEndpointPolicy(new EmbeddingOptions { AllowedEndpoints = ["https://*"] }, false);
        Assert.Throws<InvalidOperationException>(() => policy.ValidateEndpoint(new("https://first.example/v1/embeddings")));
    }

    /// <summary>域名命中 HTTPS 通配也不能连接私网、回环、元数据、保留或映射私网 IP。</summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.100.100.200")]
    [InlineData("0.0.0.0")]
    [InlineData("192.0.2.1")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("2001:db8::1")]
    public void WildcardRetainsAddressRestrictions(string address)
    {
        var policy = Network(["https://*"]).Policy;
        var uri = new Uri("https://model.example/v1/chat/completions");
        policy.ValidateEndpoint(uri);
        Assert.Throws<InvalidOperationException>(() => policy.ValidateAddresses(uri, [IPAddress.Parse(address)]));
        Assert.Throws<InvalidOperationException>(() => policy.ValidateAddresses(uri, [IPAddress.Parse("8.8.8.8"), IPAddress.Parse(address)]));
    }

    /// <summary>公网通配在开发环境也不授权回环；原有明确许可的开发模型仍然可用。</summary>
    [Fact]
    public void DevelopmentLoopbackStillNeedsItsOwnExactPermission()
    {
        var uri = new Uri("https://localhost:1234/v1/chat/completions");
        var wildcard = Network(["https://*"], development: true).Policy;
        wildcard.ValidateEndpoint(uri);
        Assert.Throws<InvalidOperationException>(() => wildcard.ValidateAddresses(uri, [IPAddress.Loopback]));
        var explicitlyAllowed = Network(["https://*", uri.AbsoluteUri], development: true).Policy;
        explicitlyAllowed.ValidateEndpoint(uri);
        explicitlyAllowed.ValidateAddresses(uri, [IPAddress.Loopback]);
    }

    /// <summary>通配不改变精确名单行为，也不会继承向量服务的 HTTPS 通配。</summary>
    [Fact]
    public void ExactPermissionsAndModuleConfigurationStayIndependent()
    {
        var network = Network(["https://first.example/v1/chat/completions"]);
        network.Policy.ValidateEndpoint(new("https://first.example/v1/chat/completions"));
        Assert.Throws<InvalidOperationException>(() => network.Policy.ValidateEndpoint(new("https://first.example/admin")));
        Assert.Throws<InvalidOperationException>(() => network.Policy.ValidateEndpoint(new("https://second.example/v1/chat/completions")));
    }

    /// <summary>生产请求守卫允许授权的 HTTPS 请求且阻止 HTTP；使用合成传输，不访问模型或产生费用。</summary>
    [Fact]
    public async Task RequestGuardAppliesWildcardBeforeSending()
    {
        using var transport = new EmbeddingFixtures.Transport();
        using var client = new HttpClient(new WorkflowV2RequestGuard(Network(["https://*"])) { InnerHandler = transport });
        using var response = await client.PostAsync("https://first.example/v1/messages", new StringContent("{}"), TestContext.Current.CancellationToken);
        Assert.Equal(1, transport.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PostAsync("http://first.example/v1/messages", new StringContent("{}"), TestContext.Current.CancellationToken));
        Assert.Equal(1, transport.Calls);
    }

    /// <summary>直接调用生产注册工厂，验证 Workflow 配置绑定而非测试自建策略。</summary>
    private static WorkflowV2Network Network(string[] endpoints, bool development = false)
    {
        var values = new Dictionary<string, string?>
        {
            ["Workflow:Enabled"] = "true",
            ["Embedding:AllowedEndpoints:0"] = "https://*",
        };
        for (var i = 0; i < endpoints.Length; i++) values[$"Workflow:AllowedEndpoints:{i}"] = endpoints[i];
        return DependencyInjection.CreateWorkflowV2Network(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), development);
    }
}
