using System.Text.Json.Serialization;

namespace SystemWidget.WidBar.ExtensionApp.Models;

/// <summary>
/// Claude Code の <c>/api/oauth/usage</c> レスポンス。
/// フィールド構成は Claude Code CLI が実装している undocumented endpoint に依存する。
/// </summary>
internal sealed class UsageData
{
    [JsonPropertyName("five_hour")]
    public UsageWindow? FiveHour { get; set; }

    [JsonPropertyName("seven_day")]
    public UsageWindow? SevenDay { get; set; }

    [JsonPropertyName("seven_day_sonnet")]
    public UsageWindow? SevenDaySonnet { get; set; }

    [JsonPropertyName("sonnet_only")]
    public UsageWindow? SonnetOnly { get; set; }

    [JsonPropertyName("seven_day_opus")]
    public UsageWindow? SevenDayOpus { get; set; }

    [JsonPropertyName("extra_usage")]
    public ExtraUsageData? ExtraUsage { get; set; }

    /// <summary>週次 Sonnet 系。新旧フィールド名の両対応。</summary>
    public UsageWindow? Sonnet => SevenDaySonnet ?? SonnetOnly;
}

internal sealed class UsageWindow
{
    [JsonPropertyName("utilization")]
    public double? Utilization { get; set; }

    [JsonPropertyName("resets_at")]
    public DateTimeOffset? ResetsAt { get; set; }
}

internal sealed class ExtraUsageData
{
    [JsonPropertyName("is_enabled")]
    public bool IsEnabled { get; set; }

    [JsonPropertyName("monthly_limit")]
    public double? MonthlyLimit { get; set; }

    [JsonPropertyName("used_credits")]
    public double? UsedCredits { get; set; }
}

/// <summary>Claude Code の <c>~/.claude/.credentials.json</c> の中身。</summary>
internal sealed class CredentialsFile
{
    [JsonPropertyName("claudeAiOauth")]
    public ClaudeOAuth? ClaudeAiOauth { get; set; }
}

internal sealed class ClaudeOAuth
{
    [JsonPropertyName("accessToken")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expiresAt")]
    public long? ExpiresAt { get; set; }

    [JsonPropertyName("scopes")]
    public string[]? Scopes { get; set; }

    [JsonPropertyName("subscriptionType")]
    public string? SubscriptionType { get; set; }
}

internal sealed class TokenRefreshResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    public long ExpiresIn { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }
}
