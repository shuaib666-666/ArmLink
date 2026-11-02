using System.Windows.Threading;

namespace ArmLink.UI.Services;

/// <summary>
/// UI 緒列的抽象介面。ViewModel 只認這個介面，因此未來要換成 WinUI / Avalonia 都不用改商業邏輯。
/// </summary>
public interface IUiDispatcher
{
    /// <summary>把工作丟到 UI 緒列執行（非同步，不等候）。</summary>
    /// <param name="action">要執行的工作。</param>
    void Post(Action action);

    /// <summary>在 UI 緒列執行並等它完成。</summary>
    /// <param name="action">要執行的工作。</param>
    Task InvokeAsync(Action action);
}

/// <summary>
/// WPF 版本緒列排程器的實作。所有從背景緒列（Socket 讀取緒、模擬器廣播緒）來的更新都必須經過它。
/// </summary>
public sealed class WpfDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    /// <summary>建立排程器。</summary>
    /// <param name="dispatcher">WPF 的 Dispatcher（通常是 Application.Current.Dispatcher）。</param>
    public WpfDispatcher(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

    /// <inheritdoc/>
    public void Post(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = _dispatcher.BeginInvoke(action);
    }

    /// <inheritdoc/>
    public Task InvokeAsync(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(action).Task;
    }
}
