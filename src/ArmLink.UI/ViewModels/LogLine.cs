namespace ArmLink.UI.ViewModels;

/// <summary>
/// 日誌主控台裡的一列。
/// </summary>
public sealed class LogLine
{
    /// <summary>建立一列日誌。</summary>
    /// <param name="time">顯示用時間字串。</param>
    /// <param name="direction">TX / RX / SYS。</param>
    /// <param name="content">內容。</param>
    public LogLine(string time, string direction, string content)
    {
        Time = time;
        Direction = direction;
        Content = content;
    }

    /// <summary>時間（HH:mm:ss.fff）。</summary>
    public string Time { get; }

    /// <summary>方向標籤。</summary>
    public string Direction { get; }

    /// <summary>內容。</summary>
    public string Content { get; }
}
