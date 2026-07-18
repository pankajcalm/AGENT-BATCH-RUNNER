using System.Windows.Threading;
using AgentBatchRunner.Services;

namespace AgentBatchRunner.Gui.Services;

public sealed class GuiPipelineEventSink(Dispatcher dispatcher) : IPipelineEventSink
{
    public event EventHandler<PipelineEvent>? PipelineEventReceived;

    public Task OnPipelineEventAsync(PipelineEvent pipelineEvent, CancellationToken cancellationToken)
    {
        if (dispatcher.CheckAccess())
        {
            PipelineEventReceived?.Invoke(this, pipelineEvent);
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(
            () => PipelineEventReceived?.Invoke(this, pipelineEvent),
            DispatcherPriority.Background,
            cancellationToken).Task;
    }
}
