using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Mission4Me.Core;

/// <summary>
/// Pinta a barra de tarefas com a cor de ênfase do Windows e restaura a original.
/// Tudo em HKCU (sem admin). Antes de pintar, salva os valores originais em disco —
/// se o app travar, a próxima inicialização restaura sozinha.
/// </summary>
public static class TaskbarTheme
{
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string Dwm         = @"Software\Microsoft\Windows\DWM";
    private const string Accent      = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
    private const string Desktop     = @"Control Panel\Desktop";

    private static readonly (string Key, string Name)[] Tracked =
    {
        (Personalize, "ColorPrevalence"),      // "mostrar ênfase em Iniciar e barra de tarefas"
        (Personalize, "SystemUsesLightTheme"), // barra com ênfase exige modo escuro do sistema
        (Dwm,         "AccentColor"),
        (Dwm,         "ColorizationColor"),
        (Dwm,         "ColorizationAfterglow"),
        (Accent,      "AccentPalette"),
        (Accent,      "StartColorMenu"),
        (Accent,      "AccentColorMenu"),
        (Desktop,     "AutoColorization"),     // senão o Windows troca a cor pelo papel de parede
    };

    private sealed record Saved(string Key, string Name, bool Exists, string? Kind, long? Number, string? Text, string? Bytes);

    private static string BackupPath => Path.Combine(Storage.Dir, "barra-original.json");

    public static bool IsApplied => File.Exists(BackupPath);

    // ------------------------------------------------------------------ aplicar
    private const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string RunOnceName = "Mission4MeRestaurarBarra";
    public const string RestoreArg = "--restaurar";

    public static void ApplyMission()
    {
        if (!IsApplied) Backup();   // nunca sobrescreve o backup original com o teal
        SetRestoreAtLogon(true);

        SetDword(Personalize, "SystemUsesLightTheme", 0);
        SetDword(Personalize, "ColorPrevalence", 1);
        SetDword(Desktop, "AutoColorization", 0);

        SetDword(Dwm, "AccentColor", Abgr(Palette.Accent));
        SetDword(Dwm, "ColorizationColor", Argb(Palette.Accent, 0xC4));
        SetDword(Dwm, "ColorizationAfterglow", Argb(Palette.Accent, 0xC4));

        SetBinary(Accent, "AccentPalette", BuildPalette());
        SetDword(Accent, "StartColorMenu", Abgr(Palette.TaskbarBody));
        SetDword(Accent, "AccentColorMenu", Abgr(Palette.Accent));

        Broadcast();
    }

    // ---------------------------------------------------------------- restaurar
    public static void Restore()
    {
        if (!IsApplied) return;
        List<Saved>? saved = null;
        try { saved = JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(BackupPath)); }
        catch { /* backup corrompido: melhor apagar do que travar em loop */ }

        if (saved != null)
        {
            foreach (var s in saved)
            {
                try
                {
                    using var k = Registry.CurrentUser.CreateSubKey(s.Key, true);
                    if (!s.Exists) { k.DeleteValue(s.Name, false); continue; }
                    var kind = Enum.Parse<RegistryValueKind>(s.Kind!);
                    object val = kind switch
                    {
                        RegistryValueKind.DWord  => unchecked((int)(uint)s.Number!.Value),
                        RegistryValueKind.QWord  => s.Number!.Value,
                        RegistryValueKind.Binary => Convert.FromBase64String(s.Bytes!),
                        _                        => s.Text ?? "",
                    };
                    k.SetValue(s.Name, val, kind);
                }
                catch (Exception ex) { Storage.LogError($"Restore {s.Key}\\{s.Name}", ex); }
            }
        }

        try { File.Delete(BackupPath); } catch { }
        SetRestoreAtLogon(false);
        Broadcast();
    }

    private static void SetRestoreAtLogon(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunOnceKey, true);
            if (on) k.SetValue(RunOnceName, $"\"{Environment.ProcessPath}\" {RestoreArg}");
            else k.DeleteValue(RunOnceName, false);
        }
        catch (Exception ex) { Storage.LogError("RunOnce", ex); }
    }

    /// <summary>Chamado na inicialização: se ficou backup pendente e não há missão, desfaz.</summary>
    public static void RecoverIfNeeded(bool missionActive)
    {
        if (IsApplied && !missionActive) Restore();
    }

    // ------------------------------------------------------------------ backup
    private static void Backup()
    {
        var list = new List<Saved>();
        foreach (var (key, name) in Tracked)
        {
            using var k = Registry.CurrentUser.OpenSubKey(key, false);
            object? v = k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (k == null || v == null) { list.Add(new Saved(key, name, false, null, null, null, null)); continue; }

            var kind = k.GetValueKind(name);
            list.Add(kind switch
            {
                RegistryValueKind.DWord  => new Saved(key, name, true, kind.ToString(), (long)(uint)(int)v, null, null),
                RegistryValueKind.QWord  => new Saved(key, name, true, kind.ToString(), (long)v, null, null),
                RegistryValueKind.Binary => new Saved(key, name, true, kind.ToString(), null, null, Convert.ToBase64String((byte[])v)),
                _                        => new Saved(key, name, true, kind.ToString(), null, v.ToString(), null),
            });
        }
        Directory.CreateDirectory(Storage.Dir);
        File.WriteAllText(BackupPath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ----------------------------------------------------------------- helpers
    private static byte[] BuildPalette()
    {
        var bytes = new byte[32];
        for (int i = 0; i < 8; i++)
        {
            uint c = Palette.AccentShades[i];
            bytes[i * 4 + 0] = (byte)(c >> 16); // R
            bytes[i * 4 + 1] = (byte)(c >> 8);  // G
            bytes[i * 4 + 2] = (byte)c;         // B
            bytes[i * 4 + 3] = 0xFF;            // A
        }
        return bytes;
    }

    /// <summary>0xRRGGBB → DWORD 0xAABBGGRR (formato do registro de ênfase).</summary>
    private static uint Abgr(uint rgb) =>
        0xFF000000u | ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);

    private static uint Argb(uint rgb, byte alpha) => ((uint)alpha << 24) | (rgb & 0xFFFFFF);

    private static void SetDword(string key, string name, uint value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(key, true);
        k.SetValue(name, unchecked((int)value), RegistryValueKind.DWord);
    }

    private static void SetBinary(string key, string name, byte[] value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(key, true);
        k.SetValue(name, value, RegistryValueKind.Binary);
    }

    /// <summary>Avisa o Explorer/DWM que o tema mudou.</summary>
    /// <remarks>
    /// Roda em segundo plano: o broadcast espera cada janela aberta responder e,
    /// na thread da interface, isso travava o app por alguns segundos.
    /// </remarks>
    public static void Broadcast()
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            foreach (var area in new[] { "ImmersiveColorSet", "WindowsThemeElement" })
                Native.SendMessageTimeout(Native.HWND_BROADCAST, Native.WM_SETTINGCHANGE, UIntPtr.Zero, area,
                                          Native.SMTO_ABORTIFHUNG, 1000, out _);
        });
    }

    /// <summary>
    /// Plano B: em alguns builds do Win11 a barra só repinta após reiniciar o Explorer.
    /// O próprio Windows relança o explorer.exe automaticamente.
    /// </summary>
    public static void RestartExplorer()
    {
        int mySession = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("explorer"))
        {
            try { if (p.SessionId == mySession) p.Kill(); } catch { }
            finally { p.Dispose(); }
        }
        System.Threading.Tasks.Task.Delay(2500).ContinueWith(_ =>
        {
            if (System.Diagnostics.Process.GetProcessesByName("explorer").Length == 0)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
                    { UseShellExecute = false });   // caminho absoluto: nada de procurar "explorer.exe" em outras pastas
        });
    }
}
