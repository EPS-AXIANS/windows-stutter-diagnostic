using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace StutterDiag.Gui.Services;

/// <summary>
/// Registers the global Gaming-Mode hotkey (default <c>Ctrl+Alt+F12</c>, parsed from the
/// config string). On press it records a user-marked stutter through IPC and shows a toast.
/// </summary>
/// <remarks>
/// Primary path is Win32 <c>RegisterHotKey</c> on a message-only window — cheap, and the
/// least likely to upset anti-cheat software. If that fails (e.g. the combo is already
/// owned by another process) it falls back to a low-level keyboard hook
/// (<c>SetWindowsHookEx(WH_KEYBOARD_LL)</c>).
/// <para>
/// Anti-cheat caveat: a global WH_KEYBOARD_LL hook installs a system-wide keyboard callback.
/// Some kernel-level anti-cheat products treat that as suspicious and may warn or block it.
/// It is strictly a fallback; RegisterHotKey is preferred and tried first.
/// </para>
/// </remarks>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0xB01D;

    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;

    [Flags]
    private enum Mod : uint
    {
        None = 0,
        Alt = 0x0001,
        Control = 0x0002,
        Shift = 0x0004,
        Win = 0x0008,
        NoRepeat = 0x4000
    }

    private readonly ServiceConnection _service;
    private readonly ToastService _toast;

    private HwndSource? _source;
    private string _spec = "Ctrl+Alt+F12";
    private bool _active;

    // Low-level hook state (only used if RegisterHotKey fails).
    private IntPtr _hookHandle = IntPtr.Zero;
    private LowLevelKeyboardProc? _hookProc;   // kept alive against GC
    private Mod _mods;
    private uint _vk;
    private bool _usingLowLevelHook;

    public HotkeyService(ServiceConnection service, ToastService toast)
    {
        _service = service;
        _toast = toast;
    }

    /// <summary>Raised on the UI thread whenever the hotkey fires (before the async mark call).</summary>
    public event EventHandler? HotkeyPressed;

    public bool IsRegistered => _active;

    public bool UsingLowLevelHookFallback => _usingLowLevelHook;

    public void Initialize(string? hotkeySpec)
    {
        if (!string.IsNullOrWhiteSpace(hotkeySpec)) _spec = hotkeySpec!;

        // Message-only window: parent = HWND_MESSAGE (-3). No visible surface.
        var p = new HwndSourceParameters("StutterDiag.Gui.HotkeyWindow", 0, 0)
        {
            ParentWindow = new IntPtr(-3),
            WindowStyle = 0
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
        Register();
    }

    /// <summary>Re-register with a new spec (called after the Settings page saves config).</summary>
    public void UpdateHotkey(string? hotkeySpec)
    {
        if (string.IsNullOrWhiteSpace(hotkeySpec) || hotkeySpec == _spec) return;
        _spec = hotkeySpec!;
        Unregister();
        Register();
    }

    private void Register()
    {
        if (_source is null) return;

        if (!TryParseSpec(_spec, out _mods, out _vk))
        {
            GuiLog.Warn($"Gaming-Mode hotkey '{_spec}' could not be parsed; hotkey disabled.");
            return;
        }

        if (RegisterHotKey(_source.Handle, HotkeyId, (uint)(_mods | Mod.NoRepeat), _vk))
        {
            _active = true;
            _usingLowLevelHook = false;
            GuiLog.Info($"Gaming-Mode hotkey registered ({_spec}) via RegisterHotKey.");
            return;
        }

        GuiLog.Warn($"RegisterHotKey failed for '{_spec}' (combo likely owned elsewhere); " +
                    "installing a low-level keyboard hook fallback.");
        InstallLowLevelHook();
    }

    private void Unregister()
    {
        if (_source is not null)
            UnregisterHotKey(_source.Handle, HotkeyId);

        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
        _hookProc = null;
        _usingLowLevelHook = false;
        _active = false;
    }

    private void InstallLowLevelHook()
    {
        _hookProc = HookCallback;
        using var proc = System.Diagnostics.Process.GetCurrentProcess();
        var moduleName = proc.MainModule?.ModuleName ?? "StutterDiag.Gui.exe";
        _hookHandle = SetWindowsHookEx(WhKeyboardLl, _hookProc, GetModuleHandle(moduleName), 0);

        if (_hookHandle == IntPtr.Zero)
        {
            GuiLog.Error("SetWindowsHookEx(WH_KEYBOARD_LL) failed; Gaming-Mode hotkey is unavailable.");
            _hookProc = null;
            return;
        }
        _usingLowLevelHook = true;
        _active = true;
        GuiLog.Info($"Gaming-Mode hotkey active ({_spec}) via low-level keyboard hook (fallback).");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Fire();
        }
        return IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int m = wParam.ToInt32();
            if (m is WmKeyDown or WmSysKeyDown)
            {
                var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                if (data.VkCode == _vk && ModifiersMatch(_mods))
                    Fire();
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private static bool KeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static bool ModifiersMatch(Mod mods)
    {
        bool ctrl = KeyDown(VkControl);
        bool alt = KeyDown(VkMenu);
        bool shift = KeyDown(VkShift);
        bool win = KeyDown(VkLWin) || KeyDown(VkRWin);
        return ctrl == mods.HasFlag(Mod.Control)
               && alt == mods.HasFlag(Mod.Alt)
               && shift == mods.HasFlag(Mod.Shift)
               && win == mods.HasFlag(Mod.Win);
    }

    private void Fire()
    {
        HotkeyPressed?.Invoke(this, EventArgs.Empty);
        _ = MarkAsync();
    }

    private async Task MarkAsync()
    {
        try
        {
            var result = await _service.MarkStutterAsync("Gaming-Mode hotkey").ConfigureAwait(true);
            string stamp = FormatLocal(result?.TimestampUtcIso);
            _toast.Show("USER MARKED STUTTER", stamp);
            GuiLog.Info($"User marked stutter at {stamp} (accepted={result?.Accepted.ToString() ?? "n/a"}).");
        }
        catch (Exception ex)
        {
            GuiLog.Error("MarkStutter from hotkey failed", ex);
            _toast.Show("USER MARKED STUTTER", "not recorded — service unavailable");
        }
    }

    private static string FormatLocal(string? iso)
    {
        if (!string.IsNullOrEmpty(iso) &&
            DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
        {
            return dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        return DateTime.UtcNow.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static bool TryParseSpec(string spec, out Mod mods, out uint vk)
    {
        mods = Mod.None;
        vk = 0;
        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        for (int i = 0; i < parts.Length; i++)
        {
            bool isLast = i == parts.Length - 1;
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control": mods |= Mod.Control; break;
                case "alt":
                case "menu": mods |= Mod.Alt; break;
                case "shift": mods |= Mod.Shift; break;
                case "win":
                case "windows":
                case "super": mods |= Mod.Win; break;
                default:
                    if (!isLast || !TryParseKey(parts[i], out vk)) return false;
                    break;
            }
        }
        return vk != 0;
    }

    private static bool TryParseKey(string token, out uint vk)
    {
        vk = 0;
        if (Enum.TryParse<Key>(token, ignoreCase: true, out var key) && key != Key.None)
        {
            int v = KeyInterop.VirtualKeyFromKey(key);
            if (v != 0) { vk = (uint)v; return true; }
        }
        if (token.Length == 1)
        {
            char c = char.ToUpperInvariant(token[0]);
            if (c is (>= 'A' and <= 'Z') or (>= '0' and <= '9')) { vk = c; return true; }
        }
        return false;
    }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
        _source = null;
    }

    // ---- P/Invoke ----------------------------------------------------------------------

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
