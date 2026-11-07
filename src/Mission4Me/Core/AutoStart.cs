using System;
using Microsoft.Win32;

namespace Mission4Me.Core;

/// <summary>Iniciar com o Windows via HKCU\...\Run (sem admin).</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Mission4Me";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return k?.GetValue(Name) is string s && s.Equals(Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enabled) k.SetValue(Name, Command);
        else k.DeleteValue(Name, false);
    }
}
