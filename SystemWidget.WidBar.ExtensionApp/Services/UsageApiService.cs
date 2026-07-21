using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using SystemWidget.WidBar.ExtensionApp.Models;

namespace SystemWidget.WidBar.ExtensionApp.Services;

/// <summary>
/// Claude Code の undocumented <c>/api/oauth/usage</c> エンドポイントから利用状況を取得する。
/// <para>Claude Code CLI 本体と同じヘッダ (<c>User-Agent: claude-code/&lt;version&gt;</c> と
/// <c>anthropic-beta: oauth-2025-04-20</c>) を必ず付ける。付け忘れると 429 バケット行き。</para>
/// </summary>
internal static class UsageApiService
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private const string UsageApiUrl = "https://api.anthropic.com/api/oauth/usage";

    // 429 / 5xx で最大この回数まで指数バックオフで再試行する (1s, 2s, 4s, 8s, 16s)。
    private const int MaxRetries = 5;

    // `claude --version` を叩けなかった場合のフォールバック UA バージョン。
    // Anthropic の bucket 判定に使われるので、実際の CC 版に近い数字を置く。
    private const string FallbackVersion = "2.1.100";

    private static string? _cachedVersion;

    /// <summary>
    /// 利用状況を取得。credentials が無い / refresh 失効 / 429 継続などで取れなければ null。
    /// </summary>
    public static async Task<UsageData?> GetUsageAsync()
    {
        var token = await CredentialService.GetAccessTokenAsync();
        if (string.IsNullOrEmpty(token))
        {
            Debug.WriteLine("UsageApiService: no access token");
            return null;
        }

        var version = GetClaudeCodeVersion();

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, UsageApiUrl);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.UserAgent.ParseAdd($"claude-code/{version}");
                request.Headers.Add("anthropic-beta", "oauth-2025-04-20");

                using var response = await _httpClient.SendAsync(request);
                var status = (int)response.StatusCode;

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    return JsonSerializer.Deserialize<UsageData>(json);
                }

                var body = await response.Content.ReadAsStringAsync();
                Debug.WriteLine($"UsageApiService HTTP {status} attempt {attempt + 1}/{MaxRetries + 1}: {body}");

                // 429 / 5xx は指数バックオフで再試行。それ以外 (401 期限切れ等) はもう一度 refresh を
                // 走らせても状況変わらないので即 null 返す。次回 tick で CredentialService が再挑戦する。
                if ((status == 429 || status >= 500) && attempt < MaxRetries)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    await Task.Delay(delay);
                    continue;
                }
                return null;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Debug.WriteLine($"UsageApiService transient error attempt {attempt + 1}: {ex.Message}");
                if (attempt < MaxRetries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
                    continue;
                }
                return null;
            }
            catch (JsonException ex)
            {
                // レスポンス構造が変わっていたら retry しても直らないので即あきらめる。
                Debug.WriteLine($"UsageApiService JSON parse failed: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UsageApiService unexpected error: {ex}");
                return null;
            }
        }
        return null;
    }

    private static string GetClaudeCodeVersion()
    {
        if (_cachedVersion != null) return _cachedVersion;
        // cmd.exe /c 経由で叩く。生の "claude" は CreateProcess が PATHEXT を無視して
        // npm 版 .cmd shim を見つけられないため (sr-kai/claudeusagewin と同じ理由)。
        var version = TryRunAndExtractVersion("cmd.exe", "/c claude --version");
        _cachedVersion = version ?? FallbackVersion;
        Debug.WriteLine($"Claude Code version resolved to: {_cachedVersion}");
        return _cachedVersion;
    }

    private static string? TryRunAndExtractVersion(string fileName, string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            // "2.1.143 (Claude Code)" などから最初の x.y(.z) を抜く。
            var match = Regex.Match(output, @"\d+\.\d+(?:\.\d+)*");
            return match.Success ? match.Value : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"claude --version failed: {ex.Message}");
            return null;
        }
    }
}
