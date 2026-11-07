using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Mission4Me.Core;

namespace Mission4Me.UI;

/// <summary>Ícone na bandeja com o menu de controle. O ícone muda de cor conforme o estado.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon = new();
    private readonly MissionService _svc;
    private readonly Settings _settings;
    private readonly Action _openMission, _exit, _settingsChanged;
    private Phase _iconPhase = (Phase)(-1);
    private Icon? _current;
    private string? _lastTip;

    public string HotkeyLabel { get; set; } = "Ctrl+Alt+M";
    public bool HotkeyOk { get; set; } = true;
    public Action? OpenHotkeySettings { get; set; }
    public Action? OpenQueue { get; set; }

    public TrayIcon(MissionService svc, Settings settings, Action openMission, Action settingsChanged, Action exit)
    {
        _svc = svc; _settings = settings; _openMission = openMission; _settingsChanged = settingsChanged; _exit = exit;
        _icon.ContextMenuStrip = new ContextMenuStrip { Renderer = new DarkRenderer(), ShowImageMargin = false };
        _icon.ContextMenuStrip.Opening += (_, e) => { BuildMenu(); e.Cancel = false; };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OnLeftClick(); };
        BuildMenu();
        Refresh();
        _icon.Visible = true;
    }

    private void OnLeftClick()
    {
        _openMission(); // sem missão: nova; com missão: edição
    }

    public void Refresh()
    {
        var p = _svc.Phase;
        if (p != _iconPhase) { SetIcon(p); _iconPhase = p; }
        var tip = _svc.IsActive ? $"{_svc.Current!.Project} · {StatusLine()}" : "Mission4Me · sem missão (clique para iniciar)";
        if (tip.Length > 63) tip = tip[..62] + "…";
        if (tip != _lastTip) { _icon.Text = tip; _lastTip = tip; }
    }

    public void Balloon(string title, string text, ToolTipIcon kind) => _icon.ShowBalloonTip(5000, title, text, kind);

    private string StatusLine() => _svc.Phase switch
    {
        Phase.Paused   => "pausado",
        Phase.Overtime => $"{MissionService.FormatClock(_svc.Remaining)} além da meta",
        _              => $"faltam {MissionService.FormatClock(_svc.Remaining)}",
    };

    // ---------------------------------------------------------------- menu
    private void BuildMenu()
    {
        var m = _icon.ContextMenuStrip!;
        m.Items.Clear();

        if (_svc.IsActive)
        {
            m.Items.Add(new ToolStripLabel(_svc.Current!.Project) { Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) });
            m.Items.Add(new ToolStripLabel(StatusLine()) { ForeColor = Color.FromArgb(0x8B, 0x98, 0xA5) });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add($"✎  Editar missão…   {HotkeyLabel}", null, (_, _) => _openMission());
            m.Items.Add(_svc.IsPaused ? "▶  Retomar" : "❚❚  Pausar", null, (_, _) => _svc.TogglePause());
            m.Items.Add("+10 min", null, (_, _) => _svc.AddTime(TimeSpan.FromMinutes(10)));
            m.Items.Add("+25 min", null, (_, _) => _svc.AddTime(TimeSpan.FromMinutes(25)));
            m.Items.Add("■  Encerrar missão", null, (_, _) => _svc.End());
        }
        else
        {
            m.Items.Add($"▶  Nova missão…   {HotkeyLabel}", null, (_, _) => _openMission());
            double today = ActivityLog.MinutesToday();
            if (today >= 1)
                m.Items.Add(new ToolStripLabel($"Hoje: {MissionService.FormatShort(TimeSpan.FromMinutes(today))} em missão")
                    { ForeColor = Color.FromArgb(0x8B, 0x98, 0xA5) });
        }

        int queued = MissionQueue.Items.Count;
        m.Items.Add(queued > 0 ? $"☰  Próximas missões ({queued})…" : "☰  Próximas missões…", null, (_, _) => OpenQueue?.Invoke());

        m.Items.Add(new ToolStripSeparator());

        var pos = new ToolStripMenuItem("Posição do contador");
        pos.DropDownItems.Add(Check("Ao lado da bandeja", _settings.Position == OverlayPosition.NearTray,
            () => { _settings.Position = OverlayPosition.NearTray; _settingsChanged(); }));
        pos.DropDownItems.Add(Check("Canto esquerdo", _settings.Position == OverlayPosition.Left,
            () => { _settings.Position = OverlayPosition.Left; _settingsChanged(); }));
        StyleDropDown(pos);
        m.Items.Add(pos);

        var inv = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
        var pulse = new ToolStripMenuItem(_settings.ColorPulse
            ? $"Pulso de cor: {Math.Round(_settings.PulseHz * 60)}× por minuto"
            : "Pulso de cor: desligado");
        pulse.DropDownItems.Add(Check("Desligado", !_settings.ColorPulse,
            () => { _settings.ColorPulse = false; _settingsChanged(); }));
        pulse.DropDownItems.Add(new ToolStripSeparator());
        foreach (var hz in ColorPulse.Frequencies)
        {
            var f = hz;
            string text = $"{Math.Round(f * 60)}× por minuto  ·  {f.ToString("0.##", inv)} Hz  ·  ciclo {(1 / f).ToString("0.0#", inv)} s  ·  {ColorPulse.FramesPerCycle(f)} passos";
            pulse.DropDownItems.Add(Check(text, _settings.ColorPulse && Math.Abs(_settings.PulseHz - f) < 1e-6,
                () => { _settings.ColorPulse = true; _settings.PulseHz = f; _settingsChanged(); }));
        }
        StyleDropDown(pulse);
        m.Items.Add(pulse);

        var hk = new ToolStripMenuItem(HotkeyOk ? $"Atalho: {HotkeyLabel}  ✓  (alterar…)" : $"Atalho: {HotkeyLabel}  ⚠ em uso — alterar…");
        if (!HotkeyOk) hk.ForeColor = Color.FromArgb(0xFC, 0xD3, 0x4D);
        hk.Click += (_, _) => OpenHotkeySettings?.Invoke();
        m.Items.Add(hk);

        m.Items.Add(Check("Iniciar com o Windows", AutoStart.IsEnabled, () => AutoStart.Set(!AutoStart.IsEnabled)));
        m.Items.Add("Exportar histórico (cópia legível)…", null, (_, _) => ExportData());
        m.Items.Add("Abrir pasta de dados / log", null, (_, _) => OpenFolder());
        m.Items.Add("Barra não mudou de cor? Reiniciar Explorer", null, (_, _) => TaskbarTheme.RestartExplorer());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Sair", null, (_, _) => _exit());

        foreach (ToolStripItem i in m.Items) i.ForeColor = i.ForeColor == SystemColors.ControlText ? DarkRenderer.TextColor : i.ForeColor;
    }

    private static ToolStripMenuItem Check(string text, bool on, Action toggle)
    {
        var i = new ToolStripMenuItem(on ? "✓  " + text : "     " + text);
        i.Click += (_, _) => toggle();
        return i;
    }

    private static void StyleDropDown(ToolStripMenuItem parent)
    {
        if (parent.DropDown is ToolStripDropDownMenu dd) { dd.Renderer = new DarkRenderer(); dd.ShowImageMargin = false; }
        foreach (ToolStripItem i in parent.DropDownItems) i.ForeColor = DarkRenderer.TextColor;
    }

    private static void OpenFolder()
    {
        System.IO.Directory.CreateDirectory(Storage.Dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Storage.Dir) { UseShellExecute = true });
    }

    /// <summary>
    /// Os dados ficam cifrados em %APPDATA%\Mission4Me. Exportar gera, onde você escolher,
    /// uma cópia legível (CSV para Excel + JSONL com a linha do tempo) — ação explícita do usuário.
    /// </summary>
    private static void ExportData()
    {
        try
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Onde salvar a cópia legível do histórico?",
                UseDescriptionForTitle = true,
                SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
            var csv = System.IO.Path.Combine(dlg.SelectedPath, $"Mission4Me-sessoes-{stamp}.csv");
            var events = System.IO.Path.Combine(dlg.SelectedPath, $"Mission4Me-eventos-{stamp}.jsonl");
            System.IO.File.WriteAllLines(csv, ActivityLog.ExportSessionLines(), new System.Text.UTF8Encoding(true)); // BOM: Excel lê acentos
            System.IO.File.WriteAllLines(events, ActivityLog.ExportEventLines(), new System.Text.UTF8Encoding(false));

            var explorer = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            System.Diagnostics.Process.Start(explorer, $"/select,\"{csv}\"");
        }
        catch (Exception ex)
        {
            Storage.LogError("Exportar", ex);
            MessageBox.Show("Não foi possível exportar: " + Storage.Redact(ex.Message), "Mission4Me",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ---------------------------------------------------------------- ícone
    private void SetIcon(Phase p)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(3, 3, 26, 26);
            if (p == Phase.Idle)
            {
                using var pen = new Pen(Color.FromArgb(0xB0, 0xBA, 0xC4), 3f);
                g.DrawEllipse(pen, rect);
                using var dot = new SolidBrush(Color.FromArgb(0xB0, 0xBA, 0xC4));
                g.FillEllipse(dot, 12, 12, 8, 8);
            }
            else
            {
                var (a, b) = p switch
                {
                    Phase.Warning  => (Color.FromArgb(0xD9, 0x77, 0x06), Color.FromArgb(0x7C, 0x4A, 0x03)),
                    Phase.Overtime => (Color.FromArgb(0xE5, 0x48, 0x4D), Color.FromArgb(0x7F, 0x1D, 0x2D)),
                    Phase.Paused   => (Color.FromArgb(0x5B, 0x6B, 0x7C), Color.FromArgb(0x33, 0x41, 0x55)),
                    _              => (Color.FromArgb(0x2E, 0xC4, 0xB6), Color.FromArgb(0x1E, 0x5A, 0xA8)),
                };
                using var br = new LinearGradientBrush(rect, a, b, 45f);
                g.FillEllipse(br, rect);
                using var white = new SolidBrush(Color.White);
                g.FillEllipse(white, 12, 12, 8, 8);
            }
        }
        IntPtr h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        Native.DestroyIcon(h);
        _icon.Icon = icon;
        _current?.Dispose();
        _current = icon;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _current?.Dispose();
    }

    // -------------------------------------------------------- menu escuro
    private sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public static readonly Color TextColor = Color.FromArgb(0xE6, 0xED, 0xF3);
        private static readonly Color Bg = Color.FromArgb(0x13, 0x1D, 0x28);
        private static readonly Color Hover = Color.FromArgb(0x10, 0x3A, 0x3A);
        private static readonly Color Line = Color.FromArgb(0x22, 0x30, 0x3F);

        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var r = new Rectangle(Point.Empty, e.Item.Size);
            using var b = new SolidBrush(e.Item.Selected && e.Item.Enabled ? Hover : Bg);
            e.Graphics.FillRectangle(b, r);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var p = new Pen(Line);
            e.Graphics.DrawLine(p, 6, y, e.Item.Width - 6, y);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = TextColor;
            base.OnRenderArrow(e);
        }

        private sealed class DarkColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Bg;
            public override Color MenuBorder => Line;
            public override Color MenuItemBorder => Hover;
            public override Color ImageMarginGradientBegin => Bg;
            public override Color ImageMarginGradientMiddle => Bg;
            public override Color ImageMarginGradientEnd => Bg;
        }
    }
}
