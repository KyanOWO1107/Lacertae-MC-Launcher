using System.Runtime.InteropServices;
using Lacertae.Application.Accessibility;

namespace Lacertae.Platform.Windows.Accessibility;

public sealed class WindowsMotionPreference : IMotionPreference
{
    private const uint SpiGetClientAreaAnimation = 0x1042;

    public bool ReduceMotion
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            return !TryGetClientAreaAnimation(out bool animationsEnabled) || !animationsEnabled;
        }
    }

    private static bool TryGetClientAreaAnimation(out bool animationsEnabled)
    {
        int value;
        try
        {
            bool succeeded = SystemParametersInfo(SpiGetClientAreaAnimation, 0, out value, 0);
            animationsEnabled = value != 0;
            return succeeded;
        }
        catch (DllNotFoundException)
        {
            animationsEnabled = true;
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            animationsEnabled = true;
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint action,
        uint parameter,
        out int value,
        uint updateIniFile);
}
