using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using SystemWidget.WidBar.ExtensionApp.Models;

namespace SystemWidget.WidBar.ExtensionApp.Services;

/// <summary>
/// Fetches usage from Claude Code's undocumented <c>/api/oauth/usage</c> endpoint.
/// <para>Always sends the same headers as the Claude Code CLI (<c>User-Agent: claude-code/&lt;version&gt;</c> and
/// <c>anthropic-beta: oauth-2025-04-20</c>). Leaving them off lands requests in the 429 bucket.</para>
/// </summary>
internal static class UsageApiService
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private const string UsageApiUrl = "https://api.anthropic.com/api/oauth/usage";

    // On 429 / 5xx, retry up to this many times with exponential backoff (1s, 2s, 4s, 8s, 16s).
    private const int MaxRetries = 5;

    // Fallback User-Agent version if `claude --version` can't be run.
    // Anthropic uses it for bucketing, so keep it close to a real Claude Code version.
    private const string FallbackVersion = "2.1.100";

    private static string? _cachedVersion;

    /// <summary>
    /// Fetches usage. Returns null if it can't (no credentials, dead refresh token, persistent 429s, etc.).
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

                // Retry 429 / 5xx with exponential backoff. Anything else (e.g. 401 expired) won't change by
                // refreshing again right now, so return null. CredentialService tries again on the next tick.
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
                // If the response shape has changed, retrying won't help, so give up immediately.
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
        // Run via cmd.exe /c. A bare "claude" fails because CreateProcess ignores PATHEXT and
        // can't find the npm .cmd shim (same reason as sr-kai/claudeusagewin).
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
            // Extract the first x.y(.z) from output like "2.1.143 (Claude Code)".
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
