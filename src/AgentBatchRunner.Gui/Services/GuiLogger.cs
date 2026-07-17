using System.Windows.Threading;
using AgentBatchRunner.Services;

namespace AgentBatchRunner.Gui.Services;

public sealed class GuiLogger(Dispatcher dispatcher) : IRunEventSink
{
    public event EventHandler<RunEvent>? RunEventReceived;

    public Task OnRunEventAsync(RunEvent runEvent, CancellationToken cancellationToken)
    {
        if (dispatcher.CheckAccess())
        {
            RunEventReceived?.Invoke(this, runEvent);
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(
            () => RunEventReceived?.Invoke(this, runEvent),
            DispatcherPriority.Background,
            CancellationToken.None).Task;
    }
}
