using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Sdat.Core.Settings;
using Sdat.Windows.Hosting;

namespace Sdat.App;

internal sealed class CompanionController : IDisposable
{
    private readonly SdatRuntime _runtime;
    private readonly MainWindow _mainWindow;
    private readonly DispatcherQueue _dispatcher;
    private readonly NativeCompanionWindow _nativeWindow;
    private QuickPaletteWindow? _palette;
    private bool _disposed;

    public CompanionController(
        SdatRuntime runtime,
        MainWindow mainWindow,
        Action exit,
        bool showTrayIcon)
    {
        _runtime = runtime;
        _mainWindow = mainWindow;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _nativeWindow = new NativeCompanionWindow(
            () => Enqueue(ShowPalette),
            () => Enqueue(ShowMainWindow),
            () => Enqueue(exit),
            HotkeyGesture.Parse(runtime.CurrentSettings.PaletteHotkey),
            showTrayIcon);
    }

    public string? HotkeyRegistrationError => _nativeWindow.HotkeyRegistrationError;

    public void ApplySettings(AppSettings settings, bool showTrayIcon)
    {
        _nativeWindow.UpdateHotkey(HotkeyGesture.Parse(settings.PaletteHotkey));
        _nativeWindow.UpdateTrayIconVisibility(showTrayIcon);
    }

    public void ShowMainWindow()
    {
        _mainWindow.AppWindow.Show();
        _mainWindow.Activate();
    }

    public void ShowPalette()
    {
        if (_palette is not null)
        {
            _palette.Close();
            return;
        }

        _palette = new QuickPaletteWindow(_runtime);
        _palette.Closed += (_, _) => _palette = null;
        _palette.Activate();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _nativeWindow.Dispose();
    }

    private void Enqueue(Action action) => _dispatcher.TryEnqueue(() => action());

    private sealed class NativeCompanionWindow : IDisposable
    {
        private const int HotkeyId = 0x5344;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint ModShift = 0x0004;
        private const uint ModWindows = 0x0008;
        private const uint ModNoRepeat = 0x4000;
        private const uint WmHotkey = 0x0312;
        private const uint WmCommand = 0x0111;
        private const uint WmRightButtonUp = 0x0205;
        private const uint WmLeftButtonDoubleClick = 0x0203;
        private const uint TrayMessage = 0x8001;
        private const uint NimAdd = 0x00000000;
        private const uint NimDelete = 0x00000002;
        private const uint NifMessage = 0x00000001;
        private const uint NifIcon = 0x00000002;
        private const uint NifTip = 0x00000004;
        private const uint MfString = 0x00000000;
        private const uint MfSeparator = 0x00000800;
        private const uint TpmRightButton = 0x0002;
        private const uint CommandPalette = 1;
        private const uint CommandOpen = 2;
        private const uint CommandExit = 3;
        private static readonly IntPtr MessageOnlyParent = new(-3);
        private static readonly IntPtr DefaultApplicationIcon = new(32512);
        private readonly Action _showPalette;
        private readonly Action _showMain;
        private readonly Action _exit;
        private readonly WindowProcedure _windowProcedure;
        private readonly string _className = $"SDAT.Companion.{Guid.NewGuid():N}";
        private IntPtr _window;
        private NotificationIconData _iconData;
        private HotkeyGesture? _registeredHotkey;
        private bool _trayIconVisible;

        public NativeCompanionWindow(
            Action showPalette,
            Action showMain,
            Action exit,
            HotkeyGesture hotkey,
            bool showTrayIcon)
        {
            _showPalette = showPalette;
            _showMain = showMain;
            _exit = exit;
            _windowProcedure = WindowProc;
            RegisterWindowClass();
            _window = CreateWindowEx(
                0,
                _className,
                "SDAT Companion",
                0,
                0,
                0,
                0,
                0,
                MessageOnlyParent,
                IntPtr.Zero,
                GetModuleHandle(null),
                IntPtr.Zero);
            if (_window == IntPtr.Zero)
            {
                throw new InvalidOperationException("The SDAT companion message window could not be created.");
            }

            _iconData = new NotificationIconData
            {
                Size = Marshal.SizeOf<NotificationIconData>(),
                Window = _window,
                Id = 1,
                Flags = NifMessage | NifIcon | NifTip,
                CallbackMessage = TrayMessage,
                Icon = LoadIcon(IntPtr.Zero, DefaultApplicationIcon),
                Tip = AppText.Get("TrayTooltip", "ShutdownAT power scheduler"),
                Info = string.Empty,
                InfoTitle = string.Empty,
            };
            UpdateTrayIconVisibility(showTrayIcon);

            if (!TryRegisterHotkey(hotkey))
            {
                HotkeyRegistrationError = $"{hotkey} is already registered by another application.";
            }
        }

        public string? HotkeyRegistrationError { get; private set; }

        public void UpdateHotkey(HotkeyGesture hotkey)
        {
            if (_registeredHotkey == hotkey)
            {
                return;
            }

            var previous = _registeredHotkey;
            if (previous is not null)
            {
                UnregisterHotKey(_window, HotkeyId);
                _registeredHotkey = null;
            }

            if (TryRegisterHotkey(hotkey))
            {
                HotkeyRegistrationError = null;
                return;
            }

            if (previous is not null && !TryRegisterHotkey(previous.Value))
            {
                HotkeyRegistrationError = "The previous palette hotkey could not be restored.";
                throw new InvalidOperationException(
                    $"{hotkey} is unavailable, and the previous hotkey could not be restored.");
            }

            HotkeyRegistrationError = $"{hotkey} is already registered by another application.";
            throw new InvalidOperationException(HotkeyRegistrationError);
        }

        public void UpdateTrayIconVisibility(bool visible)
        {
            if (_trayIconVisible == visible)
            {
                return;
            }

            if (visible)
            {
                if (!ShellNotifyIcon(NimAdd, ref _iconData))
                {
                    throw new InvalidOperationException("The SDAT tray icon could not be created.");
                }

                _trayIconVisible = true;
                return;
            }

            ShellNotifyIcon(NimDelete, ref _iconData);
            _trayIconVisible = false;
        }

        private bool TryRegisterHotkey(HotkeyGesture hotkey)
        {
            if (!RegisterHotKey(
                    _window,
                    HotkeyId,
                    ToNativeModifiers(hotkey.Modifiers) | ModNoRepeat,
                    ToVirtualKey(hotkey.Key)))
            {
                return false;
            }

            _registeredHotkey = hotkey;
            return true;
        }

        private static uint ToNativeModifiers(HotkeyModifiers modifiers)
        {
            uint value = 0;
            if (modifiers.HasFlag(HotkeyModifiers.Control)) value |= ModControl;
            if (modifiers.HasFlag(HotkeyModifiers.Alt)) value |= ModAlt;
            if (modifiers.HasFlag(HotkeyModifiers.Shift)) value |= ModShift;
            if (modifiers.HasFlag(HotkeyModifiers.Windows)) value |= ModWindows;
            return value;
        }

        private static uint ToVirtualKey(string key) => key[0] == 'F'
            ? checked((uint)(0x70 + int.Parse(
                key.AsSpan(1),
                System.Globalization.CultureInfo.InvariantCulture) - 1))
            : key[0];

        public void Dispose()
        {
            if (_window == IntPtr.Zero)
            {
                return;
            }

            if (_trayIconVisible)
            {
                ShellNotifyIcon(NimDelete, ref _iconData);
                _trayIconVisible = false;
            }
            if (_registeredHotkey is not null)
            {
                UnregisterHotKey(_window, HotkeyId);
                _registeredHotkey = null;
            }
            DestroyWindow(_window);
            _window = IntPtr.Zero;
            UnregisterClass(_className, GetModuleHandle(null));
        }

        private void RegisterWindowClass()
        {
            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
                Instance = GetModuleHandle(null),
                ClassName = _className,
            };
            if (RegisterClassEx(ref windowClass) == 0)
            {
                throw new InvalidOperationException("The SDAT companion window class could not be registered.");
            }
        }

        private IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
            {
                _showPalette();
                return IntPtr.Zero;
            }

            if (message == TrayMessage)
            {
                var mouseMessage = unchecked((uint)lParam.ToInt64());
                if (mouseMessage == WmLeftButtonDoubleClick)
                {
                    _showPalette();
                }
                else if (mouseMessage == WmRightButtonUp)
                {
                    ShowContextMenu();
                }

                return IntPtr.Zero;
            }

            if (message == WmCommand)
            {
                switch (unchecked((uint)wParam.ToInt64()) & 0xffff)
                {
                    case CommandPalette:
                        _showPalette();
                        break;
                    case CommandOpen:
                        _showMain();
                        break;
                    case CommandExit:
                        _exit();
                        break;
                }

                return IntPtr.Zero;
            }

            return DefWindowProc(window, message, wParam, lParam);
        }

        private void ShowContextMenu()
        {
            var menu = CreatePopupMenu();
            if (menu == IntPtr.Zero)
            {
                return;
            }

            try
            {
                AppendMenu(menu, MfString, CommandPalette, AppText.Get("TrayQuickSchedule", "Quick schedule"));
                AppendMenu(menu, MfString, CommandOpen, AppText.Get("TrayOpen", "Open ShutdownAT"));
                AppendMenu(menu, MfSeparator, 0, null);
                AppendMenu(menu, MfString, CommandExit, AppText.Get("TrayExit", "Exit"));
                GetCursorPos(out var cursor);
                SetForegroundWindow(_window);
                TrackPopupMenu(menu, TpmRightButton, cursor.X, cursor.Y, 0, _window, IntPtr.Zero);
            }
            finally
            {
                DestroyMenu(menu);
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WindowClass
        {
            public uint Size;
            public uint Style;
            public IntPtr WindowProcedure;
            public int ClassExtra;
            public int WindowExtra;
            public IntPtr Instance;
            public IntPtr Icon;
            public IntPtr Cursor;
            public IntPtr Background;
            public string? MenuName;
            public string ClassName;
            public IntPtr SmallIcon;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NotificationIconData
        {
            public int Size;
            public IntPtr Window;
            public uint Id;
            public uint Flags;
            public uint CallbackMessage;
            public IntPtr Icon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
            public uint State;
            public uint StateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
            public uint TimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
            public uint InfoFlags;
            public Guid ItemGuid;
            public IntPtr BalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X;
            public int Y;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? moduleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref WindowClass windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool UnregisterClass(string className, IntPtr instance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            uint extendedStyle,
            string className,
            string windowName,
            uint style,
            int x,
            int y,
            int width,
            int height,
            IntPtr parent,
            IntPtr menu,
            IntPtr instance,
            IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr window, int id);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

        [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
        private static extern bool ShellNotifyIcon(uint message, ref NotificationIconData data);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr menu, uint flags, uint itemId, string? text);

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr menu);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool TrackPopupMenu(
            IntPtr menu,
            uint flags,
            int x,
            int y,
            int reserved,
            IntPtr window,
            IntPtr rectangle);
    }
}
