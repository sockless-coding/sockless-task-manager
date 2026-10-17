using System.Runtime.InteropServices;

namespace Sockless.TaskManager.Core;

/// <summary>
/// The desktop equivalent of <c>prefers-reduced-motion</c>: Windows' "Animation effects"
/// setting. When it is off, infinite animations (the live pulse) don't run (motion.md).
/// </summary>
public static partial class Motion
{
    private const uint SpiGetClientAreaAnimation = 0x1042;

    public static bool IsReduced
    {
        get
        {
            var enabled = 1;
            return SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref enabled, 0) && enabled == 0;
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);
}
