using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Mission4Me.Core;

public enum OverlayPosition { NearTray, Left }

/// <summary>Motivo pré-definido de interrupção. Id é estável (bom para relatórios); Label e Minutes podem ser editados.</summary>
public sealed class InterruptionPreset
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public int Minutes { get; set; } = 30;

    public static List<InterruptionPreset> Defaults() => new()
    {
        new() { Id = "ligacao",       Label = "Ligação inesperada",                 Minutes = 30 },
        new() { Id = "fome",          Label = "Pausa para comer",                   Minutes = 30 },
        new() { Id = "celular",       Label = "Desviei o foco no celular",          Minutes = 15 },
        new() { Id = "outro_projeto", Label = "Desviei o foco com outro projeto",   Minutes = 15 },
        new() { Id = "pessoa",        Label = "Alguém me interrompeu",              Minutes = 30 },
        new() { Id = "quem_importa",  Label = "Fui dar atenção para quem importa",  Minutes = 60 },
    };
}

public sealed class Settings
{
    public OverlayPosition Position { get; set; } = OverlayPosition.NearTray;
    public int LastMinutes { get; set; } = 50;
    public string Hotkey { get; set; } = "Ctrl+Alt+M";
    public bool ColorPulse { get; set; } = true;
    public double PulseHz { get; set; } = 0.25;  // 15 ciclos por minuto (ciclo de 4 s)
    public List<InterruptionPreset> Interruptions { get; set; } = InterruptionPreset.Defaults();
    public bool HistoryOpen { get; set; }
    /// <summary>Painel lateral aberto da última vez: "", "fila" ou "historico".</summary>
    public string? SidePanel { get; set; }
    public int PulseVersion { get; set; }        // 0 = configuração antiga (migra para 0,25 Hz)
}

/// <summary>
/// Arquivos em %APPDATA%\Mission4Me: configurações, missão em andamento e log de erros.
/// Histórico e eventos ficam em <see cref="ActivityLog"/>.
/// </summary>
public static class Storage
{
    public static string Dir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mission4Me");

    private static string SettingsPath => Path.Combine(Dir, "config.json");
    private static string StatePath    => Path.Combine(Dir, "missao-atual.json");
    public  static string ErrorPath    => Path.Combine(Dir, "erros.log");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings LoadSettings()
    {
        try { if (SecureStore.ReadAllText(SettingsPath) is string t) return Sanitize(JsonSerializer.Deserialize<Settings>(t) ?? new()); }
        catch (Exception ex) { LogError("LoadSettings", ex); }
        return new Settings();
    }

    public static void SaveSettings(Settings s) => Write(SettingsPath, JsonSerializer.Serialize(s, Json));

    public static Mission? LoadMission()
    {
        try { if (SecureStore.ReadAllText(StatePath) is string t) return JsonSerializer.Deserialize<Mission>(t); }
        catch (Exception ex) { LogError("LoadMission", ex); }
        return null;
    }

    public static void SaveMission(Mission m) => Write(StatePath, JsonSerializer.Serialize(m, Json));
    public static void ClearMission() { try { File.Delete(StatePath); } catch { } }


    public static void LogError(string where, Exception ex) =>
        Append($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}: {ex}{Environment.NewLine}");

    public static void LogInfo(string message) =>
        Append($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] info: {message}{Environment.NewLine}");

    private const long MaxLogBytes = 1024 * 1024;

    private static void Append(string line)
    {
        try { Directory.CreateDirectory(Dir); AppendCapped(ErrorPath, Redact(line)); } catch { }
    }

    private static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Troca a pasta do usuário (que contém o nome de login) por %USERPROFILE% em textos de log/erro.</summary>
    public static string Redact(string s) =>
        string.IsNullOrEmpty(Profile) ? s : s.Replace(Profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);

    /// <summary>Limita o que vem do disco (arquivo adulterado ou corrompido não derruba nem incha a UI).</summary>
    private static Settings Sanitize(Settings s)
    {
        s.Interruptions = (s.Interruptions ?? InterruptionPreset.Defaults())
            .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Label))
            .Take(20)
            .Select(p =>
            {
                p.Label = p.Label.Trim();
                if (p.Label.Length > 60) p.Label = p.Label[..60];
                p.Minutes = Math.Clamp(p.Minutes, 1, 960);
                p.Id = string.IsNullOrWhiteSpace(p.Id) ? "personalizado" : (p.Id.Length > 40 ? p.Id[..40] : p.Id);
                return p;
            })
            .ToList();
        s.LastMinutes = Math.Clamp(s.LastMinutes, 1, 960);
        return s;
    }

    /// <summary>Acrescenta ao log; passando de 1 MB, a versão atual vira *.old.log (só uma geração).</summary>
    private static void AppendCapped(string path, string line)
    {
        var fi = new FileInfo(path);
        if (fi.Exists && fi.Length > MaxLogBytes)
            File.Move(path, Path.ChangeExtension(path, ".old.log"), true);
        File.AppendAllText(path, line);
    }

    /// <summary>Gravação atômica e cifrada para outros módulos.</summary>
    public static void WriteAtomic(string path, string content) => Write(path, content);

    private static void Write(string path, string content)
    {
        try
        {
            SecureStore.WriteAllText(path, content);   // cifrado (DPAPI) e atômico
        }
        catch (Exception ex) { LogError("Write " + path, ex); }
    }
}
