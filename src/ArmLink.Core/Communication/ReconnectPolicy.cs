namespace ArmLink.Core.Communication;

/// <summary>
/// 自動重連的退避策略（Exponential Backoff + Jitter）。
/// 加抖動（Jitter）是刻意的：真實機台重啟時若有一堆客戶端同時重連，會造成瞬間流量風暴。
/// </summary>
public sealed record ReconnectPolicy
{
    /// <summary>第一次重連前的等待時間。</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>等待時間的上限，避免退避到天荒地老。</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>每次退避的倍率。</summary>
    public double BackoffFactor { get; init; } = 2.0;

    /// <summary>最多重試幾次；<c>null</c> 代表無限重試（工控現場通常是這樣）。</summary>
    public int? MaxAttempts { get; init; }

    /// <summary>是否加入 0.8~1.2 倍的隨機抖動。</summary>
    public bool UseJitter { get; init; } = true;

    /// <summary>預設策略：1 秒起跳、指數退避、上限 15 秒、無限重試。</summary>
    public static ReconnectPolicy Default { get; } = new();

    /// <summary>算出第 <paramref name="attempt"/> 次失敗後應該等待多久。</summary>
    /// <param name="attempt">目前失敗次數（從 1 開始）。</param>
    public TimeSpan GetDelay(int attempt)
    {
        if (attempt < 1)
        {
            attempt = 1;
        }

        var milliseconds = InitialDelay.TotalMilliseconds * Math.Pow(BackoffFactor, attempt - 1);
        milliseconds = Math.Min(milliseconds, MaxDelay.TotalMilliseconds);

        if (UseJitter)
        {
            milliseconds *= 0.8 + (Random.Shared.NextDouble() * 0.4);
        }

        return TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, 0, MaxDelay.TotalMilliseconds));
    }

    /// <summary>第 <paramref name="attempt"/> 次失敗後是否該繼續嘗試。</summary>
    /// <param name="attempt">目前失敗次數。</param>
    public bool ShouldRetry(int attempt) => MaxAttempts is null || attempt < MaxAttempts.Value;
}
