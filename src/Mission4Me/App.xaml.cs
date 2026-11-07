using System;
using System.Linq;
using System.Media;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Mission4Me.Core;
using Mission4Me.UI;

namespace Mission4Me;

public partial class App : Application
{
    private Mutex? _single;
    private MissionService _svc = null!;
    private Settings _settings = null!;
    private TrayIcon? _tray;
    private OverlayWindow? _overlay;
    private MissionWindow? _missionWindow;
    private DispatcherTimer? _timer;
    private bool _exiting;
    private HotkeyManager? _hotkeys;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            Startup2(e);
        }
        catch (Exception ex)
        {
            Storage.LogError("Startup", ex);
            try { TaskbarTheme.Restore(); } catch { }
            ShowError("O Mission4Me não conseguiu iniciar.", ex);
            _tray?.Dispose();
            try { _single?.ReleaseMutex(); } catch { }
            Shutdown(1);
        }
    }

    private bool _errorShown;

    /// <summary>Chamado pelo desinstalador: deixa o Windows como estava antes do Mission4Me.</summary>
    public const string UninstallArg = "--desinstalar";

    private static void UninstallCleanup()
    {
        try { TaskbarTheme.Restore(); } catch (Exception ex) { Storage.LogError("Uninstall/barra", ex); }   // também remove o RunOnce
        try { AutoStart.Set(false); } catch (Exception ex) { Storage.LogError("Uninstall/autostart", ex); }
        // os dados do usuário (%APPDATA%\Mission4Me) ficam: o desinstalador pergunta se deve apagar
    }

    private void ShowError(string title, Exception ex)
    {
        if (_errorShown) return;   // um aviso só, para não virar cascata de janelas
        _errorShown = true;
        var detail = Storage.Redact(ex.ToString());
        if (detail.Length > 1500) detail = detail[..1500] + "…";
        MessageBox.Show($"{title}\n\n{ex.GetType().Name}: {Storage.Redact(ex.Message)}\n\nDetalhes em:\n%APPDATA%\\Mission4Me\\erros.log\n\n{detail}",
                        "Mission4Me — erro", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void Startup2(StartupEventArgs e)
    {

        bool openRequested = e.Args.Any(a => a.Equals(StartMenuShortcut.LaunchArg, StringComparison.OrdinalIgnoreCase));

        bool exitRequested = e.Args.Any(a => a.Equals("--sair", StringComparison.OrdinalIgnoreCase));
        bool uninstall = e.Args.Any(a => a.Equals(UninstallArg, StringComparison.OrdinalIgnoreCase));

        _single = new Mutex(true, @"Local\Mission4Me.SingleInstance", out bool first);
        if (!first)
        {
            bool restoreOnly = e.Args.Any(a => a.Equals(TaskbarTheme.RestoreArg, StringComparison.OrdinalIgnoreCase));
            if (exitRequested || uninstall) SingleInstance.SignalExit();
            else if (!restoreOnly) SingleInstance.SignalOpen();
            if (uninstall)
            {
                try { _single.WaitOne(TimeSpan.FromSeconds(8)); } catch (AbandonedMutexException) { }
                UninstallCleanup();
            }
            Shutdown();
            return;
        }
        if (exitRequested) { Shutdown(); return; } // nada rodando para fechar
        if (uninstall) { UninstallCleanup(); Shutdown(); return; }

        // dados de versões anteriores ainda em texto puro passam a ficar cifrados (DPAPI)
        SecureStore.EncryptLegacyFiles(Storage.Dir);


        // login após um travamento: só devolve a cor original da barra e sai.
        // (se o Mission4Me já estiver aberto, o mutex acima já encerrou esta instância)
        if (e.Args.Any(a => a.Equals(TaskbarTheme.RestoreArg, StringComparison.OrdinalIgnoreCase)))
        {
            try { TaskbarTheme.Restore(); } catch (Exception ex) { Storage.LogError("RestoreAtLogon", ex); }
            Shutdown();
            return;
        }
        SingleInstance.Listen(() => Dispatcher.BeginInvoke(OpenMissionWindow),
                              () => Dispatcher.BeginInvoke(QuietExit));

        // qualquer falha inesperada: devolve a barra ao normal antes de qualquer coisa
        DispatcherUnhandledException += (_, ex) =>
        {
            Storage.LogError("Unhandled", ex.Exception);
            ex.Handled = true;
            ShowError("Ocorreu um erro no Mission4Me.", ex.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            if (ex.ExceptionObject is Exception x) Storage.LogError("Fatal", x);
            try { TaskbarTheme.Restore(); } catch { }
        };
        SessionEnding += (_, _) => { try { TaskbarTheme.Restore(); } catch { } };

        _settings = Storage.LoadSettings();
        if (_settings.PulseVersion < 2)     // configuração de antes do ajuste de velocidade
        {
            _settings.PulseHz = 0.25;
            _settings.PulseVersion = 2;
            Storage.SaveSettings(_settings);
        }
        _settings.PulseHz = ColorPulse.Snap(_settings.PulseHz);
        ColorPulse.Instance.Configure(_settings.ColorPulse, _settings.PulseHz);
        SetupLagoonPulse();
        _svc = new MissionService();

        var saved = Storage.LoadMission();
        TaskbarTheme.RecoverIfNeeded(saved != null && saved.PausedAtUtc == null);
        if (saved != null) _svc.Resume(saved);

        _overlay = new OverlayWindow(_svc, () => _settings);
        _tray = new TrayIcon(_svc, _settings, OpenMissionWindow, OnSettingsChanged, ExitApp);

        _overlay.Clicked += OpenMissionWindow;
        _wasActive = _svc.IsActive;
        _svc.Changed += () =>
        {
            _overlay.Refresh(); _tray.Refresh(); UpdateLagoonPulse();
            // missão acabou: sugere a próxima da fila (adiado para não disparar numa troca direta encerrar→iniciar)
            if (_wasActive && !_svc.IsActive)
                Dispatcher.BeginInvoke(SuggestNextFromQueue, DispatcherPriority.Background);
            _wasActive = _svc.IsActive;
        };
        _tray.OpenQueue = () => { OpenMissionWindow(); _missionWindow?.ShowQueue(); };
        UpdateLagoonPulse();
        _svc.Milestone += OnMilestone;

        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) =>
        {
            _svc.Tick(); _overlay.Refresh(); _tray.Refresh();
            ColorPulse.Instance.SetVisible(!_locked && (_overlay.IsVisible || _missionWindow != null));
        };
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        _timer.Start();

        // Atalho global com o app aberto; atalho do Menu Iniciar com o app fechado
        _hotkeys = new HotkeyManager(_settings, OpenMissionWindow);
        _hotkeys.Warning += (title, msg) => Dispatcher.BeginInvoke(() =>
            _tray?.Balloon(title, msg, System.Windows.Forms.ToolTipIcon.Warning));
        _hotkeys.StatusChanged += () =>
        {
            _tray.HotkeyLabel = _hotkeys.Current.Label;
            _tray.HotkeyOk = _hotkeys.IsRegistered;
        };
        _tray.OpenHotkeySettings = () => { OpenMissionWindow(); _missionWindow?.ShowHotkeyEditor(); };
        _hotkeys.Apply();

        _overlay.Refresh();
        if (!_svc.IsActive || openRequested) OpenMissionWindow();
        else _tray.Balloon("Missão retomada", $"{_svc.Current!.Project} — o tempo continuou contando.", System.Windows.Forms.ToolTipIcon.Info);
    }

    private void OpenMissionWindow()
    {
        if (_hotkeys == null) return;           // ainda inicializando
        if (_missionWindow != null) { _missionWindow.Activate(); return; }
        _missionWindow = new MissionWindow(_svc, _settings, _hotkeys!);
        _missionWindow.Closed += (_, _) => _missionWindow = null;
        if (_svc.IsActive && _overlay is { IsVisible: true } o)
            _missionWindow.AnchorAbove(o.Left, o.Top, o.ActualWidth);
        _missionWindow.Show();
    }

    private readonly System.Collections.Generic.List<ColorPulse.Channel> _lagoon = new();

    /// <summary>
    /// Liga as paradas do degradê "Lagoon" (faixa do topo e botão principal da janela)
    /// ao motor de pulso.
    /// </summary>
    private void SetupLagoonPulse()
    {
        var brush = LagoonBrush.Brush;
        for (int i = 0; i < brush.GradientStops.Count && i < Palette.LagoonPulse.Length; i++)
        {
            var stop = brush.GradientStops[i];
            _lagoon.Add(ColorPulse.Instance.Register(c => stop.Color = c, stop.Color));
        }
    }

    private void UpdateLagoonPulse()
    {
        bool live = _svc.IsActive && !_svc.IsPaused;
        for (int i = 0; i < _lagoon.Count; i++)
        {
            var (b, m, r) = Palette.LagoonPulse[i];
            ColorPulse.Instance.Target(_lagoon[i], b, m, r, live);
        }
    }

    private bool _locked;
    private bool _wasActive;

    private void SuggestNextFromQueue()
    {
        if (_svc.IsActive || _tray == null || MissionQueue.Items.Count == 0) return;
        var next = MissionQueue.Items[0];
        var key = _hotkeys?.Current.Label ?? "o ícone da bandeja";
        _tray.Balloon("Próxima da fila",
            $"{next.Project} · {MissionService.FormatShort(TimeSpan.FromMinutes(next.Minutes))}. Abra com {key} e clique nela em A FAZER.",
            System.Windows.Forms.ToolTipIcon.Info);
    }

    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionLock or Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect
                     or Microsoft.Win32.SessionSwitchReason.RemoteDisconnect)
            _locked = true;
        else if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionUnlock or Microsoft.Win32.SessionSwitchReason.ConsoleConnect
                          or Microsoft.Win32.SessionSwitchReason.RemoteConnect)
            _locked = false;
    }

    private void OnSettingsChanged()
    {
        Storage.SaveSettings(_settings);
        ColorPulse.Instance.Configure(_settings.ColorPulse, _settings.PulseHz);
        _overlay?.ForceReposition();
        _overlay?.Refresh();
    }

    private void OnMilestone(Phase p)
    {
        if (_tray == null || _svc.Current == null) return;
        if (p == Phase.Warning)
        {
            _tray.Balloon("Reta final", $"{_svc.Current.Project}: faltam {MissionService.FormatClock(_svc.Remaining)}.",
                          System.Windows.Forms.ToolTipIcon.Info);
        }
        else if (p == Phase.Overtime)
        {
            SystemSounds.Asterisk.Play();
            _tray.Balloon("Tempo da missão concluído",
                          $"{_svc.Current.Project}: meta de {MissionService.FormatShort(_svc.Current.Planned)} atingida. " +
                          "Encerre pela bandeja ou continue em tempo extra.",
                          System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    private void ExitApp()
    {
        if (_exiting) return;
        if (_svc.IsActive)
        {
            var r = MessageBox.Show(
                $"A missão \"{_svc.Current!.Project}\" está ativa.\n\nSim: encerra a missão e fecha.\nNão: fecha e retoma a missão na próxima vez que abrir.",
                "Mission4Me", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return;
            if (r == MessageBoxResult.Yes) _svc.End("interrompida");
        }
        _exiting = true;
        try { TaskbarTheme.Restore(); } catch { }
        Shutdown();
    }

    /// <summary>
    /// Fecha sem perguntar (pedido do build.bat): devolve a cor da barra e mantém a missão salva,
    /// que é retomada quando o app abrir de novo.
    /// </summary>
    private void QuietExit()
    {
        if (_exiting) return;
        _exiting = true;
        _missionWindow?.Close();
        try { TaskbarTheme.Restore(); } catch { }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _timer?.Stop();
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;   // evento estático: sempre desinscrever
        _hotkeys?.Dispose();
        SingleInstance.Stop();
        try { TaskbarTheme.Restore(); } catch { }
        _tray?.Dispose();
        try { _single?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
