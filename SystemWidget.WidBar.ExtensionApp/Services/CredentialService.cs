using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using SystemWidget.WidBar.ExtensionApp.Models;

namespace SystemWidget.WidBar.ExtensionApp.Services;

/// <summary>
/// Claude Code の OAuth 資格情報を読み、必要なら refresh token で access token を更新する。
///
/// <para>安全方針 — Claude Code 本体のファイル (<c>~/.claude/.credentials.json</c>) には
/// 書き込まない。更新した token はウィジェット専用キャッシュ
/// (<c>%LOCALAPPDATA%\system_widget\claude_token.json</c>) に保存し、次回以降はキャッシュ側を優先する。</para>
///
/// <para>これは Python 版 (<c>monitors/claude_api.py</c>) と同じポリシー。デスクトップ版 CC が
/// credentials.json を触らなくなっても、ウィジェット側は自前で token を回せる。</para>
/// </summary>
internal static class CredentialService
{
    private static readonly HttpClient _httpClient = new();
    private const string TokenRefreshUrl = "https://console.anthropic.com/v1/oauth/token";

    // Expiry buffer: 残りこれ未満なら refresh を試みる (Claude Code の使用中に切れないよう余裕を持つ)
    private static readonly TimeSpan ExpiryBuffer = TimeSpan.FromMinutes(5);

    // refresh token 失効 (400/401/403) 後の再試行抑止。人間の再ログインが必要なので頻繁に叩かない。
    private static readonly TimeSpan RefreshDeadRetryInterval = TimeSpan.FromHours(1);
    private static DateTime _refreshDeadUntil = DateTime.MinValue;

    private static string GetClaudeCredentialsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude",
        ".credentials.json");

    private static string GetWidgetCachePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "system_widget",
        "claude_token.json");

    /// <summary>
    /// 使用可能な access token を返す。取れなければ null。
    /// 優先順: (1) CC ファイルの有効な token → (2) widget キャッシュの有効な token
    /// → (3) refresh token で再取得 (キャッシュ側 refresh を優先)。
    /// </summary>
    public static async Task<string?> GetAccessTokenAsync()
    {
        try
        {
            var cc = ReadCredentials(GetClaudeCredentialsPath());
            if (IsValid(cc)) return cc!.AccessToken;

            var cache = ReadCredentials(GetWidgetCachePath());
            if (IsValid(cache)) return cache!.AccessToken;

            if (DateTime.UtcNow < _refreshDeadUntil)
            {
                System.Diagnostics.Debug.WriteLine("Refresh suppressed: refresh token dead, waiting for user relogin");
                return null;
            }

            var refreshToken = cache?.RefreshToken ?? cc?.RefreshToken;
            if (string.IsNullOrEmpty(refreshToken))
            {
                System.Diagnostics.Debug.WriteLine("No refresh token available (credentials missing?)");
                return null;
            }

            var refreshed = await RefreshTokenAsync(refreshToken);
            if (refreshed == null) return null;

            SaveToWidgetCache(refreshed);
            return refreshed.AccessToken;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GetAccessTokenAsync failed: {ex}");
            return null;
        }
    }

    /// <summary>credentials.json が存在するかを sync でチェック (UI 側の可用性表示用)。</summary>
    public static bool CredentialsExist() =>
        File.Exists(GetClaudeCredentialsPath()) || File.Exists(GetWidgetCachePath());

    private static ClaudeOAuth? ReadCredentials(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<CredentialsFile>(json)?.ClaudeAiOauth;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ReadCredentials({path}) failed: {ex.Message}");
            return null;
        }
    }

    private static bool IsValid(ClaudeOAuth? oauth)
    {
        if (oauth?.AccessToken is null || oauth.ExpiresAt is not long expiresAt) return false;
        var expiresAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(expiresAt);
        return DateTimeOffset.UtcNow < expiresAtUtc - ExpiryBuffer;
    }

    private static async Task<ClaudeOAuth?> RefreshTokenAsync(string refreshToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenRefreshUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "grant_type", "refresh_token" },
                { "refresh_token", refreshToken },
            });

            using var response = await _httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                if (code is 400 or 401 or 403)
                {
                    _refreshDeadUntil = DateTime.UtcNow + RefreshDeadRetryInterval;
                    System.Diagnostics.Debug.WriteLine(
                        $"Refresh token dead ({code}); suppressing retries until {_refreshDeadUntil:u}. body={body}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"Refresh HTTP {code}: {body}");
                }
                return null;
            }

            var parsed = JsonSerializer.Deserialize<TokenRefreshResponse>(body);
            if (parsed?.AccessToken is null) return null;

            return new ClaudeOAuth
            {
                AccessToken = parsed.AccessToken,
                RefreshToken = parsed.RefreshToken ?? refreshToken,
                ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (Math.Max(parsed.ExpiresIn, 60) * 1000),
                Scopes = parsed.Scope?.Split(' '),
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RefreshTokenAsync failed: {ex.Message}");
            return null;
        }
    }

    private static void SaveToWidgetCache(ClaudeOAuth oauth)
    {
        try
        {
            var path = GetWidgetCachePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var payload = new CredentialsFile { ClaudeAiOauth = oauth };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });

            // 途中書き込みを他プロセスに見せないよう temp + replace で原子化する。
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SaveToWidgetCache failed: {ex.Message}");
        }
    }
}
