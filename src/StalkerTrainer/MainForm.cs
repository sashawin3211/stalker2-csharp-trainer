using System.Diagnostics;
using StalkerTrainer.Core;
using StalkerTrainer.Native;

namespace StalkerTrainer;

public sealed class MainForm : Form
{
    private static readonly Color Bg = Color.FromArgb(22, 25, 24);
    private static readonly Color Surface = Color.FromArgb(34, 39, 36);
    private static readonly Color Accent = Color.FromArgb(224, 175, 75);
    private static readonly Color Ink = Color.FromArgb(228, 231, 221);
    private readonly bool _interactive;
    private readonly TextBox _path = new() { Text = InstallationInfo.DefaultRoot, Width = 765 };
    private readonly Label _status = new() { AutoSize = true };
    private readonly Label _scanStatus = new() { AutoSize = true, Text = "Начни с количества купонов: тип Int32." };
    private readonly ComboBox _type = Combo(["Int32 — деньги / патроны", "Float32 — дробные значения", "Int64 — целое 64 бит", "Float64 — дробное 64 бит"], 230);
    private readonly ComboBox _filter = Combo(["Равно числу", "Изменилось", "Не изменилось", "Увеличилось", "Уменьшилось"], 150);
    private readonly TextBox _searchValue = new() { Width = 140, PlaceholderText = "Купоны в игре" };
    private readonly TextBox _newValue = new() { Width = 140, Text = "100000" };
    private readonly TextBox _name = new() { Width = 155, Text = "Деньги" };
    private readonly NumericUpDown _moneyAmount = new() { Minimum = 0, Maximum = 1_000_000, Value = 100_000, ThousandsSeparator = true, Width = 145 };
    private readonly Label _moneyLabel = new() { Text = "Купоны: подключись к игре", Width = 310, Height = 32, ForeColor = Accent, Margin = new Padding(5, 6, 8, 0) };
    private readonly DataGridView _results = Grid();
    private readonly DataGridView _watches = Grid();
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    private readonly List<WatchEntry> _entries = [];
    private readonly List<int> _hotkeys = [];
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private readonly Button _attach, _first, _next, _write, _pin, _cancel, _toggle, _remove, _setMoney;
    private ProcessMemory? _memory;
    private KnownMoneyFeature? _knownMoney;
    private TrainerFeatures? _features;
    private string? _featureError;
    private readonly Dictionary<FeatureId, Button> _featureButtons = [];
    private readonly Label _featureStatus = new() { AutoSize = true, Text = "Подключись к игре после загрузки сохранения.", ForeColor = Accent };
    private List<Candidate> _candidates = [];
    private ValueKind _scanType;
    private CancellationTokenSource? _scanCancellation;
    private bool _busy, _closing, _disposed;

    public MainForm(bool interactive = true)
    {
        _interactive = interactive;
        Text = "S.T.A.L.K.E.R. 2 • C# Memory Trainer";
        Font = new Font("Segoe UI", 10);
        BackColor = Bg;
        ForeColor = Ink;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1040, 930);
        MinimumSize = new Size(1056, 969);
        StartPosition = FormStartPosition.CenterScreen;

        var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 11, Padding = new Padding(22, 15, 22, 15) };
        foreach (var height in new[] { 0, 0, 0, 102, 30 }) page.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 65));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 85));
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(22, 15, 22, 15) };
        foreach (var height in new[] { 70, 75, 75 }) shell.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(shell);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var ready = new TabPage("Готовые функции") { BackColor = Bg, Padding = new Padding(16) };
        var advanced = new TabPage("Ручной поиск адресов") { BackColor = Bg };
        tabs.TabPages.AddRange([ready, advanced]);
        advanced.Controls.Add(page);
        shell.Controls.Add(tabs, 0, 3);
        ready.Controls.Add(CreateFeaturePanel());

        var header = new Panel { Dock = DockStyle.Fill };
        header.Controls.Add(new Label { Text = "S.T.A.L.K.E.R. 2", Font = new Font("Segoe UI", 24, FontStyle.Bold), ForeColor = Accent, AutoSize = true });
        header.Controls.Add(new Label { Text = "C# MEMORY TRAINER   /   STEAM   /   ПОИСК · ИЗМЕНЕНИЕ · УДЕРЖАНИЕ", AutoSize = true, ForeColor = Color.Silver, Location = new Point(3, 46) });
        shell.Controls.Add(header, 0, 0);

        var installation = Flow();
        _attach = Button("Подключиться", Attach, 190);
        installation.Controls.AddRange([_path, _attach]);
        installation.SetFlowBreak(_attach, true);
        _status.Margin = new Padding(3, 8, 0, 0);
        installation.Controls.Add(_status);
        shell.Controls.Add(installation, 0, 1);

        var moneyPanel = Flow();
        moneyPanel.BackColor = Surface;
        moneyPanel.Padding = new Padding(7);
        moneyPanel.Margin = new Padding(0, 0, 0, 10);
        _setMoney = Button("Установить сумму · Ctrl+Alt+M", SetMoney, 310);
        moneyPanel.Controls.AddRange([_moneyLabel, _moneyAmount, _setMoney]);
        shell.Controls.Add(moneyPanel, 0, 2);

        var scan = Flow();
        scan.BackColor = Surface;
        scan.Padding = new Padding(7);
        var tip = new Label { Text = "1. Введи число из игры → Найти.   2. Измени его в игре → введи новое → Отсеять.", Width = 950, Height = 24, ForeColor = Accent };
        scan.Controls.Add(tip);
        scan.SetFlowBreak(tip, true);
        _first = Button("Найти заново", () => _ = ScanAsync(true), 145);
        _next = Button("Отсеять", () => _ = ScanAsync(false), 125);
        _cancel = Button("Стоп", () => _scanCancellation?.Cancel(), 75);
        scan.Controls.AddRange([_type, _searchValue, _filter, _first, _next, _cancel]);
        page.Controls.Add(scan, 0, 3);
        page.Controls.Add(_scanStatus, 0, 4);

        _results.Columns.Add("address", "Адрес");
        _results.Columns.Add("value", "Значение при поиске");
        _results.Columns.Add("kind", "Тип");
        _results.SelectionChanged += (_, _) => UpdateButtons();
        page.Controls.Add(_results, 0, 5);

        var edit = Flow();
        edit.Padding = new Padding(0, 12, 0, 0);
        _write = Button("Записать разово", WriteSelected, 170);
        _pin = Button("Закрепить адрес", PinSelected, 175);
        edit.Controls.AddRange([Label("Новое число:"), _newValue, _write, Label("Название:"), _name, _pin]);
        page.Controls.Add(edit, 0, 6);
        page.Controls.Add(new Label { Text = "ЗАКРЕПЛЁННЫЕ АДРЕСА  •  Ctrl+Alt+F1…F4 — удержание строки  •  Ctrl+Alt+End — снять всё", AutoSize = true, ForeColor = Accent }, 0, 7);

        foreach (var (id, title) in new[] { ("key", "Клавиша"), ("name", "Название"), ("address", "Адрес"), ("current", "Сейчас"), ("desired", "Удерживать"), ("state", "Состояние") })
            _watches.Columns.Add(id, title);
        _watches.SelectionChanged += (_, _) => UpdateButtons();
        page.Controls.Add(_watches, 0, 8);

        var watchActions = Flow();
        _toggle = Button("Удержание ВКЛ / ВЫКЛ", ToggleSelected, 225);
        _remove = Button("Удалить строку", RemoveSelected, 170);
        watchActions.Controls.AddRange([_toggle, _remove, Button("Снять всё", ReleaseAll, 140), Button("Инструкция", OpenHelp, 150)]);
        page.Controls.Add(watchActions, 0, 9);
        _log.Font = new Font("Consolas", 9);
        page.Controls.Add(_log, 0, 10);
        StyleInputs(shell);
        UpdateStatus();
        UpdateButtons();
        Log("Адреса ищутся в текущем запуске игры. После загрузки сохранения сними удержание и проверь адрес заново.");
        if (_interactive) { _timer.Interval = 100; _timer.Tick += (_, _) => TickWatches(); _timer.Start(); }
    }

    private Control CreateFeaturePanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 8 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (var i = 0; i < 5; i++) panel.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        for (int i = 0; i < TrainerFeatures.Descriptions.Length; i++)
        {
            var feature = TrainerFeatures.Descriptions[i];
            var card = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8), Margin = new Padding(5), BackColor = Surface };
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var button = Button(feature.Name + "   ·   ВЫКЛ", () => ToggleFeature(feature.Id), 430);
            button.Dock = DockStyle.Fill;
            button.Height = 36;
            _featureButtons.Add(feature.Id, button);
            card.Controls.Add(button, 0, 0);
            card.Controls.Add(new Label { Text = feature.Detail, Dock = DockStyle.Fill, ForeColor = Color.Silver, Font = new Font("Segoe UI", 9), Padding = new Padding(4, 4, 0, 0) }, 0, 1);
            panel.Controls.Add(card, i % 2, i / 2);
        }
        _featureStatus.Margin = new Padding(6, 10, 0, 0);
        panel.Controls.Add(_featureStatus, 0, 5); panel.SetColumnSpan(_featureStatus, 2);
        var actions = Flow();
        actions.Controls.AddRange([Button("Выключить всё · Ctrl+Alt+End", ReleaseAll, 320), Button("Инструкция", OpenHelp, 155)]);
        panel.Controls.Add(actions, 0, 6); panel.SetColumnSpan(actions, 2);
        var note = new Label { Text = "Оставь трейнер открытым. После загрузки сохранения подключись заново.\nГотовые функции рассчитаны на проверенную сборку EXE; при выходе флаги и модификаторы восстанавливаются.", Dock = DockStyle.Fill, ForeColor = Color.Silver, Font = new Font("Segoe UI", 9), Padding = new Padding(6, 6, 0, 0) };
        panel.Controls.Add(note, 0, 7); panel.SetColumnSpan(note, 2);
        return panel;
    }

    private void ToggleFeature(FeatureId id) => Guard(() =>
    {
        if (_features is null || _busy) return;
        _featureError = null;
        try { _features.Set(id, !_features.IsEnabled(id)); }
        finally { RefreshFeatures(); }
        Log($"{TrainerFeatures.Descriptions.First(d => d.Id == id).Name}: {(_features.IsEnabled(id) ? "ВКЛ" : "ВЫКЛ")}");
    });

    private void RefreshFeatures()
    {
        foreach (var item in TrainerFeatures.Descriptions)
        {
            var button = _featureButtons[item.Id];
            bool enabled = _features?.IsEnabled(item.Id) == true;
            button.Text = item.Name + (enabled ? "   ·   ВКЛ" : "   ·   ВЫКЛ");
            button.ForeColor = enabled ? Color.Black : Ink;
            button.BackColor = enabled ? Accent : Surface;
            button.Enabled = _features is not null && !_busy && _memory?.IsAlive == true;
        }
        if (_features is not null) _featureStatus.Text = _featureError ?? _features.Status;
    }

    private ValueKind SelectedKind => (ValueKind)_type.SelectedIndex;
    protected override bool ShowWithoutActivation => !_interactive;

    private void Attach()
    {
        if (_busy) return;
        Guard(() =>
        {
            _featureError = null;
            using var game = InstallationInfo.FindRunning(_path.Text.Trim());
            if (game is null) throw new InvalidOperationException("Запусти игру из указанной папки и загрузи сохранение. Если она уже запущена, Windows может не давать прочитать путь её процесса.");
            var nextMemory = new ProcessMemory(game.Id);
            KnownMoneyFeature? knownMoney;
            try { knownMoney = KnownMoneyFeature.TryCreate(game, nextMemory); }
            catch { nextMemory.Dispose(); throw; }
            try { _features?.StopAll(); }
            catch (Exception ex) { Log("Предыдущее подключение: " + ex.Message); }
            _features = null;
            _memory?.Dispose();
            _memory = nextMemory;
            _knownMoney = knownMoney;
            if (knownMoney is not null)
            {
                try { _features = new TrainerFeatures(nextMemory, (ulong)game.MainModule!.BaseAddress); }
                catch (Exception ex) { _featureStatus.Text = ex.Message; Log(ex.Message); }
            }
            else _featureStatus.Text = "Версия EXE не совпадает с профилем готовых функций.";
            _candidates = [];
            _entries.Clear();
            ShowCandidates();
            ShowWatches();
            UpdateStatus();
            UpdateButtons();
            RefreshMoney();
            Log($"Подключено к {InstallationInfo.ProcessName}, PID {game.Id}. " +
                (_knownMoney is not null ? "Профиль денег совпадает с игровым файлом. Можно установить сумму кнопкой." : "Файл игры отличается: готовая функция денег выключена. Доступен ручной поиск."));
        });
    }

    private void SetMoney() => Guard(() =>
    {
        if (_busy || _knownMoney is null) return;
        foreach (var entry in _entries.Where(e => e.Address == _knownMoney.Address)) entry.Frozen = false;
        var before = _knownMoney.Read();
        var after = _knownMoney.Set((int)_moneyAmount.Value);
        RefreshMoney();
        Log($"Купоны: {before:N0} → {after:N0}. Если интерфейс игры не обновился, переоткрой инвентарь.");
    });

    private void RefreshMoney()
    {
        if (_knownMoney is null) { _moneyLabel.Text = _memory is null ? "Купоны: подключись к игре" : "Купоны: версия EXE не поддерживается"; return; }
        try { _moneyLabel.Text = $"КУПОНЫ: {_knownMoney.Read():N0}"; }
        catch { _moneyLabel.Text = "Купоны: значение пока недоступно"; }
    }

    private async Task ScanAsync(bool first)
    {
        if (_busy || _memory is null) return;
        _busy = true;
        ReleaseAll();
        _scanCancellation = new CancellationTokenSource();
        UpdateButtons();
        try
        {
            var filter = first ? FilterKind.Equals : (FilterKind)_filter.SelectedIndex;
            var kind = first ? SelectedKind : _scanType;
            MemoryValue? value = filter == FilterKind.Equals ? MemoryValue.Parse(_searchValue.Text, kind) : null;
            var progress = new Progress<ScanProgress>(p =>
            {
                if (!_closing) _scanStatus.Text = $"Прочитано {p.BytesRead / 1048576.0:N0} МБ  •  совпадений: {p.Matches:N0}  •  этап: {p.RegionsDone}/{p.RegionsTotal}";
            });
            var scanner = new MemoryScanner(_memory);
            var previous = _candidates;
            var token = _scanCancellation.Token;
            var result = await Task.Run(() => first ? scanner.FirstScan(value!.Value, progress, token) : scanner.Refine(previous, filter, value, progress, token), token);
            if (_closing) return;
            _candidates = result.Candidates;
            _scanType = kind;
            ShowCandidates();
            _scanStatus.Text = $"Найдено: {_candidates.Count:N0}. Показаны первые {Math.Min(300, _candidates.Count)}. " +
                (_candidates.Count > 1 ? "Измени число в игре и повтори отсев." : _candidates.Count == 1 ? "Остался один адрес; проверь его небольшим изменением." : "Совпадений нет — проверь число / тип и начни заново.");
            Log($"Поиск завершён: {_candidates.Count:N0} адресов. Непрочитано: {result.SkippedBytes:N0} байт.");
        }
        catch (OperationCanceledException) { Log("Поиск остановлен. Предыдущие результаты сохранены."); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Log("Ошибка поиска: " + ex.Message); }
        finally
        {
            _scanCancellation?.Dispose();
            _scanCancellation = null;
            _busy = false;
            if (!_closing) { UpdateButtons(); UpdateStatus(); }
            else { _memory?.Dispose(); _memory = null; }
        }
    }

    private Candidate? SelectedCandidate => _results.SelectedRows.Count > 0 && _results.SelectedRows[0].Tag is Candidate item ? item : null;
    private int SelectedWatch => _watches.SelectedRows.Count > 0 ? _watches.SelectedRows[0].Index : -1;

    private void WriteSelected() => Guard(() =>
    {
        if (_busy || _memory is null || SelectedCandidate is not { } item) return;
        var desired = MemoryValue.Parse(_newValue.Text, item.Value.Kind);
        _memory.WriteValue(item.Address, desired, item.Value);
        var observed = _memory.ReadValue(item.Address, desired.Kind);
        var index = _candidates.FindIndex(c => c.Address == item.Address);
        if (index >= 0) _candidates[index] = new(item.Address, observed);
        ShowCandidates(item.Address);
        Log($"0x{item.Address:X}: записано {desired}, прочитано {observed}. Проверь изменение в игре.");
    });

    private void PinSelected() => Guard(() =>
    {
        if (_busy || _memory is null || SelectedCandidate is not { } item) return;
        if (_entries.Count >= 4) throw new InvalidOperationException("Доступны четыре закреплённых адреса. Удали ненужную строку.");
        if (_entries.Any(e => e.Address == item.Address)) throw new InvalidOperationException("Этот адрес уже закреплён.");
        var desired = MemoryValue.Parse(_newValue.Text, item.Value.Kind);
        var current = _memory.ReadValue(item.Address, item.Value.Kind);
        var name = string.IsNullOrWhiteSpace(_name.Text) ? "Значение" : _name.Text.Trim();
        _entries.Add(new WatchEntry { Name = name, Address = item.Address, Desired = desired, Current = current });
        ShowWatches();
        Log($"Закреплено «{name}»: 0x{item.Address:X}. Удержание пока выключено.");
    });

    private void ToggleSelected() => Guard(() => ToggleWatch(SelectedWatch));

    private void ToggleWatch(int index)
    {
        if (_busy || _memory is null || index < 0 || index >= _entries.Count) return;
        var item = _entries[index];
        if (!item.Frozen)
        {
            var current = _memory.ReadValue(item.Address, item.Desired.Kind);
            _memory.WriteValue(item.Address, item.Desired, current);
        }
        item.Frozen = !item.Frozen;
        item.Error = "";
        ShowWatches(index);
        Log($"«{item.Name}»: удержание {(item.Frozen ? "включено" : "выключено")}.");
    }

    private void RemoveSelected()
    {
        var index = SelectedWatch;
        if (_busy || index < 0 || index >= _entries.Count) return;
        _entries.RemoveAt(index);
        ShowWatches();
    }

    private void ReleaseAll()
    {
        foreach (var item in _entries) item.Frozen = false;
        try { _features?.StopAll(); _featureError = null; }
        catch (Exception ex) { _featureError = "Остановка: " + ex.Message; _featureStatus.Text = _featureError; Log(_featureError); }
        RefreshFeatures();
        ShowWatches(SelectedWatch);
    }

    private void TickWatches()
    {
        if (_closing || _busy || _memory is null) return;
        if (!_memory.IsAlive)
        {
            foreach (var entry in _entries) { entry.Frozen = false; entry.Error = "Процесс завершён"; }
            _memory.Dispose(); _memory = null;
            _knownMoney = null;
            _features = null;
            _featureStatus.Text = "Игра завершилась. Подключись после нового запуска.";
            RefreshFeatures();
            RefreshMoney();
            _candidates.Clear(); ShowCandidates(); ShowWatches(); UpdateButtons(); UpdateStatus();
            Log("Игра завершилась. Удержание остановлено; при следующем запуске нужен новый поиск.");
            return;
        }
        RefreshMoney();
        if (_features is not null)
        {
            try { _features.Tick(); }
            catch (Exception ex)
            {
                try { _features.StopAll(); }
                catch (Exception restore) { Log("Восстановление параметров: " + restore.Message); }
                _features = null;
                _featureStatus.Text = ex.Message;
                Log("Готовые функции остановлены: " + ex.Message);
            }
            RefreshFeatures();
        }
        foreach (var entry in _entries)
        {
            try
            {
                if (entry.Frozen) _memory.WriteValue(entry.Address, entry.Desired);
                entry.Current = _memory.ReadValue(entry.Address, entry.Desired.Kind);
                entry.Error = "";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                entry.Frozen = false;
                if (entry.Error.Length == 0) Log($"«{entry.Name}»: {ex.Message} Удержание остановлено.");
                entry.Error = "Адрес недоступен";
            }
        }
        UpdateWatchCells();
    }

    private void ShowCandidates(ulong? selected = null)
    {
        _results.Rows.Clear();
        foreach (var item in _candidates.Take(300))
        {
            var row = _results.Rows[_results.Rows.Add($"0x{item.Address:X16}", item.Value.ToString(), item.Value.Kind.ToString())];
            row.Tag = item;
            if (selected == item.Address) _results.CurrentCell = row.Cells[0];
        }
        UpdateButtons();
    }

    private void ShowWatches(int selection = -1)
    {
        _watches.Rows.Clear();
        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            _watches.Rows.Add($"Ctrl+Alt+F{i + 1}", entry.Name, $"0x{entry.Address:X}", entry.Current?.ToString() ?? "?", entry.Desired.ToString(), "");
        }
        if (selection >= 0 && selection < _watches.Rows.Count) _watches.CurrentCell = _watches.Rows[selection].Cells[0];
        UpdateWatchCells();
        UpdateButtons();
    }

    private void UpdateWatchCells()
    {
        for (var i = 0; i < _entries.Count && i < _watches.Rows.Count; i++)
        {
            var entry = _entries[i];
            _watches.Rows[i].Cells[3].Value = entry.Current?.ToString();
            _watches.Rows[i].Cells[5].Value = entry.Error.Length > 0 ? entry.Error : entry.Frozen ? "УДЕРЖАНИЕ" : "Наблюдение";
            _watches.Rows[i].DefaultCellStyle.ForeColor = entry.Frozen ? Accent : Ink;
        }
    }

    private void UpdateButtons()
    {
        // SelectionChanged can fire while the constructor is still creating controls.
        if (_attach is null || _write is null || _pin is null || _toggle is null || _remove is null || _setMoney is null) return;
        var connected = _memory is not null && _memory.IsAlive;
        RefreshFeatures();
        _attach.Enabled = !_busy;
        _setMoney.Enabled = connected && !_busy && _knownMoney is not null;
        _first.Enabled = connected && !_busy;
        _next.Enabled = connected && !_busy && _candidates.Count > 0;
        _cancel.Enabled = _busy;
        _type.Enabled = !_busy;
        _write.Enabled = _pin.Enabled = connected && !_busy && SelectedCandidate is not null;
        _toggle.Enabled = _remove.Enabled = connected && !_busy && SelectedWatch >= 0;
    }

    private void UpdateStatus()
    {
        try
        {
            var installation = InstallationInfo.Inspect(_path.Text);
            _status.Text = $"Steam build {installation.BuildId ?? "?"}  •  " +
                (_memory is not null && _memory.IsAlive ? $"подключено, PID {_memory.ProcessId}" : "нет подключения") +
                (installation.HasExecutable ? "" : "  •  проверь папку игры");
        }
        catch { _status.Text = "Проверь путь к игре."; }
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _featureError = "Ошибка: " + ex.Message;
            _featureStatus.Text = _featureError;
            Log(_featureError);
        }
    }

    private void Log(string text)
    {
        if (_closing || _log.IsDisposed) return;
        if (_log.TextLength > 20000) _log.Text = _log.Text[^10000..];
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
    }

    private void OpenHelp()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "README.ru.md");
        if (File.Exists(path)) Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { path }, UseShellExecute = false });
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!_interactive) return;
        for (var id = 1; id <= 6; id++)
        {
            var key = id == 6 ? 0x4Du : id == 5 ? 0x23u : (uint)(0x6F + id);
            if (Hotkeys.RegisterHotKey(Handle, id, 0x4003, key)) _hotkeys.Add(id);
            else Log("Не удалось зарегистрировать горячую клавишу № " + id + "; используй кнопки.");
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == Hotkeys.Message)
        {
            var id = (int)message.WParam;
            if (id == 6) SetMoney(); else if (id == 5) ReleaseAll(); else Guard(() => ToggleWatch(id - 1));
        }
        base.WndProc(ref message);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        foreach (var id in _hotkeys) Hotkeys.UnregisterHotKey(Handle, id);
        _hotkeys.Clear();
        base.OnHandleDestroyed(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        ReleaseAll();
        _closing = true;
        _scanCancellation?.Cancel();
        _timer.Stop();
        foreach (var entry in _entries) entry.Frozen = false;
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _timer.Dispose();
            _scanCancellation?.Cancel();
            if (!_busy) _memory?.Dispose();
        }
        base.Dispose(disposing);
    }

    private static ComboBox Combo(string[] items, int width)
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width };
        box.Items.AddRange(items);
        box.SelectedIndex = 0;
        return box;
    }

    private static FlowLayoutPanel Flow() => new() { Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
    private static Label Label(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 6, 5, 0) };
    private static Button Button(string text, Action action, int width)
    {
        var button = new TrainerButton { Text = text, Width = width, Height = 30, BackColor = Surface, ForeColor = Ink, FlatStyle = FlatStyle.Flat, Margin = new Padding(4, 0, 5, 0), UseVisualStyleBackColor = false };
        button.FlatAppearance.BorderColor = Color.FromArgb(89, 100, 82);
        button.Click += (_, _) => action();
        return button;
    }

    private sealed class TrainerButton : Button
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Enabled) { base.OnPaint(e); return; }
            e.Graphics.Clear(BackColor);
            ControlPaint.DrawBorder(e.Graphics, ClientRectangle, Color.FromArgb(65, 73, 65), ButtonBorderStyle.Solid);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Color.FromArgb(157, 165, 151),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    private static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = Bg, BorderStyle = BorderStyle.FixedSingle, GridColor = Color.FromArgb(64, 74, 65),
        EnableHeadersVisualStyles = false,
        ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Surface, ForeColor = Accent },
        DefaultCellStyle = new DataGridViewCellStyle { BackColor = Bg, ForeColor = Ink, SelectionBackColor = Color.FromArgb(76, 86, 61), SelectionForeColor = Color.White },
        RowTemplate = { Height = 29 }
    };

    private static void StyleInputs(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            if (control is TextBox or ComboBox) { control.BackColor = Color.FromArgb(15, 19, 17); control.ForeColor = Ink; }
            if (control.HasChildren) StyleInputs(control);
        }
    }
}
