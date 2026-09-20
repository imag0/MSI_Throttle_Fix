using System.Runtime.InteropServices;

namespace UXTU.Headless;

internal static class ConsoleWindow
{
    private const int SwHide = 0;

    public static void Hide()
    {
        if (!OperatingSystem.IsWindows())
            return;

        nint window = GetConsoleWindow();
        if (window != 0)
            ShowWindow(window, SwHide);
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetConsoleWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
}
