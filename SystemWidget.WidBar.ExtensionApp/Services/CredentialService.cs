using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using SystemWidget.WidBar.ExtensionApp.Models;

namespace SystemWidget.WidBar.ExtensionApp.Services;

/// <summary>
/// Reads Claude Code's OAuth credentials and, if needed, renews the access token using the refresh token.
///
/// <para>Safety policy: never write to Claude Code's own file (<c>~/.claude/.credentials.json</c>).
/// Refreshed tokens are saved to a widget-only cache
/// (<c>%LOCALAPPDATA%\system_widget\claude_token.json</c>), which is preferred from then on.</para>
///
/// <para>This is the same policy as the Python version (<c>monitors/claude_api.py</c>). Even if desktop
/// Claude Code stops updating credentials.json, the widget can keep its own token rotating.</para>
/// </summary>
internal static class CredentialService
{
    private static readonly HttpClient _httpClient = new();
    private const string TokenRefreshUrl = "https://console.anthropic.com/v1/oauth/token";

    // Expiry buffer: try a refresh when less than this remains (leaves headroom so it doesn't expire mid-use)
    private static readonly TimeSpan ExpiryBuffer = TimeSpan.FromMinutes(5);

    // Suppress retries after the refresh token dies (400/401/403). A human has to log in again, so don't retry often.
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
    /// Returns a usable access token, or null if none can be obtained.
    /// Order: (1) a valid token in Claude Code's file, (2) a valid token in the widget cache,
    /// (3) a fresh token from the refresh token (preferring the cache's refresh token).
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

    /// <summary>Synchronously checks whether credentials.json exists (used by the UI to show availability).</summary>
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

            // Write to a temp file and replace, so other processes never see a partial write.
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
