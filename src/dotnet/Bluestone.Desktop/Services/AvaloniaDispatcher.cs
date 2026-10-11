using Avalonia.Threading;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Services;

internal sealed class AvaloniaDispatcher : IUiDispatcher
{
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
