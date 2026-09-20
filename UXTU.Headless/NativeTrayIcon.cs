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
    private const uint WmRButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;
    private const uint WmAppUpdate = 0x8001;
    private const uint WmTrayCallback = 0x0401;
    private const uint IconId = 1;
    private const uint ExitCommandId = 1001;

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
    private const uint TpmReturnCommand = 0x0100;
    private const uint TpmNonotify = 0x0080;

    private const int IdiApplication = 32512;
    private const int IdiError = 32513;

    private readonly object _stateGate = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Action _exitRequested;
    private readonly Thread _thread;
    private readonly string _className = $"MSIThrottleFix.Tray.{Guid.NewGuid():N}";
    private readonly WindowProcedure _windowProcedure;
    private TrayState _state = new("STARTING", 0, 0);
    private Exception? _startupError;
    private nint _window;
    private bool _iconAdded;
    private bool _disposed;

    public NativeTrayIcon(Action exitRequested)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The notification-area icon requires Windows.");

        _exitRequested = exitRequested;
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

            _window = CreateWindowEx(
                0, _className, "MSI Throttle Fix", 0,
                0, 0, 0, 0, 0, 0, module, 0);
            if (_window == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed.");

            AddTrayIcon();
            _ready.Set();

            while (GetMessage(out Message message, 0, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _ready.Set();
        }
        finally
        {
            RemoveTrayIcon();
            _window = 0;
            if (module != 0)
                UnregisterClass(_className, module);
        }
    }

    private nint WindowProc(nint window, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case WmAppUpdate:
                ModifyTrayIcon();
                return 0;

            case WmTrayCallback:
                uint mouseMessage = unchecked((uint)lParam.ToInt64()) & 0xFFFF;
                if (mouseMessage is WmRButtonUp or WmContextMenu)
                    ShowContextMenu(window);
                return 0;

            case WmCommand:
                if ((unchecked((uint)wParam.ToUInt64()) & 0xFFFF) == ExitCommandId)
                    _exitRequested();
                return 0;

            case WmClose:
                RemoveTrayIcon();
                DestroyWindow(window);
                return 0;

            case WmDestroy:
                PostQuitMessage(0);
                return 0;
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    private void AddTrayIcon()
    {
        NotifyIconData data = BuildIconData(includeCallback: true);
        if (!ShellNotifyIcon(NimAdd, ref data))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Shell_NotifyIcon(NIM_ADD) failed.");
        _iconAdded = true;
    }

    private void ModifyTrayIcon()
    {
        if (!_iconAdded)
            return;

        NotifyIconData data = BuildIconData(includeCallback: false);
        ShellNotifyIcon(NimModify, ref data);
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
            AppendMenu(menu, MfString, ExitCommandId, "Exit MSI Throttle Fix");

            GetCursorPos(out Point cursor);
            SetForegroundWindow(window);
            uint command = TrackPopupMenu(
                menu, TpmReturnCommand | TpmNonotify,
                cursor.X, cursor.Y, 0, window, 0);
            if (command == ExitCommandId)
                _exitRequested();
        }
        finally
        {
            DestroyMenu(menu);
        }
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
