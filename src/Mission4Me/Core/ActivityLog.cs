using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Mission4Me.Core;

/// <summary>
/// Registro de atividade, pensado para relatórios de produtividade.
///
/// Arquivos cifrados em repouso (DPAPI, ver SecureStore); "Exportar" gera cópia legível.
///
/// sessoes.csv  – uma linha por missão encerrada (resumo). Separador ';', decimal ',' (Excel pt-BR),
///                cabeçalho estável em snake_case. Colunas:
///   id              GUID da missão (liga com eventos.jsonl)
///   data            data de início (yyyy-MM-dd)
///   inicio, fim     yyyy-MM-dd HH:mm:ss (horário local)
///   projeto         nome final do projeto
///   meta_min        meta final em minutos
///   trabalhado_min  tempo efetivo (sem pausas)
///   pausado_min     tempo total em pausa
///   extra_min       quanto passou da meta
///   pausas          quantidade de pausas
///   cumprimento_pct trabalhado / meta × 100
///   status          concluida | encerrada_antes | interrompida | substituida
///   interrupcoes    quantidade de interrupções registradas
///   interrompido_min soma do tempo das interrupções
///   foco_min        trabalhado − interrompido (tempo de foco real)
///
/// eventos.jsonl – um JSON por linha, append-only, com fuso horário. Fonte detalhada.
///   eventos: inicio, pausa, retomada, edicao, meta_ampliada, interrupcao, meta_atingida, retomada_apos_reinicio, fim
/// </summary>
/// <summary>Uma missão do histórico (encerrada, ou a em andamento montada pela UI).</summary>
public sealed record SessionRecord(
    string Id, DateTime Start, DateTime? End, string Project,
    double MetaMin, double WorkedMin, double PausedMin, int Pauses,
    double PctDone, string Status, int Interruptions, double InterruptedMin, double FocusMin);

public static class ActivityLog
{
    private const string CsvHeaderV2 =
        "id;data;inicio;fim;projeto;meta_min;trabalhado_min;pausado_min;extra_min;pausas;cumprimento_pct;status";

    /// <summary>v3: colunas novas sempre acrescentadas no fim, para não quebrar leitores antigos.</summary>
    public const string CsvHeader = CsvHeaderV2 + ";interrupcoes;interrompido_min;foco_min";

    public static string SessionsPath => Path.Combine(Storage.Dir, "sessoes.csv");
    public static string EventsPath   => Path.Combine(Storage.Dir, "eventos.jsonl");

    private static readonly CultureInfo Pt = new("pt-BR");
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly object Gate = new();
    private static bool _csvReady;                                      // cabeçalho já conferido nesta execução
    private static (DateTime Stamp, long Size, List<SessionRecord> Rows)? _cache;
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // ------------------------------------------------------------- eventos
    public static void Event(string type, Mission? m, params (string Key, object? Value)[] fields)
    {
        try
        {
            var d = new Dictionary<string, object?>
            {
                ["ts"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", Inv),
                ["evento"] = type,
            };
            if (m != null) { d["missao"] = m.Id; d["projeto"] = m.Project; }
            foreach (var (k, v) in fields) d[k] = v;

            lock (Gate)
            {
                SecureStore.AppendLine(EventsPath, JsonSerializer.Serialize(d, Json));
            }
        }
        catch (Exception ex) { Storage.LogError("ActivityLog.Event", ex); }
    }

    // ------------------------------------------------------------- sessões
    public static void Session(Mission m, DateTime endUtc, TimeSpan worked, TimeSpan paused, string status)
    {
        try
        {
            var extra = worked > m.Planned ? worked - m.Planned : TimeSpan.Zero;
            double pct = m.Planned.TotalSeconds > 0 ? worked.TotalSeconds / m.Planned.TotalSeconds * 100 : 0;
            var start = m.StartUtc.ToLocalTime();
            var end = endUtc.ToLocalTime();

            var line = string.Join(";",
                m.Id,
                start.ToString("yyyy-MM-dd", Inv),
                start.ToString("yyyy-MM-dd HH:mm:ss", Inv),
                end.ToString("yyyy-MM-dd HH:mm:ss", Inv),
                Csv(m.Project),
                Min(m.Planned),
                Min(worked),
                Min(paused),
                Min(extra),
                m.PauseCount.ToString(Inv),
                Math.Round(pct).ToString("0", Inv),
                status,
                m.InterruptionCount.ToString(Inv),
                Min(m.InterruptedTotal),
                Min(worked > m.InterruptedTotal ? worked - m.InterruptedTotal : TimeSpan.Zero));

            lock (Gate)
            {
                EnsureCsv();
                SecureStore.AppendLine(SessionsPath, line);
            }
        }
        catch (Exception ex) { Storage.LogError("ActivityLog.Session", ex); }
    }

    // ------------------------------------------------------------ leitura
    public static List<string> RecentProjects(int max = 5)
    {
        var result = new List<string>();
        foreach (var row in Rows().Reverse())
        {
            if (row.Length < 5 || string.IsNullOrWhiteSpace(row[4])) continue;
            var name = FromCsvText(row[4]);
            if (result.Any(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(name);
            if (result.Count >= max) break;
        }
        return result;
    }

    public static double MinutesToday()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd", Inv);
        return Rows().Where(r => r.Length > 6 && r[1] == today)
                     .Sum(r => double.TryParse(r[6], NumberStyles.Float, Pt, out var v) ? v : 0);
    }

    private static IEnumerable<string[]> Rows()
    {
        try
        {
            lock (Gate)
            {
                EnsureCsv();
                return SecureStore.ReadLines(SessionsPath).Skip(1).Where(l => l.Length > 0).Select(SplitCsv).ToList();
            }
        }
        catch (Exception ex) { Storage.LogError("ActivityLog.Rows", ex); return Array.Empty<string[]>(); }
    }

    /// <summary>Todas as missões encerradas, lidas pelo nome das colunas.</summary>
    public static List<SessionRecord> ReadSessions()
    {
        var result = new List<SessionRecord>();
        try
        {
            string[] lines;
            lock (Gate)
            {
                EnsureCsv();
                var fi = new FileInfo(SessionsPath);
                if (_cache is { } c && c.Stamp == fi.LastWriteTimeUtc && c.Size == fi.Length)
                    return new List<SessionRecord>(c.Rows);           // arquivo não mudou: não relê
                lines = SecureStore.ReadLines(SessionsPath).ToArray();
                _cache = (fi.LastWriteTimeUtc, fi.Length, result);
            }
            if (lines.Length < 2) return new List<SessionRecord>(result);

            var head = SplitCsv(lines[0]);
            var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < head.Length; i++) idx[head[i].Trim()] = i;

            foreach (var line in lines.Skip(1))
            {
                if (line.Length == 0) continue;
                var f = SplitCsv(line);
                string G(string n) => idx.TryGetValue(n, out var i) && i < f.Length ? f[i] : "";
                double D(string n) => double.TryParse(G(n), NumberStyles.Float, Pt, out var v) ? v : 0;
                int I(string n) => int.TryParse(G(n), NumberStyles.Integer, Inv, out var v) ? v : 0;

                if (!DateTime.TryParseExact(G("inicio"), "yyyy-MM-dd HH:mm:ss", Inv, DateTimeStyles.None, out var start)) continue;
                DateTime? end = DateTime.TryParseExact(G("fim"), "yyyy-MM-dd HH:mm:ss", Inv, DateTimeStyles.None, out var e) ? e : null;
                double worked = D("trabalhado_min");
                double pct = double.TryParse(G("cumprimento_pct"), NumberStyles.Float, Inv, out var p) ? p : 0;
                double focus = idx.ContainsKey("foco_min") && G("foco_min").Length > 0 ? D("foco_min") : worked;

                result.Add(new SessionRecord(G("id"), start, end, FromCsvText(G("projeto")), D("meta_min"), worked, D("pausado_min"),
                    I("pausas"), pct, G("status"), I("interrupcoes"), D("interrompido_min"), focus));
            }
        }
        catch (Exception ex) { Storage.LogError("ActivityLog.ReadSessions", ex); _cache = null; }
        return new List<SessionRecord>(result);   // cópia: quem chama pode ordenar/filtrar à vontade
    }

    // ------------------------------------------------- exportação (cópia legível)
    /// <summary>Linhas do histórico em texto puro, para o usuário exportar explicitamente.</summary>
    public static List<string> ExportSessionLines()
    {
        lock (Gate) { EnsureCsv(); return SecureStore.ReadLines(SessionsPath); }
    }

    public static List<string> ExportEventLines()
    {
        lock (Gate) { return SecureStore.ReadLines(EventsPath); }
    }

    // ------------------------------------------------- edição / exclusão
    public static bool UpdateSession(SessionRecord before, SessionRecord after)
    {
        if (!Rewrite(after.Id, after)) return false;
        Event("historico_editado", null,
            ("missao", after.Id),
            ("projeto_antes", before.Project), ("projeto", after.Project),
            ("inicio_antes", Stamp(before.Start)), ("inicio", Stamp(after.Start)),
            ("fim_antes", before.End is DateTime b ? Stamp(b) : null), ("fim", after.End is DateTime a ? Stamp(a) : null),
            ("meta_antes_min", Math.Round(before.MetaMin, 1)), ("meta_min", Math.Round(after.MetaMin, 1)),
            ("trabalhado_antes_min", Math.Round(before.WorkedMin, 1)), ("trabalhado_min", Math.Round(after.WorkedMin, 1)),
            ("status_antes", before.Status), ("status", after.Status),
            ("interrupcoes_antes", before.Interruptions), ("interrupcoes", after.Interruptions),
            ("interrompido_antes_min", Math.Round(before.InterruptedMin, 1)), ("interrompido_min", Math.Round(after.InterruptedMin, 1)));
        return true;
    }

    public static bool DeleteSession(SessionRecord r)
    {
        if (!Rewrite(r.Id, null)) return false;
        Event("historico_excluido", null,
            ("missao", r.Id), ("projeto", r.Project), ("inicio", Stamp(r.Start)),
            ("fim", r.End is DateTime e ? Stamp(e) : null), ("meta_min", Math.Round(r.MetaMin, 1)),
            ("trabalhado_min", Math.Round(r.WorkedMin, 1)), ("status", r.Status));
        return true;
    }

    private static string Stamp(DateTime d) => d.ToString("yyyy-MM-dd HH:mm:ss", Inv);

    /// <summary>Reescreve o CSV trocando (ou removendo, se replacement = null) a linha com esse id.</summary>
    private static bool Rewrite(string id, SessionRecord? replacement)
    {
        try
        {
            lock (Gate)
            {
                EnsureCsv();
                var lines = SecureStore.ReadLines(SessionsPath).ToArray();
                if (lines.Length == 0) return false;
                var head = SplitCsv(lines[0]);
                int idCol = Array.FindIndex(head, h => h.Trim() == "id");
                if (idCol < 0) return false;

                var sb = new StringBuilder().Append(lines[0].TrimStart('\uFEFF')).Append("\r\n");
                bool found = false;
                foreach (var line in lines.Skip(1))
                {
                    if (line.Length == 0) continue;
                    var f = SplitCsv(line);
                    if (!found && idCol < f.Length && f[idCol] == id)
                    {
                        found = true;
                        if (replacement != null) sb.Append(FormatRow(replacement)).Append("\r\n");
                        continue;
                    }
                    sb.Append(line).Append("\r\n");
                }
                if (!found) return false;

                File.Copy(SessionsPath, Path.Combine(Storage.Dir, "sessoes.bak.csv"), true);   // backup (já cifrado)
                SecureStore.WriteLines(SessionsPath, sb.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
            }
            return true;
        }
        catch (Exception ex) { Storage.LogError("ActivityLog.Rewrite", ex); return false; }
    }

    /// <summary>Linha no formato v3 (mesma ordem de CsvHeader).</summary>
    private static string FormatRow(SessionRecord r)
    {
        string M(double minutes) => Math.Round(minutes, 1).ToString("0.0", Pt);
        return string.Join(";",
            r.Id,
            r.Start.ToString("yyyy-MM-dd", Inv),
            Stamp(r.Start),
            r.End is DateTime e ? Stamp(e) : "",
            Csv(r.Project),
            M(r.MetaMin),
            M(r.WorkedMin),
            M(r.PausedMin),
            M(Math.Max(0, r.WorkedMin - r.MetaMin)),
            r.Pauses.ToString(Inv),
            Math.Round(r.PctDone).ToString("0", Inv),
            r.Status,
            r.Interruptions.ToString(Inv),
            M(r.InterruptedMin),
            M(r.FocusMin));
    }

    // ----------------------------------------- criação / migração do CSV
    private static void EnsureCsv()
    {
        if (_csvReady && File.Exists(SessionsPath)) return;             // evita reler o arquivo inteiro a cada chamada
        _csvReady = true;
        Directory.CreateDirectory(Storage.Dir);
        if (!File.Exists(SessionsPath))
        {
            SecureStore.WriteLines(SessionsPath, new[] { CsvHeader });
            return;
        }

        var lines = SecureStore.ReadLines(SessionsPath).ToArray();
        var header = lines.Length > 0 ? lines[0].TrimStart('﻿') : "";
        if (header == CsvHeader)
        {
            if (!SecureStore.IsEncryptedLineFile(SessionsPath)) SecureStore.WriteLines(SessionsPath, lines);   // legado → cifrado
            return;
        }

        if (header == CsvHeaderV2)
        {
            // v2 → v3: sem interrupções registradas; foco = trabalhado
            var v3 = new StringBuilder().Append(CsvHeader).Append("\r\n");
            foreach (var l in lines.Skip(1).Where(l => l.Length > 0))
            {
                var f = SplitCsv(l);
                var worked = f.Length > 6 ? f[6] : "";
                v3.Append(l).Append(";0;0,0;").Append(worked).Append("\r\n");
            }
            var bk2 = Path.Combine(Storage.Dir, "sessoes-v2-backup.csv");
            if (!File.Exists(bk2)) SecureStore.WriteLines(bk2, lines);
            SecureStore.WriteLines(SessionsPath, v3.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
            return;
        }

        // formato antigo (v1): Inicio;Projeto;Planejado (min);Trabalhado (min);Status
        var sb = new StringBuilder().Append(CsvHeader).Append("\r\n");
        foreach (var l in lines.Skip(1).Where(l => l.Length > 0))
        {
            var f = SplitCsv(l);
            if (f.Length < 5) continue;
            if (!DateTime.TryParseExact(f[0], "yyyy-MM-dd HH:mm", Inv, DateTimeStyles.None, out var start)) continue;
            double.TryParse(f[2], NumberStyles.Float, Inv, out var meta);
            double.TryParse(f[3], NumberStyles.Float, Pt, out var worked);
            double extra = Math.Max(0, worked - meta);
            double pct = meta > 0 ? worked / meta * 100 : 0;
            string status = f[4].Trim().Replace(' ', '_');

            sb.Append(string.Join(";",
                Guid.NewGuid().ToString("N"),
                start.ToString("yyyy-MM-dd", Inv),
                start.ToString("yyyy-MM-dd HH:mm:ss", Inv),
                "",                                  // fim desconhecido no formato antigo
                Csv(f[1]),
                meta.ToString("0.0", Pt),
                worked.ToString("0.0", Pt),
                "",                                  // pausado desconhecido
                extra.ToString("0.0", Pt),
                "",                                  // pausas desconhecidas
                Math.Round(pct).ToString("0", Inv),
                status,
                "0", "0,0", worked.ToString("0.0", Pt))).Append("\r\n");
        }

        var backup = Path.Combine(Storage.Dir, "sessoes-v1-backup.csv");
        if (!File.Exists(backup)) SecureStore.WriteLines(backup, lines);
        SecureStore.WriteLines(SessionsPath, sb.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
    }

    // ------------------------------------------------------------ helpers
    private static string Min(TimeSpan t) => Math.Round(t.TotalMinutes, 1).ToString("0.0", Pt);

    private const string FormulaStarts = "=+-@\t\r";

    /// <summary>
    /// Campo de texto para CSV. Texto que começa com = + - @ ganha um apóstrofo na frente:
    /// sem isso, o Excel executaria como fórmula ("CSV injection"). A leitura remove o apóstrofo.
    /// </summary>
    private static string Csv(string s)
    {
        if (s.Length > 0 && FormulaStarts.IndexOf(s[0]) >= 0) s = "'" + s;
        return s.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private static string FromCsvText(string s) =>
        s.Length > 1 && s[0] == '\'' && FormulaStarts.IndexOf(s[1]) >= 0 ? s[1..] : s;

    private static string[] SplitCsv(string line)
    {
        var fields = new List<string>(); var sb = new StringBuilder(); bool q = false;
        line = line.TrimStart('﻿');
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (q)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') q = false;
                else sb.Append(c);
            }
            else if (c == '"') q = true;
            else if (c == ';') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields.ToArray();
    }
}
