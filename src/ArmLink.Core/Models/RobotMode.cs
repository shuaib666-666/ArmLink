namespace ArmLink.Core.Models;

/// <summary>
/// 機台作業模式。
/// </summary>
public enum RobotMode
{
    /// <summary>手動模式：接受 Jog / Move 等即時操作。</summary>
    Manual = 0,

    /// <summary>自動模式：僅接受排程移動，拒絕 Jog（貼近真實機台的安全設計）。</summary>
    Auto = 1,
}
