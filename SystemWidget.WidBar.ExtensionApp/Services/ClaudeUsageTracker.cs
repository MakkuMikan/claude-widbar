using System.Diagnostics;
using SystemWidget.WidBar.ExtensionApp.Models;

namespace SystemWidget.WidBar.ExtensionApp.Services;

/// <summary>
/// <see cref="UsageApiService"/> の結果を 180 秒キャッシュし、同期の
/// <see cref="LocalTelemetryCollector.Sample"/> から常に即時読み取れるようにする。
///
/// <para>API を毎秒叩くと 429 バケット行きになるため、fire-and-forget の背景タスクで
/// リフレッシュし、UI 側は最終取得値を返し続ける。初回取得までは null。</para>
/// </summary>
internal sealed class ClaudeUsageTracker
{
    // Anthropic の非公式エンドポイントは 180 秒 / 呼び出し以上を推奨 (429 issue の多くがそれ未満)。
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(180);

    // 直近失敗時のクールダウン。連続失敗で API に負荷を掛けない。
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(60);

    private UsageData? _latest;
    private DateTime _lastSuccessUtc = DateTime.MinValue;
    private DateTime _lastAttemptUtc = DateTime.MinValue;
    private int _refreshInFlight;

    /// <summary>最後に取得できた usage。まだ一度も取れていなければ null。</summary>
    public UsageData? Latest => _latest;

    /// <summary>credentials が読める状態にあるかを sync でチェック (Preview UI 側の "--" 判定用)。</summary>
    public bool CredentialsExist => CredentialService.CredentialsExist();

    /// <summary>
    /// 必要なら背景で再取得を仕込む。呼び出し自体は即戻る (fire-and-forget)。
    /// 前回成功から 180 秒経ってなければ何もしない。
    /// </summary>
    public void EnsureFresh()
    {
        var now = DateTime.UtcNow;
        var sinceSuccess = now - _lastSuccessUtc;
        var sinceAttempt = now - _lastAttemptUtc;

        if (_latest != null && sinceSuccess < RefreshInterval) return;
        if (sinceAttempt < FailureCooldown && _latest != null) return;

        // 二重起動防止。既に走ってるなら乗らない。
        if (Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) != 0) return;

        _lastAttemptUtc = now;

        _ = Task.Run(async () =>
        {
            try
            {
                var data = await UsageApiService.GetUsageAsync();
                if (data != null)
                {
                    _latest = data;
                    _lastSuccessUtc = DateTime.UtcNow;
                }
                // 失敗時は _latest を消さない (画面が突然 "--" にならないように)。
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ClaudeUsageTracker refresh crashed: {ex}");
            }
            finally
            {
                Interlocked.Exchange(ref _refreshInFlight, 0);
            }
        });
    }
}
