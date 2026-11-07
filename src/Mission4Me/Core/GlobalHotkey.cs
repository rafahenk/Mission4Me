using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace Mission4Me.Core;

/// <summary>Combinação de teclas (modificadores + tecla), com texto legível e formato de atalho .lnk.</summary>
public sealed record HotkeyDef(uint Modifiers, uint VirtualKey)
{
    public static HotkeyDef Default { get; } = new(GlobalHotkey.MOD_CONTROL | GlobalHotkey.MOD_ALT, 0x4D); // Ctrl+Alt+M

    public string KeyName => VirtualKey switch
    {
        >= 0x41 and <= 0x5A => ((char)VirtualKey).ToString(),       // A–Z
        >= 0x30 and <= 0x39 => ((char)VirtualKey).ToString(),       // 0–9
        >= 0x70 and <= 0x7B => "F" + (VirtualKey - 0x6F),           // F1–F12
        _ => KeyInterop.KeyFromVirtualKey((int)VirtualKey).ToString(),
    };

    public string Label
    {
        get
        {
            var parts = new List<string>();
            if ((Modifiers & GlobalHotkey.MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((Modifiers & GlobalHotkey.MOD_SHIFT) != 0) parts.Add("Shift");
            if ((Modifiers & GlobalHotkey.MOD_ALT) != 0) parts.Add("Alt");
            parts.Add(KeyName);
            return string.Join("+", parts);
        }
    }

    /// <summary>Formato aceito pela tecla de atalho de um .lnk (letras, números, F1–F12).</summary>
    public string? ShortcutText => IsSupportedKey(VirtualKey) ? Label.ToUpperInvariant() : null;

    public static bool IsSupportedKey(uint vk) =>
        vk is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39 or >= 0x70 and <= 0x7B;

    public static HotkeyDef? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        uint mods = 0, vk = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.ToUpperInvariant();
            if (t is "CTRL" or "CONTROL") mods |= GlobalHotkey.MOD_CONTROL;
            else if (t == "ALT") mods |= GlobalHotkey.MOD_ALT;
            else if (t == "SHIFT") mods |= GlobalHotkey.MOD_SHIFT;
            else if (t.Length == 1 && char.IsLetterOrDigit(t[0])) vk = t[0];
            else if (t.Length is 2 or 3 && t[0] == 'F' && int.TryParse(t[1..], out var f) && f is >= 1 and <= 12) vk = (uint)(0x6F + f);
            else return null;
        }
        var def = new HotkeyDef(mods, vk);
        return def.Validate() == null ? def : null;
    }

    /// <summary>Monta a partir de uma tecla pressionada numa janela WPF.</summary>
    public static HotkeyDef FromWpf(ModifierKeys mk, Key key)
    {
        uint mods = 0;
        if (mk.HasFlag(ModifierKeys.Control)) mods |= GlobalHotkey.MOD_CONTROL;
        if (mk.HasFlag(ModifierKeys.Alt)) mods |= GlobalHotkey.MOD_ALT;
        if (mk.HasFlag(ModifierKeys.Shift)) mods |= GlobalHotkey.MOD_SHIFT;
        return new HotkeyDef(mods, (uint)KeyInterop.VirtualKeyFromKey(key));
    }

    /// <summary>null = válido; senão, o motivo.</summary>
    public string? Validate()
    {
        if (!IsSupportedKey(VirtualKey)) return "Use uma letra, um número ou F1–F12.";
        bool ctrl = (Modifiers & GlobalHotkey.MOD_CONTROL) != 0, alt = (Modifiers & GlobalHotkey.MOD_ALT) != 0;
        int count = new[] { GlobalHotkey.MOD_CONTROL, GlobalHotkey.MOD_ALT, GlobalHotkey.MOD_SHIFT }.Count(m => (Modifiers & m) != 0);
        if (!(ctrl || alt) || count < 2)
            return "Combine pelo menos duas teclas entre Ctrl, Alt e Shift (com Ctrl ou Alt), para não roubar atalhos de outros programas.";
        return null;
    }
}

/// <summary>
/// Tecla de atalho global (funciona com qualquer janela em foco) via RegisterHotKey.
/// Se outro programa já usa a combinação, Register devolve false e LastError = 1409.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    public const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_NOREPEAT = 0x4000;
    public const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
    private const int WM_HOTKEY = 0x0312;
    private const int Id = 0x4642; // "FB"

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _src;
    private Action? _onPress;
    private bool _registered;

    public int LastError { get; private set; }

    public GlobalHotkey()
    {
        // janela de nível superior que nunca é exibida (sem WS_VISIBLE): só recebe WM_HOTKEY
        _src = new HwndSource(new HwndSourceParameters("Mission4Me.Hotkey") { Width = 0, Height = 0, WindowStyle = 0 });
        _src.AddHook(Hook);
    }

    public bool Register(HotkeyDef def, Action onPress)
    {
        Unregister();
        _onPress = onPress;
        _registered = RegisterHotKey(_src.Handle, Id, def.Modifiers | MOD_NOREPEAT, def.VirtualKey);
        LastError = _registered ? 0 : Marshal.GetLastWin32Error();
        Storage.LogInfo(_registered
            ? $"Atalho {def.Label} registrado"
            : $"Atalho {def.Label} FALHOU (erro Win32 {LastError}{(LastError == ERROR_HOTKEY_ALREADY_REGISTERED ? ": já registrado por outro programa" : "")})");
        return _registered;
    }

    public void Unregister()
    {
        if (_registered) UnregisterHotKey(_src.Handle, Id);
        _registered = false;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == Id) { _onPress?.Invoke(); handled = true; }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _src.RemoveHook(Hook);
        _src.Dispose();
    }
}
