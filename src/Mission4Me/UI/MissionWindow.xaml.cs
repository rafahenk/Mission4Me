using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Mission4Me.Core;

namespace Mission4Me.UI;

/// <summary>
/// Mesma janela para dois modos:
///  • sem missão  → "Nova missão" (centralizada)
///  • com missão  → "Editar missão" (ancorada logo acima do contador; fecha ao clicar fora)
/// </summary>
public partial class MissionWindow : Window
{
    private readonly MissionService _svc;
    private readonly Settings _settings;
    private readonly bool _editing;
    private readonly HotkeyManager _hotkeys;
    private bool _capturing;
    private bool _closing;
    private (double Left, double Top, double Width)? _anchor;

    public MissionWindow(MissionService svc, Settings settings, HotkeyManager hotkeys)
    {
        InitializeComponent();
        _svc = svc;
        _settings = settings;
        _editing = svc.IsActive;
        _hotkeys = hotkeys;
        _hotkeys.StatusChanged += RefreshHotkeyRow;
        Closed += (_, _) =>
        {
            _hotkeys.StatusChanged -= RefreshHotkeyRow;
            if (_capturing) _hotkeys.Resume();   // fechou no meio da gravação: volta o atalho anterior
        };
        RefreshHotkeyRow();

        if (_editing) SetupEdit(); else SetupNew();
        SetupHistory();

        // ancorada acima do contador: se a janela crescer (painéis), mantém a base no lugar
        SizeChanged += (_, _) => { if (IsLoaded) PlaceNearAnchor(); };

        Loaded += (_, _) =>
        {
            PlaceNearAnchor();
            Activate();
            ProjectBox.Focus();
            if (_editing) ProjectBox.SelectAll();
        };
        PreviewKeyDown += (_, e) => { if (!_capturing && !_rowEditing && e.Key == Key.Escape) SafeClose(); };
        Validate();
    }

    // -------------------------------------------------------------- modos
    private void SetupNew()
    {
        foreach (var p in ActivityLog.RecentProjects())
        {
            var b = new Button { Content = p, Style = (Style)FindResource("Chip"), Tag = "" };
            b.Click += (_, _) => { ProjectBox.Text = p; ProjectBox.CaretIndex = p.Length; DurationBox.Focus(); };
            RecentPanel.Children.Add(b);
        }
        if (RecentPanel.Children.Count == 0) RecentPanel.Visibility = Visibility.Collapsed;

        DurationBox.Text = _settings.LastMinutes.ToString();
        double today = ActivityLog.MinutesToday();
        TodayText.Text = today >= 1
            ? $"Hoje: {MissionService.FormatShort(TimeSpan.FromMinutes(today))} em missão"
            : "Enter inicia · Esc fecha";
    }

    private void SetupEdit()
    {
        var m = _svc.Current!;
        Title = "Mission4Me — Editar missão";
        ShowInTaskbar = false;
        HeaderLabel.Text = _svc.IsPaused ? "MISSÃO PAUSADA" : "MISSÃO EM ANDAMENTO";
        HeaderTitle.Text = "Editar missão";
        DurationLabel.Text = "Meta total da missão";
        RecentPanel.Visibility = Visibility.Collapsed;

        ProjectBox.Text = m.Project;
        DurationBox.Text = MissionService.FormatShort(m.Planned).Replace(" ", "");

        StartText.Text = "Salvar alterações";
        StartIcon.Data = Geometry.Parse("M0,6 L4,10 L12,1");
        StartIcon.Fill = null;
        StartIcon.Stroke = Brushes.White;
        StartIcon.StrokeThickness = 2;

        EditActions.Visibility = Visibility.Visible;
        InterruptButton.Visibility = Visibility.Visible;
        BuildInterruptOptions();
        PauseText.Text = _svc.IsPaused ? "▶  Retomar" : "❚❚  Pausar";
        TodayText.Text = $"Já feito: {MissionService.FormatShort(_svc.Elapsed)} · Enter salva · Esc fecha";

        // comportamento de "popup": clicou fora, fecha
        Deactivated += (_, _) => SafeClose();
    }

    /// <summary>Posição do contador (em DIPs) para abrir a janela logo acima dele.</summary>
    public void AnchorAbove(double left, double top, double width)
    {
        _anchor = (left, top, width);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000; Top = -10000; // evita "piscar" no centro antes de medir
    }

    private bool _placed;

    /// <summary>
    /// 1ª vez: centraliza o card sobre o contador (modo edição) ou deixa o CenterScreen agir.
    /// Depois (janela cresce com painéis/histórico): mantém a esquerda, base ancorada e tudo dentro da tela.
    /// </summary>
    private void PlaceNearAnchor()
    {
        var wa = SystemParameters.WorkArea;
        double x = Left, y = Top;
        if (_anchor is { } a)
        {
            if (!_placed) x = a.Left + a.Width / 2 - (18 + Card.ActualWidth / 2);
            y = a.Top - ActualHeight + 10;               // 18px de margem da sombra já dão o respiro
        }
        else if (!_placed) { _placed = true; return; }
        _placed = true;
        Left = Math.Clamp(x, wa.Left, Math.Max(wa.Left, wa.Right - ActualWidth));
        Top  = Math.Clamp(y, wa.Top,  Math.Max(wa.Top,  wa.Bottom - ActualHeight));
    }

    // ----------------------------------------------------------- entrada
    private void OnPreset(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.CommandParameter is string min)
        {
            DurationBox.Text = min;
            if (string.IsNullOrWhiteSpace(ProjectBox.Text)) ProjectBox.Focus();
        }
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e) => Validate();

    private void Validate()
    {
        if (StartButton == null || HintText == null || _svc == null) return; // ainda inicializando
        bool okDur = MissionService.TryParseDuration(DurationBox.Text, out var dur);

        if (!okDur)
            HintText.Text = "Ex.: 45 · 90m · 1h30 · 1:15";
        else if (!_editing)
            HintText.Text = $"Término previsto às {DateTime.Now.Add(dur):HH:mm}";
        else
        {
            var left = dur - _svc.Elapsed;
            HintText.Text = left > TimeSpan.Zero
                ? $"Faltarão {MissionService.FormatShort(left)}" + (_svc.IsPaused ? "" : $" · término às {DateTime.Now.Add(left):HH:mm}")
                : "Meta menor que o já feito: a missão entra em tempo extra.";
        }
        HintText.Foreground = (Brush)FindResource(okDur ? "Teal" : "Muted");
        StartButton.IsEnabled = okDur && !string.IsNullOrWhiteSpace(ProjectBox.Text);

        foreach (var c in PresetPanel.Children)
            if (c is Button b)
                b.Tag = okDur && b.CommandParameter is string m && int.Parse(m) == (int)Math.Round(dur.TotalMinutes) ? "selected" : "";
    }

    // ------------------------------------------------------------ ações
    private void OnStart(object sender, RoutedEventArgs e)
    {
        if (!MissionService.TryParseDuration(DurationBox.Text, out var dur) || string.IsNullOrWhiteSpace(ProjectBox.Text)) return;

        var project = ProjectBox.Text;
        SafeClose();
        if (_editing && _svc.IsActive)
            _svc.Edit(project, dur);
        else
        {
            _settings.LastMinutes = (int)dur.TotalMinutes;
            Storage.SaveSettings(_settings);
            _svc.Start(project, dur);
            if (_fromQueueId != null) MissionQueue.Remove(_fromQueueId, started: true);
        }
    }

    private void OnPause(object sender, RoutedEventArgs e)
    {
        SafeClose();
        _svc.TogglePause();
    }

    /// <summary>Um clique: fecha a janela, encerra a missão e grava no histórico.</summary>
    private void OnEnd(object sender, RoutedEventArgs e)
    {
        SafeClose();
        _svc.End();
    }

    private void OnClose(object sender, RoutedEventArgs e) => SafeClose();

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    // ------------------------------------------------------ atalho de teclado
    private static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(0xFC, 0xD3, 0x4D));

    private void RefreshHotkeyRow()
    {
        HotkeyBadgeText.Text = _hotkeys.Current.Label;
        HotkeyStatus.Text = _hotkeys.IsRegistered ? "✓ ativo" : "⚠ em uso por outro programa";
        HotkeyStatus.Foreground = _hotkeys.IsRegistered ? (Brush)FindResource("Teal") : Amber;
    }

    /// <summary>Abre a janela já com a gravação de atalho ativa (usado pelo menu da bandeja).</summary>
    public void ShowHotkeyEditor()
    {
        if (!_capturing) StartCapture();
    }

    private void OnHotkeyEdit(object sender, RoutedEventArgs e)
    {
        if (_capturing) EndCapture(); else StartCapture();
    }

    private void StartCapture()
    {
        _capturing = true;
        _hotkeys.Suspend();
        HotkeyPanel.Visibility = Visibility.Visible;
        HotkeyEditButton.Content = "Cancelar";
        HotkeyBox.Text = "Pressione a combinação…";
        HotkeyHint.Text = "Ctrl e/ou Alt (Shift opcional) + letra, número ou F1–F12. Esc cancela.";
        HotkeyHint.Foreground = (Brush)FindResource("Muted");
        Dispatcher.BeginInvoke(() => HotkeyBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void EndCapture()
    {
        if (!_capturing) return;
        _capturing = false;
        HotkeyPanel.Visibility = Visibility.Collapsed;
        HotkeyEditButton.Content = "Alterar";
        _hotkeys.Resume();      // re-registra o atalho vigente (o novo, se a troca deu certo)
        ProjectBox.Focus();
    }

    private void OnHotkeyKey(object sender, KeyEventArgs e)
    {
        e.Handled = true;       // nada de Enter salvar a missão ou Esc fechar a janela aqui
        var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };
        var mods = Keyboard.Modifiers;

        if (key == Key.Escape && mods == ModifierKeys.None) { EndCapture(); return; }

        string prefix = (mods.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") +
                        (mods.HasFlag(ModifierKeys.Shift) ? "Shift+" : "") +
                        (mods.HasFlag(ModifierKeys.Alt) ? "Alt+" : "");

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
                or Key.LWin or Key.RWin)
        {
            HotkeyBox.Text = prefix.Length > 0 ? prefix + "…" : "Pressione a combinação…";
            return;
        }

        var def = HotkeyDef.FromWpf(mods, key);
        HotkeyBox.Text = prefix + def.KeyName;

        if (_hotkeys.TryChange(def, out var error))
        {
            HotkeyHint.Text = $"✓ {def.Label} salvo e ativo.";
            HotkeyHint.Foreground = (Brush)FindResource("Teal");
            _capturing = false;               // TryChange já registrou o novo
            HotkeyPanel.Visibility = Visibility.Collapsed;
            HotkeyEditButton.Content = "Alterar";
            HotkeyStatus.Text = "✓ salvo";
            ProjectBox.Focus();
        }
        else
        {
            HotkeyHint.Text = "⚠ " + error;
            HotkeyHint.Foreground = Amber;
        }
    }

    // ---------------------------------------------------------- interrupção
    private Button? _selectedOption;
    private InterruptionPreset? _selectedPreset;   // null + opção selecionada = "Outro motivo"

    private void BuildInterruptOptions()
    {
        foreach (var p in _settings.Interruptions) AddOption(p.Label, p.Minutes, p);
        AddOption("Outro motivo…", null, null);
    }

    private void AddOption(string label, int? minutes, InterruptionPreset? preset)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        if (minutes is int min)
        {
            var t = new TextBlock
            {
                Text = MissionService.FormatShort(TimeSpan.FromMinutes(min)),
                Foreground = (Brush)FindResource("Muted"), FontSize = 12.5, Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(t, 1);
            grid.Children.Add(t);
        }
        var b = new Button { Style = (Style)FindResource("OptionRow"), Content = grid, Tag = "" };
        b.Click += (_, _) => SelectOption(b, preset);
        InterruptOptions.Children.Add(b);
    }

    private void SelectOption(Button b, InterruptionPreset? preset)
    {
        foreach (var c in InterruptOptions.Children) if (c is Button o) o.Tag = "";
        b.Tag = "selected";
        _selectedOption = b;
        _selectedPreset = preset;

        bool custom = preset == null;
        CustomReasonPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        InterruptDurationBox.Text = MissionService.FormatShort(TimeSpan.FromMinutes(preset?.Minutes ?? 30)).Replace(" ", "");
        if (custom) Dispatcher.BeginInvoke(() => ReasonBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        ValidateInterrupt();
    }

    private void OnInterruptOpen(object sender, RoutedEventArgs e) => ShowInterrupt(true);
    private void OnInterruptBack(object sender, RoutedEventArgs e) => ShowInterrupt(false);

    private void ShowInterrupt(bool on)
    {
        MainPanel.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        InterruptPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        HeaderTitle.Text = on ? "Registrar interrupção" : "Editar missão";
        StartButton.IsDefault = !on;          // Enter confirma a ação do painel visível
        InterruptConfirm.IsDefault = on;
        if (on)
        {
            _selectedOption = null; _selectedPreset = null;
            foreach (var c in InterruptOptions.Children) if (c is Button o) o.Tag = "";
            CustomReasonPanel.Visibility = Visibility.Collapsed;
            ReasonBox.Text = "";
            InterruptDurationBox.Text = "30";
            AddTimeCheck.IsChecked = false;   // padrão: desmarcado
            ValidateInterrupt();
        }
        else ProjectBox.Focus();
    }

    private void OnInterruptInputChanged(object sender, TextChangedEventArgs e) => ValidateInterrupt();
    private void OnInterruptCheck(object sender, RoutedEventArgs e) => ValidateInterrupt();

    private void ValidateInterrupt()
    {
        if (InterruptConfirm == null || InterruptHint == null || _svc?.Current == null) return; // inicializando
        bool okDur = MissionService.TryParseDuration(InterruptDurationBox.Text, out var d);
        bool custom = _selectedOption != null && _selectedPreset == null;
        bool okReason = !custom || !string.IsNullOrWhiteSpace(ReasonBox.Text);

        InterruptConfirm.IsEnabled = _selectedOption != null && okDur && okReason;

        var planned = _svc.Current.Planned;
        InterruptHint.Text = _selectedOption == null ? "Escolha o motivo acima."
            : !okDur ? "Tempo inválido. Ex.: 15 · 30 · 1h · 1h30"
            : custom && !okReason ? "Descreva o que aconteceu."
            : AddTimeCheck.IsChecked == true
                ? $"A meta passa de {MissionService.FormatShort(planned)} para {MissionService.FormatShort(planned + d)}."
                : "A meta não muda; a interrupção fica registrada no histórico.";
    }

    private void OnInterruptConfirm(object sender, RoutedEventArgs e)
    {
        if (_selectedOption == null || !MissionService.TryParseDuration(InterruptDurationBox.Text, out var d)) return;
        bool custom = _selectedPreset == null;
        string category = custom ? "outro" : _selectedPreset!.Id;
        string reason = custom ? ReasonBox.Text.Trim() : _selectedPreset!.Label;
        if (custom && reason.Length == 0) return;
        bool add = AddTimeCheck.IsChecked == true;

        SafeClose();
        _svc.RegisterInterruption(category, reason, d, add);
    }

    // ------------------------------------------------------------ histórico
    private const double HistoryWidth = 408;   // painel 400 + 8 de respiro
    private string _sidePanel = "";                       // "", "fila" ou "historico"
    private bool _historyOpen => _sidePanel == "historico";
    private string _period = "7d";
    private string _statusFilter = "todas";
    private static readonly CultureInfo Pt = new("pt-BR");

    private void SetupHistory()
    {
        foreach (var (key, label) in new[] { ("hoje", "Hoje"), ("7d", "7 dias"), ("30d", "30 dias"), ("tudo", "Tudo") })
            PeriodChips.Children.Add(FilterChip(label, key, () => { _period = key; LoadHistory(); }));
        foreach (var (key, label) in new[] { ("todas", "Todas"), ("concluida", "Concluídas"), ("encerrada_antes", "Antes da meta"),
                                             ("interrompida", "Interrompidas"), ("em_andamento", "Em andamento") })
            StatusChips.Children.Add(FilterChip(label, key, () => { _statusFilter = key; LoadHistory(); }));

        // fila: valores iniciais, selo da aba e atualização automática
        NextDurationBox.Text = _settings.LastMinutes.ToString();
        QuickMinutes.Text = MissionService.FormatShort(TimeSpan.FromMinutes(_settings.LastMinutes)).Replace(" ", "");
        MissionQueue.Changed += OnQueueChanged;
        Closed += (_, _) => MissionQueue.Changed -= OnQueueChanged;
        RefreshQueueBadge();

        var start = _settings.SidePanel ?? (_settings.HistoryOpen ? "historico" : "");
        if (start is "fila" or "historico") SetSidePanel(start, animate: false);
    }

    private Button FilterChip(string label, string key, Action apply)
    {
        var b = new Button { Content = label, Style = (Style)FindResource("Chip"), Tag = "", FontSize = 12,
                             Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 6), CommandParameter = key };
        b.Click += (_, _) => apply();
        return b;
    }

    private void OnHistoryToggle(object sender, RoutedEventArgs e) =>
        SetSidePanel(_sidePanel == "historico" ? "" : "historico", animate: true);

    private void OnQueueToggle(object sender, RoutedEventArgs e) =>
        SetSidePanel(_sidePanel == "fila" ? "" : "fila", animate: true);

    /// <summary>Abre a janela já com a fila à mostra (usado pelo menu da bandeja).</summary>
    public void ShowQueue() => SetSidePanel("fila", animate: true);

    /// <summary>Abre/fecha/troca o painel lateral. Trocar entre fila e histórico não reanima a largura.</summary>
    private void SetSidePanel(string panel, bool animate)
    {
        bool wasOpen = _sidePanel != "";
        _sidePanel = panel;
        if (_settings.SidePanel != panel)
        {
            _settings.SidePanel = panel;
            _settings.HistoryOpen = panel == "historico";
            Storage.SaveSettings(_settings);
        }

        if (panel != "")   // ao fechar, mantém o conteúdo visível enquanto o painel recolhe
        {
            HistoryPanel.Visibility = panel == "historico" ? Visibility.Visible : Visibility.Collapsed;
            QueuePanel.Visibility = panel == "fila" ? Visibility.Visible : Visibility.Collapsed;
            if (panel == "historico") LoadHistory(); else LoadQueue();
        }
        ChevronFlip.ScaleX = panel == "historico" ? -1 : 1;
        QueueChevronFlip.ScaleX = panel == "fila" ? -1 : 1;

        if (wasOpen && panel != "") return;   // só trocou o conteúdo

        double to = panel != "" ? HistoryWidth : 0;
        if (animate)
        {
            // mantém o valor final (HoldEnd); a próxima animação parte de onde esta parou
            var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            HistoryHost.BeginAnimation(WidthProperty, anim);
        }
        else
        {
            HistoryHost.BeginAnimation(WidthProperty, null);
            HistoryHost.Width = to;
        }
    }

    // ============================================================ fila (A FAZER)
    private string? _fromQueueId;   // missão da fila carregada no formulário "Missão atual"

    private void OnQueueChanged()
    {
        RefreshQueueBadge();
        if (_sidePanel == "fila") LoadQueue();
        if (_nextMode) ValidateNext();
    }

    private void RefreshQueueBadge()
    {
        int n = MissionQueue.Items.Count;
        QueueBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueBadgeText.Text = n > 99 ? "99+" : n.ToString();
    }

    private void LoadQueue()
    {
        _rowEditing = false;
        QueueList.Children.Clear();
        var items = MissionQueue.Items;
        for (int i = 0; i < items.Count; i++) QueueList.Children.Add(QueueRow(items[i], i));
        QueueEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueFooter.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        QueueFooter.Text = _svc.IsActive
            ? "Clique numa missão para trocar a atual por ela."
            : "Clique numa missão para trazê-la para a execução.";
    }

    private UIElement QueueRow(QueuedMission q, int index)
    {
        var card = new Border
        {
            Background = (Brush)FindResource("Surface"), BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 6),
            Cursor = Cursors.Hand, Tag = q.Id,
        };
        card.Child = QueueRowView(q, index, card);
        return card;
    }

    private UIElement QueueRowView(QueuedMission q, int index, Border card)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var num = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = HexBrush("#103A3A"),
            Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = (index + 1).ToString(), Foreground = (Brush)FindResource("Teal"), FontSize = 11.5,
                                    FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
                                    VerticalAlignment = VerticalAlignment.Center },
        };
        grid.Children.Add(num);

        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        mid.Children.Add(new TextBlock { Text = q.Project, Foreground = (Brush)FindResource("Text"), FontSize = 13.5,
                                         FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        mid.Children.Add(new TextBlock { Text = $"meta {Fmt(q.Minutes)}  ·  na fila há {Age(q.CreatedUtc)}",
                                         Foreground = (Brush)FindResource("Muted"), FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(mid, 1);
        grid.Children.Add(mid);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                                       Visibility = Visibility.Collapsed, Margin = new Thickness(8, 0, -4, 0) };
        if (index > 0)
        {
            var up = new Button { Content = "↑", Style = (Style)FindResource("Link"), FontSize = 13, ToolTip = "Subir na fila" };
            up.Click += (_, _) => MissionQueue.MoveUp(q.Id);
            actions.Children.Add(up);
        }
        var ed = new Button { Content = "editar", Style = (Style)FindResource("Link"), FontSize = 11 };
        ed.Click += (_, _) => { _rowEditing = true; card.Child = QueueEditor(q, card); };
        actions.Children.Add(ed);
        var rm = new Button { Content = "remover", Style = (Style)FindResource("Link"), FontSize = 11, Foreground = HexBrush("#FF8A8E") };
        rm.Click += (_, _) => MissionQueue.Remove(q.Id, started: false);
        actions.Children.Add(rm);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        card.MouseEnter += (_, _) => { if (card.Child == grid) { actions.Visibility = Visibility.Visible; card.BorderBrush = (Brush)FindResource("Teal"); } };
        card.MouseLeave += (_, _) => { actions.Visibility = Visibility.Collapsed; if (card.Tag as string != "loaded") card.BorderBrush = (Brush)FindResource("Line"); };
        // os botões internos marcam o clique como tratado, então só o clique no "corpo" do card chega aqui
        card.MouseLeftButtonUp += (_, _) => { if (card.Child == grid) ActivateQueued(q, card); };
        return grid;
    }

    /// <summary>Edição inline de uma missão da fila (projeto e tempo previsto).</summary>
    private UIElement QueueEditor(QueuedMission q, Border card)
    {
        var muted = (Brush)FindResource("Muted");
        var project = new TextBox { Text = q.Project, Style = (Style)FindResource("FieldSm"), MaxLength = 60 };
        var minutes = new TextBox { Text = MissionService.FormatShort(TimeSpan.FromMinutes(q.Minutes)).Replace(" ", ""),
                                    Style = (Style)FindResource("FieldSm"), ToolTip = "Ex.: 45, 90m, 1h30, 2h" };
        var msg = new TextBlock { FontSize = 11, Foreground = muted, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "EDITAR PRÓXIMA MISSÃO", Foreground = (Brush)FindResource("Teal"),
                                           FontSize = 10.5, FontWeight = FontWeights.SemiBold });

        var fields = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        fields.ColumnDefinitions.Add(new ColumnDefinition());
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        var c1 = new StackPanel();
        c1.Children.Add(new TextBlock { Text = "Projeto", Foreground = muted, FontSize = 11.5, Margin = new Thickness(0, 0, 0, 4) });
        c1.Children.Add(project);
        var c2 = new StackPanel();
        c2.Children.Add(new TextBlock { Text = "Tempo", Foreground = muted, FontSize = 11.5, Margin = new Thickness(0, 0, 0, 4) });
        c2.Children.Add(minutes);
        Grid.SetColumn(c2, 2);
        fields.Children.Add(c1); fields.Children.Add(c2);
        panel.Children.Add(fields);
        panel.Children.Add(msg);

        var save = new Button { Style = (Style)FindResource("Secondary"), Height = 32, Content = "✓  Salvar", Foreground = (Brush)FindResource("Teal") };
        var cancel = new Button { Style = (Style)FindResource("Secondary"), Height = 32, Content = "Cancelar" };
        var buttons = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(cancel, 2);
        buttons.Children.Add(save); buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        bool Valid(out TimeSpan d)
        {
            bool okDur = MissionService.TryParseDuration(minutes.Text, out d);
            bool okName = !string.IsNullOrWhiteSpace(project.Text);
            msg.Text = !okName ? "Informe o projeto." : !okDur ? "Tempo inválido. Ex.: 45 · 1h30 · 2h" : "Enter salva · Esc cancela";
            msg.Foreground = okName && okDur ? muted : HexBrush("#FCD34D");
            save.IsEnabled = okName && okDur;
            return save.IsEnabled;
        }

        void Save()
        {
            if (!Valid(out var d)) return;
            _rowEditing = false;
            MissionQueue.Update(q.Id, project.Text, (int)Math.Round(d.TotalMinutes));
            LoadQueue();   // recarrega mesmo sem alteração (fecha o editor)
        }

        void Cancel() { _rowEditing = false; LoadQueue(); }

        foreach (var tb in new[] { project, minutes })
        {
            tb.TextChanged += (_, _) => Valid(out _);
            tb.PreviewKeyDown += (_, ev) =>
            {
                if (ev.Key == Key.Enter) { ev.Handled = true; Save(); }
                else if (ev.Key == Key.Escape) { ev.Handled = true; Cancel(); }
            };
        }
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => Cancel();

        Valid(out _);
        Dispatcher.BeginInvoke(() => { project.Focus(); project.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
        return panel;
    }

    /// <summary>Traz a missão da fila para a execução.</summary>
    private void ActivateQueued(QueuedMission q, Border card)
    {
        if (!_svc.IsActive)
        {
            // sem missão: carrega no formulário; Enter (ou Iniciar) começa
            SetMode(next: false);
            _fromQueueId = q.Id;
            ProjectBox.Text = q.Project;
            DurationBox.Text = MissionService.FormatShort(TimeSpan.FromMinutes(q.Minutes)).Replace(" ", "");
            Validate();
            HintText.Text = "Da fila · " + HintText.Text;
            foreach (var c in QueueList.Children)
                if (c is Border b) { b.BorderBrush = (Brush)FindResource("Line"); if (b.Tag as string == "loaded") b.Tag = null; }
            card.Tag = "loaded";
            card.BorderBrush = (Brush)FindResource("Teal");
            ProjectBox.Focus();
            ProjectBox.CaretIndex = ProjectBox.Text.Length;
            return;
        }

        // com missão em andamento: confirmar a troca no próprio card
        _rowEditing = true;
        card.Child = QueueSwitchConfirm(q, card);
    }

    private UIElement QueueSwitchConfirm(QueuedMission q, Border card)
    {
        var cur = _svc.Current!;
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = $"Trocar para “{q.Project}”?", Foreground = (Brush)FindResource("Text"),
            FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"A missão atual “{cur.Project}” ({Fmt(_svc.Elapsed.TotalMinutes)} feitos) será encerrada e registrada no histórico.",
            Foreground = (Brush)FindResource("Muted"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
        });
        var giveBack = new CheckBox
        {
            Style = (Style)FindResource("Check"), FontSize = 12, IsChecked = false, Margin = new Thickness(0, 10, 0, 0),
            Content = "Devolver a missão atual para o topo da fila (com o tempo que falta)",
        };
        panel.Children.Add(giveBack);

        var go = new Button { Style = (Style)FindResource("Secondary"), Height = 34, Content = "⇄  Trocar agora", Foreground = (Brush)FindResource("Teal") };
        var cancel = new Button { Style = (Style)FindResource("Secondary"), Height = 34, Content = "Cancelar" };
        var buttons = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(cancel, 2);
        buttons.Children.Add(go); buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        go.Click += (_, _) =>
        {
            _rowEditing = false;
            var curProject = cur.Project;
            int remaining = (int)Math.Ceiling(Math.Max(5, _svc.Remaining.TotalMinutes));
            bool back = giveBack.IsChecked == true;

            SafeClose();
            _svc.End();                                                   // registra a atual no histórico
            if (back) MissionQueue.Add(curProject, remaining, atTop: true);
            MissionQueue.Remove(q.Id, started: true);
            _svc.Start(q.Project, TimeSpan.FromMinutes(q.Minutes));
        };
        cancel.Click += (_, _) => { _rowEditing = false; LoadQueue(); };
        return panel;
    }

    private static string Age(DateTime createdUtc)
    {
        var t = DateTime.UtcNow - createdUtc;
        if (t.TotalMinutes < 1) return "instantes";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes} min";
        if (t.TotalDays < 1) return $"{(int)t.TotalHours} h";
        int d = (int)t.TotalDays;
        return d == 1 ? "1 dia" : $"{d} dias";
    }

    // ---- adição rápida no painel
    private void OnQuickChanged(object sender, TextChangedEventArgs e)
    {
        if (QuickPlaceholder == null || QuickAdd == null) return;
        QuickPlaceholder.Visibility = string.IsNullOrEmpty(QuickProject.Text) ? Visibility.Visible : Visibility.Collapsed;
        QuickAdd.IsEnabled = !string.IsNullOrWhiteSpace(QuickProject.Text) && MissionService.TryParseDuration(QuickMinutes.Text, out _);
    }

    private void OnQuickKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; OnQuickAdd(sender, e); }   // não aciona o botão padrão da janela
    }

    private void OnQuickAdd(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(QuickProject.Text) || !MissionService.TryParseDuration(QuickMinutes.Text, out var d)) return;
        MissionQueue.Add(QuickProject.Text, (int)Math.Round(d.TotalMinutes));
        QuickProject.Text = "";
        QuickProject.Focus();
        QueueScroll.ScrollToBottom();
    }

    // ============================================================ modo: missão atual / próxima missão
    private bool _nextMode;

    private void OnModeCurrent(object sender, RoutedEventArgs e) => SetMode(next: false);
    private void OnModeNext(object sender, RoutedEventArgs e) => SetMode(next: true);

    private void SetMode(bool next)
    {
        if (InterruptPanel.Visibility == Visibility.Visible) ShowInterrupt(false);
        _nextMode = next;
        ModeCurrent.Tag = next ? "" : "selected";
        ModeNext.Tag = next ? "selected" : "";
        MainPanel.Visibility = next ? Visibility.Collapsed : Visibility.Visible;
        NextPanel.Visibility = next ? Visibility.Visible : Visibility.Collapsed;
        StartButton.IsDefault = !next;            // Enter aciona o botão do formulário visível
        NextAddButton.IsDefault = next;
        InterruptConfirm.IsDefault = false;

        if (next)
        {
            HeaderLabel.Text = "PRÓXIMA MISSÃO";
            HeaderTitle.Text = "O que vem depois?";
            ValidateNext();
            Dispatcher.BeginInvoke(() => NextProjectBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }
        else
        {
            HeaderLabel.Text = _editing ? (_svc.IsPaused ? "MISSÃO PAUSADA" : "MISSÃO EM ANDAMENTO") : "NOVA MISSÃO";
            HeaderTitle.Text = _editing ? "Editar missão" : "No que você vai focar agora?";
            Dispatcher.BeginInvoke(() => ProjectBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void OnNextPreset(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.CommandParameter is string min) NextDurationBox.Text = min;
    }

    private void OnNextInputChanged(object sender, TextChangedEventArgs e) => ValidateNext();

    private void ValidateNext()
    {
        if (NextAddButton == null || NextHint == null || _svc == null) return;   // inicializando
        bool okDur = MissionService.TryParseDuration(NextDurationBox.Text, out var dur);
        NextAddButton.IsEnabled = okDur && !string.IsNullOrWhiteSpace(NextProjectBox.Text);
        int n = MissionQueue.Items.Count;
        NextHint.Text = !okDur ? "Ex.: 45 · 90m · 1h30 · 1:15"
            : n == 0 ? "A fila está vazia: esta será a próxima."
            : $"Entra na posição {n + 1} da fila.";
        NextHint.Foreground = (Brush)FindResource("Muted");
        foreach (var c in NextPresetPanel.Children)
            if (c is Button b)
                b.Tag = okDur && b.CommandParameter is string m && int.Parse(m) == (int)Math.Round(dur.TotalMinutes) ? "selected" : "";
    }

    private void OnNextAdd(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NextProjectBox.Text) || !MissionService.TryParseDuration(NextDurationBox.Text, out var dur)) return;
        var name = NextProjectBox.Text.Trim();
        MissionQueue.Add(name, (int)Math.Round(dur.TotalMinutes));
        NextProjectBox.Text = "";
        NextHint.Text = $"✓ “{name}” adicionada à fila ({MissionQueue.Items.Count} no total). Adicione outra ou volte para a missão atual.";
        NextHint.Foreground = (Brush)FindResource("Teal");
        NextProjectBox.Focus();
    }

    private System.Windows.Threading.DispatcherTimer? _filterDebounce;

    private void OnHistoryFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (FilterPlaceholder == null) return;
        FilterPlaceholder.Visibility = string.IsNullOrEmpty(ProjectFilter.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (!_historyOpen) return;

        // espera a digitação dar uma pausa (200 ms) antes de refazer a lista
        if (_filterDebounce == null)
        {
            _filterDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _filterDebounce.Tick += (_, _) => { _filterDebounce.Stop(); LoadHistory(); };
        }
        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    private SessionRecord? CurrentAsRecord()
    {
        var m = _svc.Current;
        if (m == null) return null;
        var worked = _svc.Elapsed;
        double pct = m.Planned.TotalSeconds > 0 ? worked.TotalSeconds / m.Planned.TotalSeconds * 100 : 0;
        var focus = worked > m.InterruptedTotal ? worked - m.InterruptedTotal : TimeSpan.Zero;
        return new SessionRecord(m.Id, m.StartUtc.ToLocalTime(), null, m.Project, m.Planned.TotalMinutes, worked.TotalMinutes,
            _svc.PausedSoFar.TotalMinutes, m.PauseCount, pct, "em_andamento", m.InterruptionCount,
            m.InterruptedTotal.TotalMinutes, focus.TotalMinutes);
    }

    private void LoadHistory()
    {
        foreach (var g in new[] { PeriodChips, StatusChips })
            foreach (var c in g.Children)
                if (c is Button b) b.Tag = (string)b.CommandParameter == (g == PeriodChips ? _period : _statusFilter) ? "selected" : "";

        var all = new List<SessionRecord>();
        if (CurrentAsRecord() is { } cur) all.Add(cur);
        all.AddRange(ActivityLog.ReadSessions().OrderByDescending(r => r.Start));

        var today = DateTime.Today;
        DateTime? from = _period switch { "hoje" => today, "7d" => today.AddDays(-6), "30d" => today.AddDays(-29), _ => null };
        var q = ProjectFilter.Text.Trim();
        var cmp = Pt.CompareInfo;

        var rows = all.Where(r =>
                (from == null || r.Start >= from) &&
                (_statusFilter switch
                {
                    "todas" => true,
                    "interrompida" => r.Status is "interrompida" or "substituida",
                    _ => r.Status == _statusFilter,
                }) &&
                (q.Length == 0 || cmp.IndexOf(r.Project, q, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0))
            .ToList();

        // resumo do filtro (sobre todas as missões filtradas, não só as exibidas)
        var done = rows.Where(r => r.Status != "em_andamento").ToList();
        double focus = rows.Sum(r => r.FocusMin);
        int interr = rows.Sum(r => r.Interruptions);
        string avg = done.Count > 0 ? $"{done.Average(r => Math.Min(r.PctDone, 999)):0}%" : "—";
        HistorySummary.Text = rows.Count == 0
            ? "Sem missões para mostrar."
            : $"{Plural(rows.Count, "missão", "missões")}  ·  {Fmt(focus)} de foco  ·  cumprimento médio {avg}  ·  {Plural(interr, "interrupção", "interrupções")}";

        const int MaxShown = 300;   // limite de cartões na tela; o resumo acima já conta tudo
        int hidden = Math.Max(0, rows.Count - MaxShown);
        if (hidden > 0) { rows = rows.Take(MaxShown).ToList(); HistorySummary.Text += $"\n(mostrando as {MaxShown} mais recentes; refine o filtro para ver as demais)"; }

        // lista agrupada por dia
        _rowEditing = false;
        HistoryList.Children.Clear();
        DateTime? day = null;
        foreach (var r in rows)
        {
            if (day != r.Start.Date)
            {
                day = r.Start.Date;
                HistoryList.Children.Add(new TextBlock
                {
                    Text = DayLabel(r.Start.Date), Foreground = (Brush)FindResource("Muted"), FontSize = 11,
                    FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, HistoryList.Children.Count == 0 ? 0 : 10, 0, 6),
                });
            }
            HistoryList.Children.Add(Row(r));
        }
        HistoryEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryScroll.ScrollToTop();
    }

    private bool _rowEditing;   // editor/confirmação aberto: Enter/Esc ficam com o card, não com a janela

    private UIElement Row(SessionRecord r)
    {
        var card = new Border
        {
            Background = (Brush)FindResource("Surface"), BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 6),
        };
        card.Child = RowView(r, card);
        return card;
    }

    private static (string Color, string Label) StatusStyle(string status) => status switch
    {
        "concluida"       => ("#2EC4B6", "concluída"),
        "encerrada_antes" => ("#F59E0B", "antes da meta"),
        "interrompida"    => ("#8B98A5", "interrompida"),
        "substituida"     => ("#8B98A5", "substituída"),
        "em_andamento"    => ("#5FD9CE", "● em andamento"),
        _                 => ("#8B98A5", status),
    };

    private static Brush HexBrush(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    // ---------------------------------------------------- visualização
    private UIElement RowView(SessionRecord r, Border card)
    {
        var (color, label) = StatusStyle(r.Status);
        var accent = HexBrush(color);
        var muted = (Brush)FindResource("Muted");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var bar = new Border { Width = 3, CornerRadius = new CornerRadius(2), Background = accent, Margin = new Thickness(0, 1, 10, 1) };
        Grid.SetRowSpan(bar, 2);
        grid.Children.Add(bar);

        var parts = new List<string>
        {
            r.End is DateTime end ? $"{r.Start:HH:mm}–{end:HH:mm}" : (r.Status == "em_andamento" ? $"desde {r.Start:HH:mm}" : $"{r.Start:HH:mm}"),
            $"meta {Fmt(r.MetaMin)}",
        };
        if (r.Pauses > 0) parts.Add(Plural(r.Pauses, "pausa", "pausas"));
        if (r.Interruptions > 0) parts.Add($"{Plural(r.Interruptions, "interrupção", "interrupções")} ({Fmt(r.InterruptedMin)})");

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = r.Project, Foreground = (Brush)FindResource("Text"), FontSize = 13.5,
                                          FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        left.Children.Add(new TextBlock { Text = string.Join("  ·  ", parts), Foreground = muted, FontSize = 11.5,
                                          Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(left, 1);
        grid.Children.Add(left);

        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                                     Margin = new Thickness(10, 0, 0, 0) };
        right.Children.Add(new TextBlock { Text = Fmt(r.FocusMin), Foreground = (Brush)FindResource("Text"),
                                           FontFamily = (FontFamily)FindResource("DisplayFont"), FontSize = 15,
                                           FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right,
                                           ToolTip = "Tempo de foco (trabalhado − interrupções)" });
        var statusText = new TextBlock { Text = label, Foreground = accent, FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(statusText);

        // ações (aparecem no lugar do status ao passar o mouse) — só para missões encerradas
        if (r.Status != "em_andamento")
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                                           Visibility = Visibility.Collapsed, Margin = new Thickness(0, -2, -4, -3) };
            var editBtn = new Button { Content = "editar", Style = (Style)FindResource("Link"), FontSize = 11 };
            var delBtn = new Button { Content = "excluir", Style = (Style)FindResource("Link"), FontSize = 11, Foreground = HexBrush("#FF8A8E") };
            editBtn.Click += (_, _) => { _rowEditing = true; card.Child = RowEditor(r, card); };
            delBtn.Click += (_, _) => { _rowEditing = true; card.Child = RowDeleteConfirm(r, card); };
            actions.Children.Add(editBtn);
            actions.Children.Add(delBtn);
            right.Children.Add(actions);

            card.MouseEnter += (_, _) => { if (card.Child == grid) { statusText.Visibility = Visibility.Collapsed; actions.Visibility = Visibility.Visible; } };
            card.MouseLeave += (_, _) => { statusText.Visibility = Visibility.Visible; actions.Visibility = Visibility.Collapsed; };
        }
        else card.ToolTip = "Missão em andamento: edite pelo painel à esquerda.";

        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        double pct = Math.Clamp(r.PctDone, 0, 100);
        var track = new Grid { Height = 3, Margin = new Thickness(0, 8, 0, 0) };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(pct, 0.001), GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(100 - pct, 0.001), GridUnitType.Star) });
        var fill = new Border { Background = accent, CornerRadius = new CornerRadius(1.5) };
        var rest = new Border { Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(1.5) };
        Grid.SetColumn(rest, 1);
        track.Children.Add(fill); track.Children.Add(rest);
        track.ToolTip = $"Cumprimento da meta: {r.PctDone:0}%";
        Grid.SetRow(track, 1); Grid.SetColumn(track, 1); Grid.SetColumnSpan(track, 2);
        grid.Children.Add(track);

        return grid;
    }

    // ---------------------------------------------------- edição inline
    private UIElement RowEditor(SessionRecord r, Border card)
    {
        var muted = (Brush)FindResource("Muted");
        TextBlock Lbl(string t) => new() { Text = t, Foreground = muted, FontSize = 11.5, Margin = new Thickness(0, 8, 0, 4) };
        TextBox Box(string t) => new() { Text = t, Style = (Style)FindResource("FieldSm") };

        var project = Box(r.Project);
        project.MaxLength = 60;
        var start = Box(r.Start.ToString("dd/MM/yyyy HH:mm", Pt));
        var end = Box(r.End is DateTime endAt ? endAt.ToString("dd/MM/yyyy HH:mm", Pt) : "");
        var meta = Box(MissionService.FormatShort(TimeSpan.FromMinutes(Math.Round(r.MetaMin))).Replace(" ", ""));
        var preview = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "EDITAR MISSÃO", Foreground = (Brush)FindResource("Teal"), FontSize = 10.5, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(Lbl("Projeto"));
        panel.Children.Add(project);

        var times = new Grid();
        times.ColumnDefinitions.Add(new ColumnDefinition());
        times.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        times.ColumnDefinitions.Add(new ColumnDefinition());
        var c1 = new StackPanel(); c1.Children.Add(Lbl("Início")); c1.Children.Add(start);
        var c2 = new StackPanel(); c2.Children.Add(Lbl("Término")); c2.Children.Add(end);
        Grid.SetColumn(c2, 2);
        times.Children.Add(c1); times.Children.Add(c2);
        panel.Children.Add(times);

        panel.Children.Add(Lbl("Meta"));
        panel.Children.Add(meta);

        // ---------- interrupções (adicionar ao item do histórico)
        Action recalc = () => { };   // ligado ao Recalc() mais abaixo
        var amber = HexBrush("#FCD34D");
        var pending = new List<(string Cat, string Reason, double Min, bool Add)>();
        double PendingMin() => pending.Sum(x => x.Min);

        var intHeader = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        var intSummary = new TextBlock { Foreground = muted, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        var intAdd = new Button { Content = "+ adicionar interrupção", Style = (Style)FindResource("Link"), FontSize = 11.5,
                                  Foreground = amber, HorizontalAlignment = HorizontalAlignment.Right };
        intHeader.Children.Add(intSummary);
        intHeader.Children.Add(intAdd);
        panel.Children.Add(intHeader);

        var pendingList = new StackPanel();
        panel.Children.Add(pendingList);

        var form = new StackPanel();
        var formBorder = new Border
        {
            Background = HexBrush("#0F1820"), BorderBrush = HexBrush("#5C4210"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed, Child = form,
        };
        panel.Children.Add(formBorder);

        Button? selChip = null;
        InterruptionPreset? selPreset = null;
        var chips = new WrapPanel();
        var reasonPanel = new StackPanel { Visibility = Visibility.Collapsed };
        var reason = Box("");
        reasonPanel.Children.Add(Lbl("O que aconteceu?"));
        reasonPanel.Children.Add(reason);
        var dur = Box("30");
        var addToGoal = new CheckBox { Style = (Style)FindResource("Check"), Content = "Acrescentar à meta", FontSize = 12,
                                       IsChecked = false, Margin = new Thickness(0, 10, 0, 0) };
        var formMsg = new TextBlock { Foreground = muted, FontSize = 11, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        var ok = new Button { Style = (Style)FindResource("Secondary"), Height = 30, Content = "Adicionar", Foreground = amber };
        var cancelForm = new Button { Style = (Style)FindResource("Secondary"), Height = 30, Content = "Cancelar" };
        var formButtons = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        formButtons.ColumnDefinitions.Add(new ColumnDefinition());
        formButtons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        formButtons.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(cancelForm, 2);
        formButtons.Children.Add(ok); formButtons.Children.Add(cancelForm);

        form.Children.Add(new TextBlock { Text = "O que tirou do foco?", Foreground = muted, FontSize = 11.5, Margin = new Thickness(0, 0, 0, 6) });
        form.Children.Add(chips);
        form.Children.Add(reasonPanel);
        form.Children.Add(Lbl("Tempo da interrupção"));
        form.Children.Add(dur);
        form.Children.Add(addToGoal);
        form.Children.Add(formMsg);
        form.Children.Add(formButtons);

        void AddChip(string label, InterruptionPreset? preset)
        {
            var c = new Button { Content = label, Style = (Style)FindResource("Chip"), Tag = "", FontSize = 11.5,
                                 Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 6, 6) };
            c.Click += (_, _) =>
            {
                foreach (var x in chips.Children) if (x is Button o) o.Tag = "";
                c.Tag = "selected";
                selChip = c;
                selPreset = preset;
                reasonPanel.Visibility = preset == null ? Visibility.Visible : Visibility.Collapsed;
                dur.Text = MissionService.FormatShort(TimeSpan.FromMinutes(preset?.Minutes ?? 30)).Replace(" ", "");
                if (preset == null) Dispatcher.BeginInvoke(() => reason.Focus(), System.Windows.Threading.DispatcherPriority.Input);
                ValidateForm();
            };
            chips.Children.Add(c);
        }
        foreach (var ip in _settings.Interruptions) AddChip($"{ip.Label} · {Fmt(ip.Minutes)}", ip);
        AddChip("Outro motivo…", null);

        void ValidateForm()
        {
            bool okDur = MissionService.TryParseDuration(dur.Text, out var d);
            bool custom = selChip != null && selPreset == null;
            bool okReason = !custom || !string.IsNullOrWhiteSpace(reason.Text);
            ok.IsEnabled = selChip != null && okDur && okReason;
            formMsg.Text = selChip == null ? "Escolha o motivo."
                : !okDur ? "Tempo inválido. Ex.: 15 · 30 · 1h"
                : !okReason ? "Descreva o que aconteceu."
                : addToGoal.IsChecked == true ? $"A meta aumenta {Fmt(d.TotalMinutes)}." : "A meta não muda.";
            formMsg.Foreground = ok.IsEnabled ? muted : amber;
        }

        void OpenForm(bool open)
        {
            formBorder.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            intAdd.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
            if (open)
            {
                foreach (var x in chips.Children) if (x is Button o) o.Tag = "";
                selChip = null; selPreset = null;
                reason.Text = ""; reasonPanel.Visibility = Visibility.Collapsed;
                dur.Text = "30"; addToGoal.IsChecked = false;
                ValidateForm();
            }
        }

        void ShiftMeta(double minutes)
        {
            if (MissionService.TryParseDuration(meta.Text, out var cur))
            {
                var nv = TimeSpan.FromMinutes(Math.Max(1, cur.TotalMinutes + minutes));
                meta.Text = MissionService.FormatShort(nv).Replace(" ", "");
            }
        }

        void RefreshPending()
        {
            pendingList.Children.Clear();
            for (int i = 0; i < pending.Count; i++)
            {
                var item = pending[i];
                var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
                row.Children.Add(new TextBlock
                {
                    Text = $"⚡ {item.Reason} · {Fmt(item.Min)}{(item.Add ? " · +meta" : "")}",
                    Foreground = amber, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 0, 60, 0), VerticalAlignment = VerticalAlignment.Center,
                });
                var rm = new Button { Content = "remover", Style = (Style)FindResource("Link"), FontSize = 11,
                                      Foreground = HexBrush("#FF8A8E"), HorizontalAlignment = HorizontalAlignment.Right };
                rm.Click += (_, _) =>
                {
                    if (item.Add) ShiftMeta(-item.Min);
                    pending.Remove(item);
                    RefreshPending();
                    recalc();
                };
                row.Children.Add(rm);
                pendingList.Children.Add(row);
            }
            int count = r.Interruptions + pending.Count;
            double total = r.InterruptedMin + PendingMin();
            intSummary.Text = count == 0 ? "Sem interrupções" : $"Interrupções: {count} ({Fmt(total)})" + (pending.Count > 0 ? $" · {pending.Count} nova(s)" : "");
        }

        void ConfirmForm()
        {
            if (!ok.IsEnabled || !MissionService.TryParseDuration(dur.Text, out var d)) return;
            bool custom = selPreset == null;
            bool add = addToGoal.IsChecked == true;
            pending.Add((custom ? "outro" : selPreset!.Id, custom ? reason.Text.Trim() : selPreset!.Label, d.TotalMinutes, add));
            if (add) ShiftMeta(d.TotalMinutes);
            OpenForm(false);
            RefreshPending();
            recalc();
        }

        intAdd.Click += (_, _) => OpenForm(true);
        cancelForm.Click += (_, _) => OpenForm(false);
        ok.Click += (_, _) => ConfirmForm();
        addToGoal.Checked += (_, _) => ValidateForm();
        addToGoal.Unchecked += (_, _) => ValidateForm();
        foreach (var tb in new[] { reason, dur })
        {
            tb.TextChanged += (_, _) => ValidateForm();
            tb.PreviewKeyDown += (_, ev) =>
            {
                if (ev.Key == Key.Enter) { ev.Handled = true; ConfirmForm(); }
                else if (ev.Key == Key.Escape) { ev.Handled = true; OpenForm(false); }
            };
        }
        panel.Children.Add(preview);

        var save = new Button { Style = (Style)FindResource("Secondary"), Height = 34, Content = "✓  Salvar", Foreground = (Brush)FindResource("Teal") };
        var cancel = new Button { Style = (Style)FindResource("Secondary"), Height = 34, Content = "Cancelar" };
        var buttons = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(cancel, 2);
        buttons.Children.Add(save); buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        SessionRecord? candidate = null;
        void Recalc()
        {
            candidate = null;
            string? err = null;
            if (string.IsNullOrWhiteSpace(project.Text)) err = "Informe o projeto.";
            else if (!TryParseWhen(start.Text, r.Start, out var s)) err = "Início inválido. Ex.: 07/11/2026 12:30 ou 12:30";
            else if (!TryParseWhen(end.Text, s, out var f, rollPastMidnight: true)) err = "Término inválido. Ex.: 07/11/2026 13:45 ou 13:45";
            else if (f <= s) err = "O término precisa ser depois do início.";
            else if ((f - s).TotalHours > 24) err = "Uma missão não pode passar de 24 h.";
            else if (!MissionService.TryParseDuration(meta.Text, out var m)) err = "Meta inválida. Ex.: 50 · 1h30 · 2h";
            else
            {
                double worked = (f - s).TotalMinutes - r.PausedMin;
                double interrupted = r.InterruptedMin + PendingMin();
                if (worked < 0) err = $"O intervalo é menor que as pausas já registradas ({Fmt(r.PausedMin)}).";
                else if (interrupted > worked + 0.01) err = $"As interrupções ({Fmt(interrupted)}) passam do tempo trabalhado ({Fmt(worked)}).";
                else
                {
                    double focus = Math.Max(0, worked - interrupted);
                    double pct = m.TotalMinutes > 0 ? worked / m.TotalMinutes * 100 : 0;
                    string status = r.Status is "concluida" or "encerrada_antes"
                        ? (worked >= m.TotalMinutes ? "concluida" : "encerrada_antes") : r.Status;
                    candidate = r with { Project = project.Text.Trim(), Start = s, End = f, MetaMin = m.TotalMinutes,
                                         WorkedMin = worked, PctDone = pct, Status = status, FocusMin = focus,
                                         Interruptions = r.Interruptions + pending.Count, InterruptedMin = interrupted };
                    var extras = r.PausedMin > 0 ? $" (descontadas pausas de {Fmt(r.PausedMin)})" : "";
                    preview.Text = $"Trabalhado {Fmt(worked)}{extras}  ·  foco {Fmt(focus)}  ·  {pct:0}%  ·  {StatusStyle(status).Label}";
                    preview.Foreground = (Brush)FindResource("Teal");
                }
            }
            if (err != null) { preview.Text = "⚠ " + err; preview.Foreground = HexBrush("#FCD34D"); }
            save.IsEnabled = candidate != null;
        }

        recalc = Recalc;

        void Finish(bool reload)
        {
            _rowEditing = false;
            if (reload) LoadHistory(); else card.Child = RowView(r, card);
        }

        void Save()
        {
            if (candidate == null) return;
            if (ActivityLog.UpdateSession(r, candidate))
            {
                foreach (var x in pending)
                    ActivityLog.Event("interrupcao", null,
                        ("missao", r.Id), ("projeto", candidate.Project), ("categoria", x.Cat), ("motivo", x.Reason),
                        ("duracao_min", Math.Round(x.Min, 1)), ("acrescentou_meta", x.Add), ("origem", "historico"));
                Finish(reload: true);
            }
            else { preview.Text = "⚠ Não foi possível salvar (veja erros.log)."; preview.Foreground = HexBrush("#FCD34D"); }
        }

        foreach (var tb in new[] { project, start, end, meta })
        {
            tb.TextChanged += (_, _) => Recalc();
            tb.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; Save(); }
                else if (e.Key == Key.Escape) { e.Handled = true; Finish(reload: false); }
            };
        }
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => Finish(reload: false);

        RefreshPending();
        Recalc();
        Dispatcher.BeginInvoke(() => { project.Focus(); project.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
        return panel;
    }

    /// <summary>Aceita "dd/MM/yyyy HH:mm", "dd/MM HH:mm" ou só "HH:mm" (usa a data de referência).</summary>
    private static bool TryParseWhen(string text, DateTime reference, out DateTime result, bool rollPastMidnight = false)
    {
        text = text.Trim();
        if (DateTime.TryParseExact(text, new[] { "dd/MM/yyyy HH:mm", "d/M/yyyy HH:mm", "dd/MM/yyyy H:mm", "d/M/yyyy H:mm" },
                                   Pt, DateTimeStyles.None, out result)) return true;
        if (DateTime.TryParseExact(text, new[] { "dd/MM HH:mm", "d/M HH:mm", "dd/MM H:mm", "d/M H:mm" }, Pt, DateTimeStyles.None, out var dm))
        {
            result = new DateTime(reference.Year, dm.Month, dm.Day, dm.Hour, dm.Minute, 0);
            return true;
        }
        if (DateTime.TryParseExact(text, new[] { "HH:mm", "H:mm" }, Pt, DateTimeStyles.None, out var hm))
        {
            result = reference.Date.Add(hm.TimeOfDay);
            if (rollPastMidnight && result <= reference) result = result.AddDays(1);   // término depois da meia-noite
            return true;
        }
        result = default;
        return false;
    }

    // ---------------------------------------------------- exclusão
    private UIElement RowDeleteConfirm(SessionRecord r, Border card)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = $"Excluir “{r.Project}” ({r.Start:dd/MM HH:mm}) do histórico?",
            Foreground = (Brush)FindResource("Text"), FontSize = 13, TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Uma cópia do histórico anterior fica em sessoes.bak.csv.",
            Foreground = (Brush)FindResource("Muted"), FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0),
        });

        var del = new Button { Style = (Style)FindResource("Danger"), Height = 34, Content = "Excluir" };
        var cancel = new Button { Style = (Style)FindResource("Secondary"), Height = 34, Content = "Cancelar" };
        var buttons = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(cancel, 2);
        buttons.Children.Add(del); buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        del.Click += (_, _) =>
        {
            _rowEditing = false;
            if (ActivityLog.DeleteSession(r)) LoadHistory();
            else card.Child = RowView(r, card);
        };
        cancel.Click += (_, _) => { _rowEditing = false; card.Child = RowView(r, card); };
        return panel;
    }

    private static string DayLabel(DateTime d)
    {
        var today = DateTime.Today;
        if (d == today) return "HOJE";
        if (d == today.AddDays(-1)) return "ONTEM";
        return d.ToString("ddd, dd/MM", Pt).ToUpperInvariant();
    }

    private static string Fmt(double minutes) => MissionService.FormatShort(TimeSpan.FromMinutes(Math.Round(minutes)));
    private static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } }
    }
}
