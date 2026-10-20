using System.Text.Json;
using Microsoft.Extensions.Options;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Features.Identity;

namespace CreativeFlow.Infrastructure.Identity;

/// <summary>
/// GitHub OAuth Web 应用流程。供应商特有的 HTTP 和资料规范化保留在此处；
/// 账户表只接触稳定的供应商键和主体。
/// </summary>
internal sealed class GitHubExternalLoginProvider(
    IHttpClientFactory clients,
    IOptions<AuthenticationOptions> configuredOptions)
    : IExternalLoginProvider
{
    /// <summary>当前组件使用的已绑定配置。</summary>
    private readonly GitHubAuthenticationOptions options = configuredOptions.Value.GitHub;

    /// <summary>用于注册和回调路由的 GitHub 登录渠道标识。</summary>
    public string Key => "github";
    /// <summary>登录页面展示的 GitHub 渠道名称。</summary>
    public string DisplayName => "GitHub";
    /// <summary>仅在开关启用且 ClientId、ClientSecret 均已配置时开放 GitHub 登录。</summary>
    public bool IsEnabled => options.Enabled &&
                             !string.IsNullOrWhiteSpace(options.ClientId) &&
                             !string.IsNullOrWhiteSpace(options.ClientSecret);

    /// <summary>构造包含回调地址、授权范围、state 和 S256 PKCE 挑战的 GitHub 授权地址。</summary>
    public string BuildAuthorizationUrl(Uri callbackUri, string state, string codeChallenge)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = options.ClientId,
            ["redirect_uri"] = callbackUri.ToString(),
            ["scope"] = options.Scope,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        };

        return "https://github.com/login/oauth/authorize?" +
            string.Join('&', query.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    /// <summary>使用授权码和 PKCE 校验值换取访问令牌，再读取 GitHub 身份及可信邮箱资料。</summary>
    public async Task<ExternalLoginProfile> ExchangeCodeAsync(
        string code,
        string codeVerifier,
        Uri callbackUri,
        CancellationToken ct)
    {
        // 先交换访问令牌再读取身份资料，邮箱补充沿用原有可信来源选择；上游失败不能形成已登录会话。
        var client = clients.CreateClient("github-oauth");
        using var tokenRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "https://github.com/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("client_id", options.ClientId),
                new KeyValuePair<string, string>("client_secret", options.ClientSecret),
                new KeyValuePair<string, string>("code", code),
                new KeyValuePair<string, string>("redirect_uri", callbackUri.ToString()),
                new KeyValuePair<string, string>("code_verifier", codeVerifier),
            ]),
        };
        tokenRequest.Headers.Accept.ParseAdd("application/json");
        tokenRequest.Headers.UserAgent.ParseAdd("CreativeFlow/1.0");

        using var tokenResponse = await client.SendAsync(tokenRequest, ct);
        var tokenJson = await tokenResponse.Content.ReadAsStringAsync(ct);
        if (!tokenResponse.IsSuccessStatusCode ||
            !TryReadString(tokenJson, "access_token", out var accessToken))
        {
            throw new ExternalLoginException("provider_exchange_failed");
        }

        using var userRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.github.com/user");
        userRequest.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        userRequest.Headers.Accept.ParseAdd("application/vnd.github+json");
        userRequest.Headers.UserAgent.ParseAdd("CreativeFlow/1.0");

        using var userResponse = await client.SendAsync(userRequest, ct);
        var userJson = await userResponse.Content.ReadAsStringAsync(ct);
        if (!userResponse.IsSuccessStatusCode ||
            !TryReadString(userJson, "id", out var subject) ||
            !TryReadString(userJson, "login", out var login))
        {
            throw new ExternalLoginException("provider_profile_failed");
        }

        TryReadNullableString(userJson, "name", out var name);
        TryReadNullableString(userJson, "email", out var email);
        TryReadNullableString(userJson, "avatar_url", out var avatarUrl);

        return new ExternalLoginProfile(
            Key,
            subject,
            string.IsNullOrWhiteSpace(name) ? login : name!,
            email,
            avatarUrl);
    }

    /// <summary>读取外部身份响应中必需的字符串字段。</summary>
    private static bool TryReadString(string json, string property, out string value)
    {
        value = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(property, out var element)) return false;

            value = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? string.Empty,
                JsonValueKind.Number => element.GetRawText(),
                _ => string.Empty,
            };
            return !string.IsNullOrWhiteSpace(value);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>读取外部身份响应中的可选字符串字段。</summary>
    private static void TryReadNullableString(
        string json,
        string property,
        out string? value)
    {
        value = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty(property, out var element) &&
                element.ValueKind == JsonValueKind.String)
            {
                value = element.GetString();
            }
        }
        catch (JsonException)
        {
            // 必填字段会单独验证。当用户隐藏邮箱或 GitHub 更改负载时，允许可选资料字段缺失。
        }
    }
}
