using System.Data;
using System.Drawing;
using System.Windows.Forms;
using RengaHeat.Core.Calculation;
using RengaHeat.Core.Classification;
using RengaHeat.Core.Mapping;
using RengaHeat.Core.Model;
using RengaHeat.Core.Reporting;
using RengaHeat.Core.Validation;

namespace RengaHeat.RengaPlugin;

/// <summary>
/// Главное окно плагина: немодальный хаб с навигацией по разделам в светлом стиле Renga.
/// По открытию читает модель (быстро), но НЕ считает — расчёт запускается явной командой.
/// Зависит только от ядра RengaHeat.Core и делегатов PluginContext.
/// </summary>
public sealed class MainForm : Form
{
    // Палитра в духе интерфейса Renga: светлый фон, спокойный синий акцент.
    private static readonly Color Accent = Color.FromArgb(0x2F, 0x80, 0xED);
    private static readonly Color NavBg = Color.FromArgb(0xF4, 0xF6, 0xF8);
    private static readonly Color PanelBg = Color.White;
    private static readonly Color BorderColor = Color.FromArgb(0xE1, 0xE5, 0xEA);
    private static readonly Color TextDark = Color.FromArgb(0x25, 0x2A, 0x31);
    private static readonly Color TextMuted = Color.FromArgb(0x6B, 0x72, 0x80);
    private static readonly Color SelBg = Color.FromArgb(0xE8, 0xF0, 0xFE);

    private readonly Font _ui = new("Segoe UI", 9.5f);
    private readonly Font _uiBold = new("Segoe UI", 9.5f, FontStyle.Bold);
    private readonly Font _navFont = new("Segoe UI", 10f);
    private readonly Font _navFontSel = new("Segoe UI", 10f, FontStyle.Bold);
    private readonly Font _h1 = new("Segoe UI", 13f, FontStyle.Bold);

    private readonly PluginContext _ctx;
    private readonly SessionConfig _config = SessionConfig.Load();
    private HeatingModel? _model;
    private SessionOutcome? _outcome;

    // Распространённые имена свойства тепловой нагрузки — резервная цепочка после выбранного пользователем.
    private static readonly string[] DefaultLoadNames =
        { "Q_расч", "Qрасч", "Q", "Тепловая мощность", "Мощность 80/60", "Теплопотери" };

    private readonly ListBox _nav = new();
    private readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = PanelBg };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(14, 0, 0, 0) };

    private static readonly (string Title, string Glyph)[] Sections =
    {
        ("Обзор", "▦"), ("Профиль", "⚙"), ("Классификатор", "▤"), ("Сопоставление", "⇄"),
        ("Проверка модели", "✓"), ("Расчёт", "∑"), ("Балансировка", "≡"),
        ("Предпросмотр изменений", "✎"), ("Отчёты и экспорт", "⭳"),
    };

    public MainForm(PluginContext ctx)
    {
        _ctx = ctx;
        Text = "RengaHeat — гидравлический расчёт отопления";
        Width = 1080;
        Height = 720;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(880, 580);
        BackColor = PanelBg;
        Font = _ui;
        try { Icon = SystemIcons.Application; } catch { /* без иконки — не критично */ }

        BuildLayout();
        TryLoadModel();
        _nav.SelectedIndex = 0;
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, BackColor = PanelBg };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 232));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));   // шапка
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // навигация + контент
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));   // статус-бар

        // Шапка приложения
        var header = new Panel { Dock = DockStyle.Fill, BackColor = PanelBg };
        header.Paint += (_, e) => e.Graphics.DrawLine(new Pen(BorderColor), 0, header.Height - 1, header.Width, header.Height - 1);
        var title = new Label
        {
            Text = "RengaHeat", AutoSize = true, ForeColor = Accent,
            Font = new Font("Segoe UI", 15f, FontStyle.Bold), Location = new Point(16, 8),
        };
        var subtitle = new Label
        {
            Text = "гидравлический расчёт систем отопления", AutoSize = true, ForeColor = TextMuted,
            Font = _ui, Location = new Point(18, 34),
        };
        header.Controls.Add(title);
        header.Controls.Add(subtitle);
        root.Controls.Add(header, 0, 0);
        root.SetColumnSpan(header, 2);

        // Навигация (owner-drawn)
        _nav.Dock = DockStyle.Fill;
        _nav.BorderStyle = BorderStyle.None;
        _nav.BackColor = NavBg;
        _nav.DrawMode = DrawMode.OwnerDrawFixed;
        _nav.ItemHeight = 42;
        _nav.IntegralHeight = false;
        foreach (var s in Sections) _nav.Items.Add(s.Title);
        _nav.DrawItem += NavDrawItem;
        _nav.SelectedIndexChanged += (_, _) => { if (_nav.SelectedItem is string s) ShowSection(s); };
        var navHost = new Panel { Dock = DockStyle.Fill, BackColor = NavBg, Padding = new Padding(0, 6, 0, 0) };
        navHost.Paint += (_, e) => e.Graphics.DrawLine(new Pen(BorderColor), navHost.Width - 1, 0, navHost.Width - 1, navHost.Height);
        navHost.Controls.Add(_nav);
        root.Controls.Add(navHost, 0, 1);

        root.Controls.Add(_content, 1, 1);

        // Статус-бар
        _status.Font = _ui;
        _status.ForeColor = TextMuted;
        var statusHost = new Panel { Dock = DockStyle.Fill, BackColor = NavBg };
        statusHost.Paint += (_, e) => e.Graphics.DrawLine(new Pen(BorderColor), 0, 0, statusHost.Width, 0);
        statusHost.Controls.Add(_status);
        root.Controls.Add(statusHost, 0, 2);
        root.SetColumnSpan(statusHost, 2);

        Controls.Add(root);
    }

    private void NavDrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using (var bg = new SolidBrush(selected ? PanelBg : NavBg))
            e.Graphics.FillRectangle(bg, e.Bounds);
        if (selected)
            using (var bar = new SolidBrush(Accent))
                e.Graphics.FillRectangle(bar, e.Bounds.X, e.Bounds.Y, 4, e.Bounds.Height);

        var glyph = Sections[e.Index].Glyph;
        var text = Sections[e.Index].Title;
        var rect = new Rectangle(e.Bounds.X + 14, e.Bounds.Y, e.Bounds.Width - 16, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, glyph + "   " + text, selected ? _navFontSel : _navFont, rect,
            selected ? Accent : TextDark, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    /// <summary>Перечитать модель из Renga (окно немодальное — модель могла измениться).</summary>
    public void ReloadModel()
    {
        _outcome = null;
        TryLoadModel();
        if (_nav.SelectedItem is string s) ShowSection(s);
    }

    private void TryLoadModel()
    {
        try { _model = _ctx.ReadModel(); }
        catch (Exception ex) { _model = null; _status.Text = "Не удалось прочитать модель: " + ex.Message; return; }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var model = _model is null ? "модель не загружена" : $"объектов: {_model.Objects.Count}, связей: {_model.Connections.Count}";
        var ready = _outcome is null ? "расчёт не выполнялся"
            : (_outcome.IsReady ? "✓ готово" : "⚠ есть замечания");
        _status.Text = $"Профиль: {_ctx.Profile.Name} · график {_ctx.Profile.HeatingSchedule}     |     {model}     |     {ready}";
    }

    private void ShowSection(string section)
    {
        _content.Controls.Clear();
        var body = section switch
        {
            "Обзор" => BuildOverview(),
            "Профиль" => BuildProfile(),
            "Классификатор" => BuildClassifier(),
            "Сопоставление" => BuildMapping(),
            "Проверка модели" => BuildValidation(),
            "Расчёт" => BuildCalculation(),
            "Балансировка" => BuildBalancing(),
            "Предпросмотр изменений" => BuildPreview(),
            "Отчёты и экспорт" => BuildReports(),
            _ => Info("Раздел в разработке."),
        };
        body.Dock = DockStyle.Fill;

        var inner = new Panel { Dock = DockStyle.Fill, BackColor = PanelBg, Padding = new Padding(20, 14, 20, 16), AutoScroll = true };
        inner.Controls.Add(body);

        var host = new Panel { Dock = DockStyle.Fill, BackColor = PanelBg };
        host.Controls.Add(inner);
        host.Controls.Add(SectionHeader(section));
        _content.Controls.Add(host);
    }

    private Panel SectionHeader(string section)
    {
        var panel = new Panel { Dock = DockStyle.Top, Height = 50, BackColor = PanelBg, Padding = new Padding(20, 0, 20, 0) };
        panel.Paint += (_, e) => e.Graphics.DrawLine(new Pen(BorderColor), 0, panel.Height - 1, panel.Width, panel.Height - 1);
        panel.Controls.Add(new Label { Text = section, Dock = DockStyle.Fill, Font = _h1, ForeColor = TextDark, TextAlign = ContentAlignment.MiddleLeft });
        return panel;
    }

    // ---------- Разделы ----------

    private Control BuildOverview()
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };

        var bar = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 0, 0, 10) };
        var runBtn = PrimaryButton("▶  Рассчитать");
        runBtn.Click += (_, _) => RunCalculation();
        var reloadBtn = SecondaryButton("⭯  Обновить модель");
        reloadBtn.Click += (_, _) => ReloadModel();
        bar.Controls.Add(runBtn);
        bar.Controls.Add(reloadBtn);
        panel.Controls.Add(bar);

        if (_model is null)
        {
            panel.Controls.Add(Info("Модель не загружена. Откройте проект Renga и нажмите «Обновить модель»."));
            return panel;
        }

        var byType = _model.Objects.Values
            .GroupBy(o => string.IsNullOrEmpty(o.RengaTypeId) ? "(тип не задан)" : o.RengaTypeId)
            .OrderByDescending(g => g.Count()).Take(15)
            .Select(g => $"   • {g.Key}: {g.Count()}");
        panel.Controls.Add(Card("Модель",
            $"Всего объектов: {_model.Objects.Count}\r\nСоединений: {_model.Connections.Count}\r\n\r\n" +
            "Наиболее частые типы объектов:\r\n" + string.Join("\r\n", byType)));

        panel.Controls.Add(Info(
            "Расчёт выполняется по текущему профилю и настройкам классификатора/сопоставления.\r\n" +
            "Если роли объектов не распознаны, сначала настройте «Классификатор» и «Сопоставление», " +
            "иначе появится много замечаний «роль не определена»."));

        if (_outcome is not null)
        {
            var errors = _outcome.AllFindings.Count(f => f.Status == FindingStatus.Error);
            var decisions = _outcome.AllFindings.Count(f => f.Status == FindingStatus.NeedsDecision);
            panel.Controls.Add(Card("Последний расчёт",
                $"Источников (ИТП): {_outcome.Topology.Sources.Count}\r\n" +
                $"Рассчитано контуров: {_outcome.Results.Count}\r\n" +
                $"Ошибок: {errors}, требуют решения: {decisions}\r\n" +
                $"Готовность: {(_outcome.IsReady ? "ГОТОВО" : "есть замечания")}"));
        }
        return panel;
    }

    private void RunCalculation()
    {
        if (_model is null) { Msg("Модель не загружена."); return; }
        try
        {
            Cursor = Cursors.WaitCursor;
            _config.Save();
            _outcome = BuildSession().Run(_model);
            UpdateStatus();
            _nav.SelectedItem = "Расчёт";
            ShowSection("Расчёт");
        }
        catch (Exception ex) { Msg("Ошибка расчёта: " + ex.Message, MessageBoxIcon.Error); }
        finally { Cursor = Cursors.Default; }
    }

    /// <summary>Собрать сессию из профиля и пользовательских настроек (роли по типам, свойство нагрузки).</summary>
    private CalculationSession BuildSession()
    {
        var classifier = SessionFactory.DefaultClassifier();
        foreach (var (typeS, role) in _config.ResolvedTypeRoles())
            classifier.AddRule(new RoleRule($"Тип Renga → {RoleNames.Of(role)}",
                new RoleCriteria { RengaTypeId = typeS }, role, 50));

        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(_config.LoadPropertyName)) names.Add(_config.LoadPropertyName!);
        names.AddRange(DefaultLoadNames);
        var mappings = SessionFactory.DefaultMappings(names);

        return new CalculationSession
        {
            Profile = _ctx.Profile,
            Mappings = mappings,
            Classifier = classifier,
            Rules = SessionFactory.RuleEngineFor(_ctx.Profile),
            Scenario = _ctx.Scenario,
        };
    }

    private Control BuildProfile()
    {
        var p = _ctx.Profile;
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        panel.Controls.Add(Card($"{p.Name} (вер. {p.Version})",
            $"Источник: {p.SourceDocument}\r\n\r\n" +
            $"График отопления:            {p.HeatingSchedule}\r\n" +
            $"График теплоснабжения вент.: {p.VentilationSchedule}\r\n\r\n" +
            $"Не более квартир на коллектор:      {p.MaxApartmentsPerManifold}\r\n" +
            $"Не более коллекторов в секции:      {p.MaxManifoldsPerSection}\r\n" +
            $"Не более приборов в кольце:         {p.MaxDevicesPerHorizontalLoop}\r\n" +
            $"Этажей нижней зоны (макс.):         {p.MaxFloorsLowerZone}\r\n" +
            $"Запас мощности (терморег./тех.):    {p.PowerMarginThermostaticPercent} % / {p.PowerMarginTechnicalPercent} %\r\n" +
            $"Длина радиатора в квартире (макс.): {p.MaxApartmentRadiatorLengthM * 1000:0} мм\r\n" +
            $"Сталь ВГП до Ду{p.MaxVgpDn}, выше — электросварные; поквартирные PE-Xa до Ду{p.MaxApartmentPexDn}\r\n" +
            $"Регулятор перепада перед коллектором: {(p.RequireDprBeforeManifold ? "требуется" : "нет")}\r\n" +
            $"Теплосчётчик на обратке:             {(p.HeatMeterOnReturn ? "да" : "нет")}\r\n\r\n" +
            $"Лимит скорости (квартиры/магистрали): {p.MaxVelocityApartmentMS} / {p.MaxVelocityMainMS} м/с\r\n" +
            $"Лимит удельных потерь:                {p.MaxSpecificLossPaM} Па/м"));
        panel.Controls.Add(Info("Редактирование профиля и собственные профили — в следующей версии."));
        return panel;
    }

    private Control BuildClassifier()
    {
        if (_model is null) return Info("Модель не загружена.");

        var roleDisplays = Enum.GetValues<ObjectRole>()
            .Select(r => r == ObjectRole.Unknown ? "— не назначать —" : RoleNames.Of(r)).ToArray();
        static string DisplayOf(ObjectRole r) => r == ObjectRole.Unknown ? "— не назначать —" : RoleNames.Of(r);
        static ObjectRole RoleOfDisplay(string d)
        {
            if (d == "— не назначать —") return ObjectRole.Unknown;
            foreach (var r in Enum.GetValues<ObjectRole>()) if (RoleNames.Of(r) == d) return r;
            return ObjectRole.Unknown;
        }

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, EditMode = DataGridViewEditMode.EditOnEnter,
        };
        StyleGrid(grid);
        var cType = new DataGridViewTextBoxColumn { HeaderText = "Тип объекта Renga (GUID)", ReadOnly = true, FillWeight = 36 };
        var cSample = new DataGridViewTextBoxColumn { HeaderText = "Пример объекта", ReadOnly = true, FillWeight = 30 };
        var cCount = new DataGridViewTextBoxColumn { HeaderText = "Кол-во", ReadOnly = true, FillWeight = 10 };
        var cRole = new DataGridViewComboBoxColumn { HeaderText = "Роль", FlatStyle = FlatStyle.Flat, FillWeight = 24 };
        cRole.Items.AddRange(roleDisplays);
        grid.Columns.AddRange(cType, cSample, cCount, cRole);

        foreach (var g in _model.Objects.Values.GroupBy(o => o.RengaTypeId ?? "").OrderByDescending(g => g.Count()))
        {
            var role = _config.TypeRoles.TryGetValue(g.Key, out var rn) && Enum.TryParse<ObjectRole>(rn, out var rr)
                ? rr : ObjectRole.Unknown;
            grid.Rows.Add(g.Key, g.First().Name, g.Count(), DisplayOf(role));
        }

        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        grid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex < 0 || grid.Columns[e.ColumnIndex] != cRole) return;
            var typeS = grid.Rows[e.RowIndex].Cells[0].Value?.ToString() ?? "";
            var role = RoleOfDisplay(grid.Rows[e.RowIndex].Cells[cRole.Index].Value?.ToString() ?? "");
            if (role == ObjectRole.Unknown) _config.TypeRoles.Remove(typeS);
            else _config.TypeRoles[typeS] = role.ToString();
            _config.Save();
        };

        var caption = new Label
        {
            Text = "Назначьте роль каждому типу объектов Renga (это убирает замечания «роль не определена»). " +
                   "Выбор сохраняется автоматически и переносится между запусками.",
            Dock = DockStyle.Fill, ForeColor = TextMuted, Font = _ui, TextAlign = ContentAlignment.MiddleLeft,
        };
        var apply = PrimaryButton("Применить и пересчитать");
        apply.Dock = DockStyle.Fill;
        apply.Click += (_, _) => RunCalculation();

        return VStack(caption, 44, grid, apply, 48);
    }

    private Control BuildMapping()
    {
        if (_model is null) return Info("Модель не загружена.");

        var propNames = _model.Objects.Values.SelectMany(o => o.Properties.Values)
            .Select(p => p.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().OrderBy(n => n).ToList();

        const string autoItem = "(авто-поиск по стандартным именам)";
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 380, Font = _ui, Margin = new Padding(8, 4, 8, 4) };
        combo.Items.Add(autoItem);
        foreach (var n in propNames) combo.Items.Add(n);
        combo.Text = string.IsNullOrWhiteSpace(_config.LoadPropertyName) ? autoItem : _config.LoadPropertyName!;

        void Store()
        {
            var v = combo.Text?.Trim();
            _config.LoadPropertyName = string.IsNullOrEmpty(v) || v.StartsWith("(авто") ? null : v;
            _config.Save();
        }
        var applyBtn = PrimaryButton("Применить и пересчитать");
        applyBtn.Width = 220; applyBtn.Margin = new Padding(8, 0, 0, 0);
        applyBtn.Click += (_, _) => { Store(); RunCalculation(); };

        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight };
        controls.Controls.Add(new Label { Text = "Свойство тепловой нагрузки прибора:", AutoSize = true, Font = _ui, ForeColor = TextDark, Margin = new Padding(0, 8, 0, 0) });
        controls.Controls.Add(combo);
        controls.Controls.Add(applyBtn);

        // Справочно: текущие цепочки источников по полям.
        var table = new DataTable();
        table.Columns.Add("Расчётное поле");
        table.Columns.Add("Роли");
        table.Columns.Add("Цепочка источников");
        table.Columns.Add("Единица");
        foreach (var r in BuildSession().Mappings.Rules)
            table.Rows.Add(r.Field.DisplayName,
                r.AppliesToRoles.Count == 0 ? "все" : string.Join(", ", r.AppliesToRoles.Select(RoleNames.Of)),
                string.Join(" → ", r.SourceChain.Select(s => s.Kind)),
                r.SourceUnitSymbol ?? r.Field.BaseUnitSymbol);

        return VStack(controls, 48, MakeGrid(table, null), null, 0);
    }

    /// <summary>Вертикальная раскладка: верх (фикс. высота) / центр (тянется) / низ (фикс., может быть null).</summary>
    private static Control VStack(Control top, int topH, Control fill, Control? bottom, int bottomH)
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = PanelBg };
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, topH));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, bottom is null ? 0 : bottomH));
        top.Dock = DockStyle.Fill;
        fill.Dock = DockStyle.Fill;
        t.Controls.Add(top, 0, 0);
        t.Controls.Add(fill, 0, 1);
        if (bottom is not null) { bottom.Dock = DockStyle.Fill; t.Controls.Add(bottom, 0, 2); }
        return t;
    }

    private Control BuildValidation()
    {
        if (_outcome is null) return Info("Замечания появляются после расчёта. Нажмите «Рассчитать» в разделе «Обзор».");

        var all = _outcome.AllFindings.ToList();
        var table = new DataTable();
        table.Columns.Add("Статус");
        table.Columns.Add("Код");
        table.Columns.Add("Сообщение");
        table.Columns.Add("Группа");
        table.Columns.Add("ObjectId");
        const int cap = 3000;
        foreach (var f in all.OrderBy(f => f.Status).Take(cap))
            table.Rows.Add(StatusText(f.Status), f.Code, f.Message, f.Grouping ?? "", f.ObjectId ?? "");

        var summary = string.Join("    ", all.GroupBy(f => f.Status).Select(g => $"{StatusText(g.Key)}: {g.Count()}"));
        var note = all.Count > cap ? $"  (показаны первые {cap} из {all.Count})" : "";
        return WithGrid($"{summary}.{note}  Двойной клик — показать объект в Renga.", table, "ObjectId");
    }

    private Control BuildCalculation()
    {
        if (_outcome is null) return Info("Результаты появляются после расчёта. Нажмите «Рассчитать» в разделе «Обзор».");
        if (_outcome.Results.Count == 0)
            return Info("Расчёт не дал результатов: не найден источник (ИТП) или во фрагменте нет приборов. " +
                        "Проверьте роли в «Классификаторе» и замечания в «Проверке модели».");

        var tabs = new TabControl { Dock = DockStyle.Fill };
        foreach (var r in _outcome.Results)
        {
            var page = new TabPage(r.SourceName) { BackColor = PanelBg, Padding = new Padding(6) };
            var crit = r.Devices.FirstOrDefault(d => d.DeviceId == r.CriticalRingDeviceId)?.DeviceName ?? "—";
            var summary = Card("Итоги контура",
                $"Суммарный расход: {r.TotalFlowKgS * 3600:0.0} кг/ч        Требуемый напор: {r.RequiredHeadPa / 1000:0.00} кПа\r\n" +
                $"Критическое кольцо: {crit}\r\n" +
                $"Насос: {(r.Pump?.Pump is { } pump ? $"{pump.Article} ({r.Pump.DutyFlowM3H:0.00} м³/ч / {r.Pump.DutyHeadKPa:0.0} кПа)" : "не подобран")}        " +
                $"Сходимость: {(r.Converged ? "да" : "нет")}");
            summary.Dock = DockStyle.Top;

            var devTable = new DataTable();
            devTable.Columns.Add("Прибор");
            devTable.Columns.Add("Группа");
            devTable.Columns.Add("Нагрузка, Вт");
            devTable.Columns.Add("Расход, кг/ч");
            devTable.Columns.Add("Располаг., Па");
            devTable.Columns.Add("Критич.");
            devTable.Columns.Add("ObjectId");
            foreach (var d in r.Devices)
                devTable.Rows.Add(d.DeviceName, d.Grouping ?? "", $"{d.LoadW:0}", $"{d.MassFlowKgS * 3600:0.0}",
                    $"{d.AvailablePressurePa:0}", d.DeviceId == r.CriticalRingDeviceId ? "да" : "", d.DeviceId);
            var grid = MakeGrid(devTable, "ObjectId");
            grid.Dock = DockStyle.Fill;

            page.Controls.Add(grid);
            page.Controls.Add(summary);
            tabs.TabPages.Add(page);
        }
        return tabs;
    }

    private Control BuildBalancing()
    {
        if (_outcome is null) return Info("Балансировка появляется после расчёта.");
        var rows = _outcome.Results.SelectMany(r => r.Balancing).ToList();
        if (rows.Count == 0) return Info("Нет данных балансировки (сначала выполните расчёт).");

        var table = new DataTable();
        table.Columns.Add("Прибор");
        table.Columns.Add("Избыток, Па");
        table.Columns.Add("Требуемая Kv");
        table.Columns.Add("Клапан");
        table.Columns.Add("Преднастройка n");
        table.Columns.Add("Авторитет");
        table.Columns.Add("Критич.");
        table.Columns.Add("ObjectId");
        foreach (var b in rows)
            table.Rows.Add(b.DeviceObjectId, $"{b.ExcessPressurePa:0}", $"{b.RequiredKv:0.000}",
                b.Valve?.Article ?? "", b.PresetN?.ToString("0.0") ?? "", b.ValveAuthority?.ToString("0.00") ?? "",
                b.IsCriticalRing ? "да" : "", b.DeviceObjectId);
        return WithGrid("Преднастройка n балансировочных клапанов и авторитет.", table, "ObjectId");
    }

    private Control BuildPreview()
    {
        if (_outcome is null) return Info("Изменения формируются после расчёта.");
        var changes = _outcome.PreviewChanges.Changes;
        if (changes.Count == 0)
            return Info("Нет предлагаемых изменений. Они появляются, когда включена запись результатов и настроены " +
                        "свойства-приёмники (по умолчанию плагин работает в режиме только анализа — модель не меняется).");

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, ReadOnly = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        };
        StyleGrid(grid);
        var colApprove = new DataGridViewCheckBoxColumn { HeaderText = "✔", FillWeight = 8 };
        grid.Columns.Add(colApprove);
        grid.Columns.Add("obj", "Объект");
        grid.Columns.Add("target", "Что меняется");
        grid.Columns.Add("old", "Было");
        grid.Columns.Add("new", "Станет");
        grid.Columns.Add("reason", "Основание");
        for (var i = 1; i < grid.Columns.Count; i++) grid.Columns[i].ReadOnly = true;
        foreach (var ch in changes)
            grid.Rows.Add(ch.Approved, ch.ObjectName, ch.Target, ch.OldValue?.ToString() ?? "—",
                ch.NewValue?.ToString() ?? "—", ch.Reason);

        var apply = PrimaryButton("Применить отмеченные (с поддержкой Undo)");
        apply.Width = 320;
        apply.Click += (_, _) =>
        {
            if (_ctx.ApplyChanges is null) { Msg("Применение недоступно: включён режим только анализа."); return; }
            var approved = new List<ModelChange>();
            for (var i = 0; i < changes.Count; i++)
                if (grid.Rows[i].Cells[0].Value is true) { changes[i].Approved = true; approved.Add(changes[i]); }
            if (approved.Count == 0) { Msg("Не отмечено ни одного изменения."); return; }
            Msg(_ctx.ApplyChanges(approved));
        };

        var caption = new Label
        {
            Text = "Отметьте изменения и нажмите «Применить». Ничего не применяется без подтверждения; " +
                   "изменения выполняются одной операцией с поддержкой отмены (Undo).",
            Dock = DockStyle.Fill, ForeColor = TextMuted, Font = _ui, TextAlign = ContentAlignment.MiddleLeft,
        };
        return VStack(caption, 44, grid, apply, 48);
    }

    private Control BuildReports()
    {
        if (_outcome is null) return Info("Отчёты доступны после расчёта.");
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };

        panel.Controls.Add(SaveButton("Печатный отчёт по сессии (.txt)", "RengaHeat_отчёт.txt",
            "Текст (*.txt)|*.txt", () => Reports.SessionReport(_outcome!)));
        panel.Controls.Add(SaveButton("Отчёт проверки модели (.txt)", "RengaHeat_проверка.txt",
            "Текст (*.txt)|*.txt", () => Reports.FindingsReport(_outcome!.AllFindings)));
        panel.Controls.Add(SaveButton("Пакет сверки для Sankom/DCad (.csv)", "RengaHeat_сверка.csv",
            "CSV (*.csv)|*.csv", () => Reports.ReconciliationPackage(_outcome!)));
        if (_outcome!.Results.Count > 0)
        {
            panel.Controls.Add(SaveButton("Ведомость приборов первого контура (.csv)", "RengaHeat_приборы.csv",
                "CSV (*.csv)|*.csv", () => Reports.DevicesCsv(_outcome!.Results[0])));
            panel.Controls.Add(SaveButton("Ведомость участков первого контура (.csv)", "RengaHeat_участки.csv",
                "CSV (*.csv)|*.csv", () => Reports.SegmentsCsv(_outcome!.Results[0])));
        }
        panel.Controls.Add(Info("\r\nВНИМАНИЕ: расчёт RengaHeat не заменяет обязательный расчёт в Sankom/DCad " +
                                "и согласование производителя арматуры по ЧТУ. Пакет сверки — для проверки методик."));
        return panel;
    }

    // ---------- Вспомогательное ----------

    private Button PrimaryButton(string text)
    {
        var b = new Button
        {
            Text = text, AutoSize = false, Height = 38, Width = 240, Margin = new Padding(0, 0, 8, 0),
            Font = _uiBold, BackColor = Accent, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0x1E, 0x6F, 0xD9);
        return b;
    }

    private Button SecondaryButton(string text)
    {
        var b = new Button
        {
            Text = text, AutoSize = false, Height = 38, Width = 200, Margin = new Padding(0, 0, 8, 0),
            Font = _ui, BackColor = PanelBg, ForeColor = TextDark, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = BorderColor;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.MouseOverBackColor = NavBg;
        return b;
    }

    private Button SaveButton(string text, string defaultName, string filter, Func<string> content)
    {
        var btn = SecondaryButton("⭳  " + text);
        btn.Width = 420;
        btn.TextAlign = ContentAlignment.MiddleLeft;
        btn.Margin = new Padding(0, 0, 0, 8);
        btn.Click += (_, _) =>
        {
            using var dlg = new SaveFileDialog { FileName = defaultName, Filter = filter };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                System.IO.File.WriteAllText(dlg.FileName, content(), new System.Text.UTF8Encoding(true));
                Msg("Сохранено: " + dlg.FileName);
            }
            catch (Exception ex) { Msg("Ошибка сохранения: " + ex.Message, MessageBoxIcon.Error); }
        };
        return btn;
    }

    private Panel Card(string title, string body)
    {
        var card = new Panel { AutoSize = true, BackColor = Color.FromArgb(0xFB, 0xFC, 0xFD), Margin = new Padding(0, 0, 0, 12), Padding = new Padding(14, 10, 14, 12) };
        card.Paint += (_, e) => e.Graphics.DrawRectangle(new Pen(BorderColor), 0, 0, card.Width - 1, card.Height - 1);
        var flow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        flow.Controls.Add(new Label { Text = title, AutoSize = true, Font = _uiBold, ForeColor = Accent, Margin = new Padding(0, 0, 0, 4) });
        flow.Controls.Add(new Label { Text = body, AutoSize = true, Font = _ui, ForeColor = TextDark, MaximumSize = new Size(720, 0) });
        card.Controls.Add(flow);
        return card;
    }

    private Control WithGrid(string caption, DataTable table, string? idColumn)
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = PanelBg };
        host.Controls.Add(MakeGrid(table, idColumn));
        host.Controls.Add(new Label { Text = caption, Dock = DockStyle.Top, Height = 34, ForeColor = TextMuted, Font = _ui, TextAlign = ContentAlignment.MiddleLeft });
        return host;
    }

    private DataGridView MakeGrid(DataTable table, string? idColumn)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, DataSource = table, ReadOnly = true, AllowUserToAddRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        };
        StyleGrid(grid);
        grid.DataBindingComplete += (_, _) =>
        {
            if (idColumn is not null && grid.Columns.Contains(idColumn)) grid.Columns[idColumn].Visible = false;
        };
        if (idColumn is not null)
            grid.CellDoubleClick += (_, e) =>
            {
                if (e.RowIndex < 0 || _ctx.SelectInRenga is null) return;
                var id = grid.Rows[e.RowIndex].Cells[idColumn].Value?.ToString();
                if (!string.IsNullOrEmpty(id)) _ctx.SelectInRenga(id);
            };
        return grid;
    }

    private void StyleGrid(DataGridView grid)
    {
        grid.BorderStyle = BorderStyle.None;
        grid.BackgroundColor = PanelBg;
        grid.GridColor = BorderColor;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersHeight = 34;
        grid.ColumnHeadersDefaultCellStyle.BackColor = NavBg;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = TextDark;
        grid.ColumnHeadersDefaultCellStyle.Font = _uiBold;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
        grid.DefaultCellStyle.Font = _ui;
        grid.DefaultCellStyle.ForeColor = TextDark;
        grid.DefaultCellStyle.SelectionBackColor = SelBg;
        grid.DefaultCellStyle.SelectionForeColor = TextDark;
        grid.DefaultCellStyle.Padding = new Padding(6, 3, 6, 3);
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(0xFA, 0xFB, 0xFC);
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.AllowUserToResizeRows = false;
        grid.RowTemplate.Height = 26;
    }

    private Label Info(string text) => new()
    {
        Text = text, AutoSize = true, Font = _ui, ForeColor = TextMuted,
        Margin = new Padding(0, 6, 0, 6), MaximumSize = new Size(760, 0),
    };

    private void Msg(string text, MessageBoxIcon icon = MessageBoxIcon.Information) =>
        MessageBox.Show(this, text, "RengaHeat", MessageBoxButtons.OK, icon);

    private static string StatusText(FindingStatus s) => s switch
    {
        FindingStatus.Error => "Ошибка",
        FindingStatus.Warning => "Предупреждение",
        FindingStatus.Assumption => "Допущение",
        FindingStatus.NeedsDecision => "Требует решения",
        FindingStatus.Done => "Выполнено",
        FindingStatus.Excluded => "Исключено",
        FindingStatus.ManuallyFixed => "Зафиксировано вручную",
        _ => s.ToString(),
    };
}
