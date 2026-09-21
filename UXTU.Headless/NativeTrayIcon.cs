using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UXTU.Headless;

/// <summary>
/// Minimal Win32 notification-area icon. This intentionally avoids WPF and
/// Windows Forms; its thread blocks in GetMessage while the worker sleeps.
/// </summary>
internal sealed class NativeTrayIcon : IDisposable
{
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const uint WmCommand = 0x0111;
    private const uint WmTimer = 0x0113;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;
    private const uint WmAppUpdate = 0x8001;
    private const uint WmTrayCallback = 0x0401;
    private const uint IconId = 1;
    private const uint ExitCommandId = 1001;
    private const uint BalancedMinusSecondCommandId = 2001;
    private const uint BalancedMinusQuarterCommandId = 2002;
    private const uint BalancedPlusQuarterCommandId = 2003;
    private const uint BalancedPlusSecondCommandId = 2004;
    private const uint ExtremeMinusSecondCommandId = 2101;
    private const uint ExtremeMinusQuarterCommandId = 2102;
    private const uint ExtremePlusQuarterCommandId = 2103;
    private const uint ExtremePlusSecondCommandId = 2104;
    private const uint ResetTimingsCommandId = 2201;
    private const nuint RetryTimerId = 1;

    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;

    private const uint MfString = 0x00000000;
    private const uint MfDisabled = 0x00000002;
    private const uint MfGrayed = 0x00000001;
    private const uint MfSeparator = 0x00000800;
    private const uint MfPopup = 0x00000010;
    private const uint TpmReturnCommand = 0x0100;
    private const uint TpmNonotify = 0x0080;

    private const int IdiApplication = 32512;
    private const int IdiError = 32513;

    private readonly object _stateGate = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Action _exitRequested;
    private readonly Action<string, int?> _statusChanged;
    private readonly CycleTimingSettings? _timings;
    private readonly Thread _thread;
    private readonly string _className = $"MSIThrottleFix.Tray.{Guid.NewGuid():N}";
    private readonly WindowProcedure _windowProcedure;
    private TrayState _state = new("STARTING", 0, 0);
    private Exception? _startupError;
    private nint _window;
    private bool _iconAdded;
    private bool _everAdded;
    private bool _failureReported;
    private uint _taskbarCreated;
    private volatile bool _disposed;

    public NativeTrayIcon(
        Action exitRequested,
        Action<string, int?> statusChanged,
        CycleTimingSettings? timings = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The notification-area icon requires Windows.");

        _exitRequested = exitRequested;
        _statusChanged = statusChanged;
        _timings = timings;
        _windowProcedure = WindowProc;
        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "MSIThrottleFix notification icon"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("Timed out while creating the notification-area icon.");
        if (_startupError is not null)
            throw new InvalidOperationException("Could not create the notification-area icon.", _startupError);
    }

    public void Update(string phase, long cycle, long failures)
    {
        lock (_stateGate)
            _state = new TrayState(phase, cycle, failures);

        nint window = Volatile.Read(ref _window);
        if (window != 0)
            PostMessage(window, WmAppUpdate, 0, 0);
    }

    public bool Failed => Volatile.Read(ref _startupError) is not null;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        nint window = Volatile.Read(ref _window);
        if (window != 0)
            PostMessage(window, WmClose, 0, 0);

        if (_thread.IsAlive && Thread.CurrentThread != _thread)
            _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    private void MessageLoop()
    {
        nint module = GetModuleHandle(null);
        try
        {
            var windowClass = new WindowClass
            {
                Instance = module,
                ClassName = _className,
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(_windowProcedure)
            };

            if (RegisterClass(ref windowClass) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClass failed.");

            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            if (_taskbarCreated == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterWindowMessage failed.");

            _window = CreateWindowEx(
                0, _className, "MSI Throttle Fix", 0,
                0, 0, 0, 0, 0, 0, module, 0);
            if (_window == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed.");

            if (SetTimer(_window, RetryTimerId, 5_000, 0) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetTimer failed.");

            TryAddTrayIcon();
            _ready.Set();

            while (true)
            {
                int result = GetMessage(out Message message, 0, 0, 0);
                if (result == 0)
                    break;
                if (result < 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "GetMessage failed.");
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }

            if (!_disposed)
                throw new InvalidOperationException("The tray message loop stopped unexpectedly.");
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _ready.Set();
            _statusChanged("tray_thread_failed", ex.HResult);
        }
        finally
        {
            if (_window != 0)
                KillTimer(_window, RetryTimerId);
            RemoveTrayIcon();
            if (_window != 0)
                DestroyWindow(_window);
            _window = 0;
            if (module != 0)
                UnregisterClass(_className, module);
        }
    }

    private nint WindowProc(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == _taskbarCreated)
        {
            _iconAdded = false;
            TryAddTrayIcon();
            return 0;
        }

        switch (message)
        {
            case WmAppUpdate:
                ModifyTrayIcon();
                return 0;

            case WmTimer when wParam == RetryTimerId:
                if (_iconAdded)
                    ModifyTrayIcon();
                else
                    TryAddTrayIcon();
                return 0;

            case WmTrayCallback:
                uint mouseMessage = unchecked((uint)lParam.ToInt64()) & 0xFFFF;
                if (mouseMessage is WmRButtonUp or WmContextMenu)
                    ShowContextMenu(window);
                return 0;

            case WmCommand:
                HandleCommand(unchecked((uint)wParam.ToUInt64()) & 0xFFFF);
                return 0;

            case WmClose:
                RemoveTrayIcon();
                DestroyWindow(window);
                return 0;

            case WmDestroy:
                RemoveTrayIcon();
                _window = 0;
                PostQuitMessage(0);
                return 0;
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    private void TryAddTrayIcon()
    {
        NotifyIconData data = BuildIconData(includeCallback: true);
        if (!ShellNotifyIcon(NimAdd, ref data) && !ShellNotifyIcon(NimModify, ref data))
        {
            ReportFailure(Marshal.GetLastWin32Error());
            return;
        }

        _iconAdded = true;
        _failureReported = false;
        _statusChanged(_everAdded ? "tray_restored" : "tray_ready", null);
        _everAdded = true;
    }

    private void ModifyTrayIcon()
    {
        if (!_iconAdded)
            return;

        NotifyIconData data = BuildIconData(includeCallback: false);
        if (!ShellNotifyIcon(NimModify, ref data))
        {
            _iconAdded = false;
            ReportFailure(Marshal.GetLastWin32Error());
            TryAddTrayIcon();
        }
    }

    private void ReportFailure(int errorCode)
    {
        if (_failureReported)
            return;

        _failureReported = true;
        _statusChanged("tray_unavailable", errorCode);
    }

    private void RemoveTrayIcon()
    {
        if (!_iconAdded || _window == 0)
            return;

        var data = new NotifyIconData
        {
            Size = Marshal.SizeOf<NotifyIconData>(),
            Window = _window,
            Id = IconId
        };
        ShellNotifyIcon(NimDelete, ref data);
        _iconAdded = false;
    }

    private NotifyIconData BuildIconData(bool includeCallback)
    {
        TrayState state;
        lock (_stateGate)
            state = _state;

        string tooltip = state.Cycle == 0
            ? $"MSI Throttle Fix | {state.Phase}"
            : $"MSI Throttle Fix | {state.Phase} | Cycle {state.Cycle} | Failures {state.Failures}";

        return new NotifyIconData
        {
            Size = Marshal.SizeOf<NotifyIconData>(),
            Window = _window,
            Id = IconId,
            Flags = NifIcon | NifTip | (includeCallback ? NifMessage : 0),
            CallbackMessage = WmTrayCallback,
            Icon = LoadIcon(0, state.Failures == 0 ? (nint)IdiApplication : (nint)IdiError),
            Tip = tooltip.Length <= 127 ? tooltip : tooltip[..127]
        };
    }

    private void ShowContextMenu(nint window)
    {
        nint menu = CreatePopupMenu();
        if (menu == 0)
            return;

        try
        {
            TrayState state;
            lock (_stateGate)
                state = _state;

            string status = state.Cycle == 0
                ? "Starting"
                : $"{state.Phase} - cycle {state.Cycle} - failures {state.Failures}";
            AppendMenu(menu, MfString | MfDisabled | MfGrayed, 0, status);
            AppendMenu(menu, MfSeparator, 0, null);

            if (_timings is not null)
            {
                CycleTimingSnapshot timing = _timings.Snapshot();
                nint balancedMenu = CreatePopupMenu();
                nint extremeMenu = CreatePopupMenu();
                if (balancedMenu != 0 && extremeMenu != 0)
                {
                    AppendAdjustment(balancedMenu, BalancedMinusSecondCommandId,
                        "Shorter by 1 second", _timings.CanAdjustBalanced(-1_000));
                    AppendAdjustment(balancedMenu, BalancedMinusQuarterCommandId,
                        "Shorter by 250 ms", _timings.CanAdjustBalanced(-250));
                    AppendAdjustment(balancedMenu, BalancedPlusQuarterCommandId,
                        "Longer by 250 ms", _timings.CanAdjustBalanced(250));
                    AppendAdjustment(balancedMenu, BalancedPlusSecondCommandId,
                        "Longer by 1 second", _timings.CanAdjustBalanced(1_000));

                    AppendAdjustment(extremeMenu, ExtremeMinusSecondCommandId,
                        "Shorter by 1 second", _timings.CanAdjustExtreme(-1_000));
                    AppendAdjustment(extremeMenu, ExtremeMinusQuarterCommandId,
                        "Shorter by 250 ms", _timings.CanAdjustExtreme(-250));
                    AppendAdjustment(extremeMenu, ExtremePlusQuarterCommandId,
                        "Longer by 250 ms", _timings.CanAdjustExtreme(250));
                    AppendAdjustment(extremeMenu, ExtremePlusSecondCommandId,
                        "Longer by 1 second", _timings.CanAdjustExtreme(1_000));

                    AppendMenu(menu, MfPopup, unchecked((nuint)balancedMenu.ToInt64()),
                        $"Balanced wait: {timing.BalancedMilliseconds} ms");
                    AppendMenu(menu, MfPopup, unchecked((nuint)extremeMenu.ToInt64()),
                        $"Extreme wait: {timing.ExtremeMilliseconds} ms");
                    AppendMenu(menu, MfString, ResetTimingsCommandId, "Reset both waits to startup defaults");
                    AppendMenu(menu, MfSeparator, 0, null);
                }
                else
                {
                    if (balancedMenu != 0) DestroyMenu(balancedMenu);
                    if (extremeMenu != 0) DestroyMenu(extremeMenu);
                }
            }

            AppendMenu(menu, MfString, ExitCommandId, "Exit MSI Throttle Fix");

            GetCursorPos(out Point cursor);
            SetForegroundWindow(window);
            uint command = TrackPopupMenu(
                menu, TpmReturnCommand | TpmNonotify,
                cursor.X, cursor.Y, 0, window, 0);
            HandleCommand(command);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static void AppendAdjustment(nint menu, uint command, string label, bool enabled) =>
        AppendMenu(menu, MfString | (enabled ? 0 : MfDisabled | MfGrayed), command, label);

    private void HandleCommand(uint command)
    {
        if (command == ExitCommandId)
        {
            _exitRequested();
            return;
        }

        if (_timings is null)
            return;

        bool changed = command switch
        {
            BalancedMinusSecondCommandId => _timings.AdjustBalanced(-1_000),
            BalancedMinusQuarterCommandId => _timings.AdjustBalanced(-250),
            BalancedPlusQuarterCommandId => _timings.AdjustBalanced(250),
            BalancedPlusSecondCommandId => _timings.AdjustBalanced(1_000),
            ExtremeMinusSecondCommandId => _timings.AdjustExtreme(-1_000),
            ExtremeMinusQuarterCommandId => _timings.AdjustExtreme(-250),
            ExtremePlusQuarterCommandId => _timings.AdjustExtreme(250),
            ExtremePlusSecondCommandId => _timings.AdjustExtreme(1_000),
            ResetTimingsCommandId => _timings.RestoreDefaults(),
            _ => false
        };

        if (changed)
            ModifyTrayIcon();
    }

    private sealed record TrayState(string Phase, long Cycle, long Failures);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style;
        public nint WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Value;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Point Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint TimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid GuidItem;
        public nint BalloonIcon;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", EntryPoint = "RegisterClassW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WindowClass windowClass);

    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(string className, nint instance);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nuint SetTimer(nint window, nuint timerId, uint milliseconds, nint callback);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool KillTimer(nint window, nuint timerId);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref Message message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    private static extern nint LoadIcon(nint instance, nint iconName);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(nint menu, uint flags, nuint item, string? text);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(
        nint menu,
        uint flags,
        int x,
        int y,
        int reserved,
        nint window,
        nint rectangle);
}
