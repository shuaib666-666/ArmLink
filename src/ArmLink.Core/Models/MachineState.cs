namespace ArmLink.Core.Models;

/// <summary>
/// 機台的生命週期狀態，同時決定 UI 上狀態指示燈的顏色。
/// 綠色 = Ready、黃色 = Moving / Connecting、紅色 = Error / EmergencyStop、灰色 = Unknown / Booting。
/// </summary>
public enum MachineState
{
    /// <summary>尚未收到任何狀態封包。</summary>
    Unknown = 0,

    /// <summary>開機自我檢測中。</summary>
    Booting = 1,

    /// <summary>閒置待命，可接受指令。</summary>
    Ready = 2,

    /// <summary>正在移動。</summary>
    Moving = 3,

    /// <summary>發生錯誤，需 RESET 復歸。</summary>
    Error = 4,

    /// <summary>急停觸發，所有運動立即凍結，需 RESET 復歸。</summary>
    EmergencyStop = 5,
}
