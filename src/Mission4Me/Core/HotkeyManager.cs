using System;
using System.Threading.Tasks;

namespace Mission4Me.Core;

/// <summary>
/// Dono do atalho de teclado: registra, troca, salva na configuração e mantém
/// o atalho do Menu Iniciar com a mesma tecla (para abrir com o app fechado).
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly GlobalHotkey _hk = new();
    private readonly Settings _settings;
    private readonly Action _onPress;

    public HotkeyDef Current { get; private set; }
    public bool IsRegistered { get; private set; }

    /// <summary>Status mudou (registrado/falhou/trocado). Sempre na thread da interface.</summary>
    public event Action? StatusChanged;
    /// <summary>Aviso para o usuário (título, mensagem). Pode vir de outra thread.</summary>
    public event Action<string, string>? Warning;

    public HotkeyManager(Settings settings, Action onPress)
    {
        _settings = settings;
        _onPress = onPress;
        Current = HotkeyDef.Parse(settings.Hotkey) ?? HotkeyDef.Default;
    }

    public void Apply()
    {
        IsRegistered = _hk.Register(Current, _onPress);
        if (!IsRegistered)
            Warning?.Invoke($"{Current.Label} indisponível",
                "Outro programa já usa essa combinação. Troque o atalho na janela do Mission4Me.");
        UpdateStartMenuShortcut();
        StatusChanged?.Invoke();
    }

    /// <summary>Solta o atalho enquanto o usuário grava um novo (senão a própria tecla é interceptada).</summary>
    public void Suspend() => _hk.Unregister();

    public void Resume()
    {
        IsRegistered = _hk.Register(Current, _onPress);
        StatusChanged?.Invoke();
    }

    public bool TryChange(HotkeyDef def, out string error)
    {
        error = "";
        if (def.Validate() is string invalid) { error = invalid; return false; }

        if (!_hk.Register(def, _onPress))
        {
            error = _hk.LastError == GlobalHotkey.ERROR_HOTKEY_ALREADY_REGISTERED
                ? $"{def.Label} já é usado por outro programa. Tente outra combinação."
                : $"O Windows recusou {def.Label} (erro {_hk.LastError}). Tente outra combinação.";
            return false;
        }

        bool changed = def != Current;
        Current = def;
        IsRegistered = true;
        if (changed)
        {
            _settings.Hotkey = def.Label;
            Storage.SaveSettings(_settings);
        }
        UpdateStartMenuShortcut();
        StatusChanged?.Invoke();
        return true;
    }

    private void UpdateStartMenuShortcut()
    {
        var def = Current;
        var text = IsRegistered ? def.ShortcutText : null;
        Task.Run(() => StartMenuShortcut.Ensure(text)).ContinueWith(t =>
        {
            if (t.Result is string other)
            {
                Storage.LogInfo($"Atalho do Menu Iniciar sem tecla: \"{other}\" já usa {def.Label}");
                Warning?.Invoke($"{def.Label} em conflito",
                    $"O atalho \"{other}\" já usa {def.Label}. Com o Mission4Me aberto funciona; com ele fechado, não.");
            }
        });
    }

    public void Dispose() => _hk.Dispose();
}
