using InputExecutor.Core.Protocol;
using InputExecutor.Windows;

namespace InputExecutor.Service;

/// <summary>
/// Observation surface for physical Raw Input without requiring recording Start.
/// </summary>
public interface IPhysicalInputObservationSource
{
    event EventHandler<HumanInputFrame>? FrameObserved;
    event EventHandler<HumanInputFrame>? TaggedRawFrameObserved;
}

internal sealed class WindowsPhysicalInputObservationSource : IPhysicalInputObservationSource
{
    private readonly WindowsRawInputRecorder _recorder;

    public WindowsPhysicalInputObservationSource(WindowsRawInputRecorder recorder) => _recorder = recorder;

    public event EventHandler<HumanInputFrame>? FrameObserved
    {
        add => _recorder.FrameObserved += value;
        remove => _recorder.FrameObserved -= value;
    }

    public event EventHandler<HumanInputFrame>? TaggedRawFrameObserved
    {
        add => _recorder.TaggedRawFrameObserved += value;
        remove => _recorder.TaggedRawFrameObserved -= value;
    }
}
