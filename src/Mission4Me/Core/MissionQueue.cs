using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Mission4Me.Core;

/// <summary>Missão aguardando execução (sem data/horário — só a ordem da fila).</summary>
public sealed class QueuedMission
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Project { get; set; } = "";
    public int Minutes { get; set; } = 50;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Fila de próximas missões, salva em %APPDATA%\Mission4Me\fila.json (cifrada, gravação atômica).
/// Cada movimento vai para eventos.jsonl: fila_adicionada, fila_removida, fila_iniciada (com tempo de espera).
/// </summary>
public static class MissionQueue
{
    private static string QueuePath => Path.Combine(Storage.Dir, "fila.json");
    private static List<QueuedMission>? _items;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>A fila mudou (adicionar, remover, reordenar).</summary>
    public static event Action? Changed;

    public static IReadOnlyList<QueuedMission> Items { get { Load(); return _items!; } }

    private static void Load()
    {
        if (_items != null) return;
        try
        {
            _items = SecureStore.ReadAllText(QueuePath) is string t
                ? JsonSerializer.Deserialize<List<QueuedMission>>(t) ?? new()
                : new();
            _items.RemoveAll(q => q == null || string.IsNullOrWhiteSpace(q.Project));
            foreach (var q in _items)
            {
                if (q.Project.Length > 60) q.Project = q.Project[..60];
                q.Minutes = Math.Clamp(q.Minutes, 1, 16 * 60);
            }
            if (_items.Count > 500) _items = _items.Take(500).ToList();
        }
        catch (Exception ex) { Storage.LogError("MissionQueue.Load", ex); _items = new(); }
    }

    private static void Save() => Storage.WriteAtomic(QueuePath, JsonSerializer.Serialize(_items, Json));

    public static QueuedMission Add(string project, int minutes, bool atTop = false)
    {
        Load();
        var q = new QueuedMission { Project = project.Trim(), Minutes = Math.Clamp(minutes, 1, 16 * 60) };
        if (atTop) _items!.Insert(0, q); else _items!.Add(q);
        Save();
        ActivityLog.Event("fila_adicionada", null,
            ("fila_id", q.Id), ("projeto", q.Project), ("meta_min", q.Minutes), ("posicao", _items.IndexOf(q) + 1));
        Changed?.Invoke();
        return q;
    }

    /// <param name="started">true = saiu da fila para virar a missão atual; false = removida</param>
    public static void Remove(string id, bool started)
    {
        Load();
        var q = _items!.FirstOrDefault(x => x.Id == id);
        if (q == null) return;
        _items.Remove(q);
        Save();
        ActivityLog.Event(started ? "fila_iniciada" : "fila_removida", null,
            ("fila_id", q.Id), ("projeto", q.Project), ("meta_min", q.Minutes),
            ("espera_min", Math.Round((DateTime.UtcNow - q.CreatedUtc).TotalMinutes, 1)));
        Changed?.Invoke();
    }

    public static void Update(string id, string project, int minutes)
    {
        Load();
        var q = _items!.FirstOrDefault(x => x.Id == id);
        if (q == null) return;
        var oldProject = q.Project; var oldMinutes = q.Minutes;
        q.Project = project.Trim();
        q.Minutes = Math.Clamp(minutes, 1, 16 * 60);
        if (q.Project == oldProject && q.Minutes == oldMinutes) return;
        Save();
        ActivityLog.Event("fila_editada", null,
            ("fila_id", q.Id), ("projeto_antes", oldProject), ("projeto", q.Project),
            ("meta_antes_min", oldMinutes), ("meta_min", q.Minutes));
        Changed?.Invoke();
    }

    public static void MoveUp(string id)
    {
        Load();
        int i = _items!.FindIndex(x => x.Id == id);
        if (i <= 0) return;
        (_items[i - 1], _items[i]) = (_items[i], _items[i - 1]);
        Save();
        Changed?.Invoke();
    }
}
