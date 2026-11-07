using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Mission4Me.Core;

public sealed class Mission
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Project { get; set; } = "";
    public DateTime StartUtc { get; set; }
    public TimeSpan Planned { get; set; }
    public TimeSpan PausedTotal { get; set; }
    public DateTime? PausedAtUtc { get; set; }
    public int PauseCount { get; set; }
    public int InterruptionCount { get; set; }
    public TimeSpan InterruptedTotal { get; set; }
}

public enum Phase { Idle, Running, Warning, Overtime, Paused }

/// <summary>Regra de negócio da missão. Não sabe nada de UI. Todo evento vai para o ActivityLog.</summary>
public sealed class MissionService
{
    public Mission? Current { get; private set; }
    public event Action? Changed;

    /// <summary>Disparado uma vez quando entra nos últimos minutos e uma vez quando o tempo acaba.</summary>
    public event Action<Phase>? Milestone;
    private Phase _lastPhase = Phase.Idle;

    public bool IsActive => Current != null;
    public bool IsPaused => Current?.PausedAtUtc != null;

    public TimeSpan Elapsed => Current == null ? TimeSpan.Zero : ElapsedAt(DateTime.UtcNow);

    private TimeSpan ElapsedAt(DateTime nowUtc)
    {
        var end = Current!.PausedAtUtc ?? nowUtc;
        var e = end - Current.StartUtc - Current.PausedTotal;
        return e < TimeSpan.Zero ? TimeSpan.Zero : e;
    }

    private TimeSpan PausedAt(DateTime nowUtc) =>
        Current!.PausedTotal + (Current.PausedAtUtc is DateTime p ? nowUtc - p : TimeSpan.Zero);

    public TimeSpan PausedSoFar => Current == null ? TimeSpan.Zero : PausedAt(DateTime.UtcNow);

    public TimeSpan Remaining => Current == null ? TimeSpan.Zero : Current.Planned - Elapsed;

    public double Progress => Current == null || Current.Planned.TotalSeconds <= 0
        ? 0 : Math.Clamp(Elapsed.TotalSeconds / Current.Planned.TotalSeconds, 0, 1);

    public Phase Phase
    {
        get
        {
            if (Current == null) return Phase.Idle;
            if (IsPaused) return Phase.Paused;
            var r = Remaining;
            if (r <= TimeSpan.Zero) return Phase.Overtime;
            var warn = TimeSpan.FromMinutes(Math.Min(10, Current.Planned.TotalMinutes * 0.15));
            return r <= warn ? Phase.Warning : Phase.Running;
        }
    }

    // ---------------------------------------------------------------- ações
    public void Start(string project, TimeSpan planned)
    {
        if (Current != null) End("substituida");
        Current = new Mission { Project = project.Trim(), StartUtc = DateTime.UtcNow, Planned = planned };
        _lastPhase = Phase.Running;
        Persist();
        ActivityLog.Event("inicio", Current, ("meta_min", Math.Round(planned.TotalMinutes, 1)));
        SafeTheme(TaskbarTheme.ApplyMission);
        Changed?.Invoke();
    }

    /// <summary>Retoma uma missão salva em disco (app reiniciado / PC religado).</summary>
    public void Resume(Mission m)
    {
        if (string.IsNullOrEmpty(m.Id)) m.Id = Guid.NewGuid().ToString("N");
        Current = m;
        _lastPhase = Phase;
        ActivityLog.Event("retomada_apos_reinicio", m, ("pausada", IsPaused));
        if (!IsPaused) SafeTheme(TaskbarTheme.ApplyMission);
        Changed?.Invoke();
    }

    public void TogglePause()
    {
        if (Current == null) return;
        if (Current.PausedAtUtc is DateTime p)
        {
            var dur = DateTime.UtcNow - p;
            Current.PausedTotal += dur;
            Current.PausedAtUtc = null;
            ActivityLog.Event("retomada", Current, ("pausa_min", Math.Round(dur.TotalMinutes, 1)));
            SafeTheme(TaskbarTheme.ApplyMission);       // voltou ao trabalho → teal
        }
        else
        {
            Current.PausedAtUtc = DateTime.UtcNow;
            Current.PauseCount++;
            ActivityLog.Event("pausa", Current, ("trabalhado_min", Math.Round(Elapsed.TotalMinutes, 1)));
            SafeTheme(TaskbarTheme.Restore);            // pausa = fora de missão → cor tradicional
        }
        Persist();
        Changed?.Invoke();
    }

    public void AddTime(TimeSpan extra)
    {
        if (Current == null) return;
        var before = Current.Planned;
        Current.Planned += extra;
        _lastPhase = Phase;
        ActivityLog.Event("meta_ampliada", Current,
            ("meta_antes_min", Math.Round(before.TotalMinutes, 1)), ("meta_min", Math.Round(Current.Planned.TotalMinutes, 1)));
        Persist();
        Changed?.Invoke();
    }

    public void Edit(string project, TimeSpan planned)
    {
        if (Current == null) return;
        var oldProject = Current.Project;
        var oldPlanned = Current.Planned;
        Current.Project = project.Trim();
        Current.Planned = planned;
        _lastPhase = Phase;   // não dispara aviso só por ter editado

        if (oldProject != Current.Project || oldPlanned != planned)
            ActivityLog.Event("edicao", Current,
                ("projeto_antes", oldProject),
                ("meta_antes_min", Math.Round(oldPlanned.TotalMinutes, 1)),
                ("meta_min", Math.Round(planned.TotalMinutes, 1)));
        Persist();
        Changed?.Invoke();
    }

    /// <summary>
    /// Registra algo que tirou você do foco enquanto o relógio corria.
    /// addToGoal = true: a meta cresce na mesma medida (o tempo "volta" para a atividade principal).
    /// addToGoal = false: só registra; a meta não muda.
    /// </summary>
    public void RegisterInterruption(string category, string reason, TimeSpan duration, bool addToGoal)
    {
        if (Current == null) return;
        var before = Current.Planned;
        Current.InterruptionCount++;
        Current.InterruptedTotal += duration;
        if (addToGoal) Current.Planned += duration;
        _lastPhase = Phase;

        ActivityLog.Event("interrupcao", Current,
            ("categoria", category),
            ("motivo", reason),
            ("duracao_min", Math.Round(duration.TotalMinutes, 1)),
            ("acrescentou_meta", addToGoal),
            ("meta_antes_min", Math.Round(before.TotalMinutes, 1)),
            ("meta_min", Math.Round(Current.Planned.TotalMinutes, 1)),
            ("trabalhado_min", Math.Round(Elapsed.TotalMinutes, 1)));
        Persist();
        Changed?.Invoke();
    }

    /// <param name="status">concluida (padrão; vira encerrada_antes se a meta não foi atingida) | interrompida | substituida</param>
    public void End(string status = "concluida")
    {
        if (Current == null) return;
        var now = DateTime.UtcNow;
        var m = Current;
        var worked = ElapsedAt(now);
        var paused = PausedAt(now);
        if (status == "concluida" && worked < m.Planned) status = "encerrada_antes";

        Current = null;
        _lastPhase = Phase.Idle;
        Storage.ClearMission();
        SafeTheme(TaskbarTheme.Restore);
        Changed?.Invoke();                      // UI some imediatamente; o log vem depois

        ActivityLog.Session(m, now, worked, paused, status);
        ActivityLog.Event("fim", m,
            ("status", status),
            ("meta_min", Math.Round(m.Planned.TotalMinutes, 1)),
            ("trabalhado_min", Math.Round(worked.TotalMinutes, 1)),
            ("pausado_min", Math.Round(paused.TotalMinutes, 1)),
            ("pausas", m.PauseCount),
            ("interrupcoes", m.InterruptionCount),
            ("interrompido_min", Math.Round(m.InterruptedTotal.TotalMinutes, 1)));
    }

    /// <summary>Chamado periodicamente pela UI.</summary>
    public void Tick()
    {
        var p = Phase;
        if (p != _lastPhase)
        {
            if (p is Phase.Warning or Phase.Overtime && _lastPhase != Phase.Paused)
            {
                if (p == Phase.Overtime)
                    ActivityLog.Event("meta_atingida", Current, ("meta_min", Math.Round(Current!.Planned.TotalMinutes, 1)));
                Milestone?.Invoke(p);
            }
            _lastPhase = p;
        }
    }

    private void Persist() { if (Current != null) Storage.SaveMission(Current); }

    private static void SafeTheme(Action a)
    {
        try { a(); } catch (Exception ex) { Storage.LogError("TaskbarTheme", ex); }
    }

    // ------------------------------------------------------------ utilitários
    /// <summary>Aceita "50", "90m", "1h", "1h30", "1:30", "2,5h".</summary>
    public static bool TryParseDuration(string? text, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().ToLowerInvariant().Replace(" ", "").Replace(',', '.');

        Match m;
        if ((m = Regex.Match(t, @"^(\d+):(\d{1,2})$")).Success)
            result = TimeSpan.FromHours(int.Parse(m.Groups[1].Value)) + TimeSpan.FromMinutes(int.Parse(m.Groups[2].Value));
        else if ((m = Regex.Match(t, @"^(\d+)h(\d{1,2})(m|min)?$")).Success)
            result = TimeSpan.FromHours(int.Parse(m.Groups[1].Value)) + TimeSpan.FromMinutes(int.Parse(m.Groups[2].Value));
        else if ((m = Regex.Match(t, @"^(\d+(\.\d+)?)h$")).Success)
            result = TimeSpan.FromHours(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        else if ((m = Regex.Match(t, @"^(\d+)(m|min)?$")).Success)
            result = TimeSpan.FromMinutes(int.Parse(m.Groups[1].Value));
        else return false;

        return result >= TimeSpan.FromMinutes(1) && result <= TimeSpan.FromHours(16);
    }

    public static string FormatClock(TimeSpan t)
    {
        bool neg = t < TimeSpan.Zero;
        var a = neg ? t.Negate() : t;
        a = neg ? TimeSpan.FromSeconds(Math.Floor(a.TotalSeconds)) : TimeSpan.FromSeconds(Math.Ceiling(a.TotalSeconds));
        string s = a.TotalHours >= 1 ? $"{(int)a.TotalHours}:{a.Minutes:00}:{a.Seconds:00}" : $"{a.Minutes:00}:{a.Seconds:00}";
        return neg ? "+" + s : s;
    }

    public static string FormatShort(TimeSpan t)
    {
        int h = (int)t.TotalHours, m = t.Minutes;
        if (h == 0) return $"{m} min";
        return m == 0 ? $"{h}h" : $"{h}h{m:00}";
    }
}
