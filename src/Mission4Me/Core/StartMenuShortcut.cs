using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Mission4Me.Core;

/// <summary>
/// Atalho "Mission4Me" no Menu Iniciar com tecla de atalho Ctrl+Alt+M.
/// É o que permite abrir o app com a combinação mesmo quando ele está fechado
/// (o próprio Windows lê a tecla de atalho de atalhos do Menu Iniciar/Área de Trabalho).
/// </summary>
public static class StartMenuShortcut
{
    public const string LaunchArg = "--missao";

    private static string LinkPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Mission4Me.lnk");

    /// <summary>
    /// Cria/atualiza o atalho com a tecla indicada (null = sem tecla). Devolve o nome do atalho
    /// conflitante, se outro .lnk já usar a mesma tecla (nesse caso cria sem tecla, para não brigar).
    /// </summary>
    public static string? Ensure(string? hotkeyText)
    {
        string? conflict = null;
        try
        {
            var shell = CreateShell();
            if (shell == null) return null;

            if (hotkeyText != null) conflict = FindConflict(shell, hotkeyText);

            dynamic lnk = shell.CreateShortcut(LinkPath);
            lnk.TargetPath = Environment.ProcessPath;
            lnk.Arguments = LaunchArg;
            lnk.WorkingDirectory = Path.GetDirectoryName(Environment.ProcessPath);
            lnk.Description = "Mission4Me — iniciar missão de foco";
            lnk.Hotkey = hotkeyText != null && conflict == null ? hotkeyText : "";
            lnk.Save();
        }
        catch (Exception ex) { Storage.LogError("StartMenuShortcut", ex); }
        return conflict;
    }

    private static dynamic? CreateShell()
    {
        var t = Type.GetTypeFromProgID("WScript.Shell");
        return t == null ? null : Activator.CreateInstance(t);
    }

    /// <summary>Procura outros .lnk (Menu Iniciar e Área de Trabalho, usuário e comum) com Ctrl+Alt+M.</summary>
    private static string? FindConflict(dynamic shell, string hotkeyText)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };
        var wanted = Normalize(hotkeyText);

        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)).Distinct())
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }); }
            catch { continue; }

            foreach (var f in files)
            {
                if (string.Equals(f, LinkPath, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    dynamic l = shell.CreateShortcut(f);
                    string hk = l.Hotkey ?? "";
                    if (hk.Length > 0 && Normalize(hk) == wanted) return Path.GetFileNameWithoutExtension(f);
                }
                catch { /* atalho corrompido: ignora */ }
            }
        }
        return null;
    }

    private static string Normalize(string hk) =>
        string.Join("+", hk.ToUpperInvariant().Replace("CONTROL", "CTRL").Split('+', StringSplitOptions.TrimEntries).OrderBy(x => x));
}
