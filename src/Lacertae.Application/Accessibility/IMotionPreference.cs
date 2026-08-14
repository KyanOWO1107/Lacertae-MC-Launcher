namespace Lacertae.Application.Accessibility;

/// <summary>
/// Reads the user's operating-system motion preference without exposing platform APIs to the application layer.
/// </summary>
public interface IMotionPreference
{
    bool ReduceMotion { get; }
}
