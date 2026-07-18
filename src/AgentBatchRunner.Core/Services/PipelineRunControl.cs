namespace AgentBatchRunner.Services;

public sealed class PipelineRunControl
{
    private int _pauseRequested;
    private int _stopRequested;

    public bool PauseRequested => Volatile.Read(ref _pauseRequested) == 1;

    public bool StopRequested => Volatile.Read(ref _stopRequested) == 1;

    public void RequestPauseAfterCurrentFile()
    {
        Interlocked.Exchange(ref _pauseRequested, 1);
    }

    public void RequestStop()
    {
        Interlocked.Exchange(ref _stopRequested, 1);
    }
}
