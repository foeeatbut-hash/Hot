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
    // Нативная светлая палитра Renga (Qt-стиль): системный серый фон, белые списки/таблицы,
    // тонкие серые рамки, стандартное синее выделение.
    private static readonly Color NavBg = SystemColors.Control;        // тулбар, статус-бар, фон окна
    private static readonly Color PanelBg = Color.White;               // контент, списки, таблицы
    private static readonly Color BorderColor = Color.FromArgb(0xAB, 0xAB, 0xAB);
    private static readonly Color TextDark = SystemColors.ControlText;
    private static readonly Color TextMuted = Color.FromArgb(0x60, 0x60, 0x60);
    private static readonly Color SelBg = Color.FromArgb(0xCC, 0xE4, 0xF7);   // классическое выделение Windows
    private static readonly Color ToolHover = Color.FromArgb(0xE0, 0xE6, 0xEE);

    // Видимый штамп версии плагина. Увеличивайте при каждом изменении UI — по нему сразу
    // видно в заголовке окна, свежая DLL загружена или старая.
    private const string Build = "сборка 7";

    private readonly Font _ui = new("Segoe UI", 9f);
    private readonly Font _uiBold = new("Segoe UI", 9f, FontStyle.Bold);
    private readonly Font _h1 = new("Segoe UI", 10.5f, FontStyle.Bold);
    // Segoe MDL2 Assets — системный шрифт значков Windows (10/11); даёт компактные векторные
    // иконки тулбара без подписей, как «+ / копия / карандаш / крестик» в диалогах Renga.
    private readonly Font _iconFont = new("Segoe MDL2 Assets", 13f);
    private readonly ToolTip _tip = new() { AutoPopDelay = 6000, InitialDelay = 350, ReshowDelay = 100 };

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

    private static readonly string[] Sections =
    {
        "Обзор", "Профиль", "Классификатор", "Сопоставление",
        "Проверка модели", "Расчёт", "Балансировка", "Предпросмотр изменений", "Отчёты и экспорт",
    };

    public MainForm(PluginContext ctx)
    {
        _ctx = ctx;
        // Штамп сборки в заголовке — чтобы однозначно проверять, что загружена свежая DLL,
        // а не старая копия из кэша Renga/другой папки. Меняется с каждым обновлением плагина.
        Text = $"RengaHeat — гидравлический расчёт отопления · {Build}";
        Width = 1080;
        Height = 720;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(880, 580);
        BackColor = NavBg;
        Font = _ui;
        try { Icon = SystemIcons.Application; } catch { /* без иконки — не критично */ }

        BuildLayout();
        TryLoadModel();
        _nav.SelectedIndex = 0;
        AutoCalculate();   // сразу показываем результат — без ручных действий
    }

    /// <summary>
    /// Авто-расчёт при открытии/обновлении: результат появляется без кликов. Для очень больших
    /// моделей пропускаем (чтобы не подвешивать окно) — там расчёт запускается кнопкой.
    /// </summary>
    private void AutoCalculate()
    {
        if (_model is null || _model.Objects.Count is 0 or > 20000) return;
        try
        {
            Cursor = Cursors.WaitCursor;
            _outcome = BuildSession().Run(_model);
            UpdateStatus();
        }
        catch { /* авто-расчёт не критичен: инженер запустит вручную кнопкой «Рассчитать» */ }
        finally { Cursor = Cursors.Default; }
        if (_nav.SelectedItem is string s) ShowSection(s);
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, BackColor = NavBg };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 208));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));   // тулбар
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // навигация + контент
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));   // нижняя панель

        // Тулбар — компактные квадратные значки без подписей (подсказка по наведению),
        // как ряд «+ / копия / карандаш / крестик» в диалогах Renga.
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = NavBg, Padding = new Padding(4, 4, 0, 0), WrapContents = false };
        toolbar.Paint += (_, e) => e.Graphics.DrawLine(new Pen(BorderColor), 0, toolbar.Height - 1, toolbar.Width, toolbar.Height - 1);
        var runBtn = IconToolButton("", "Рассчитать");     // Play
        runBtn.Click += (_, _) => RunCalculation();
        var reloadBtn = IconToolButton("", "Обновить модель");  // Refresh
        reloadBtn.Click += (_, _) => ReloadModel();
        toolbar.Controls.Add(runBtn);
        toolbar.Controls.Add(Separator());
        toolbar.Controls.Add(reloadBtn);
        root.Controls.Add(toolbar, 0, 0);
        root.SetColumnSpan(toolbar, 2);

        // Навигация — нативный список в белой рамке-инсете (как список стилей в диалогах Renga)
        _nav.Dock = DockStyle.Fill;
        _nav.BorderStyle = BorderStyle.None;
        _nav.BackColor = PanelBg;
        _nav.Font = _ui;
        _nav.ItemHeight = 24;
        _nav.IntegralHeight = false;
        foreach (var s in Sections) _nav.Items.Add(s);
        _nav.SelectedIndexChanged += (_, _) => { if (_nav.SelectedItem is string s) ShowSection(s); };
        var leftPanel = new Panel { Dock = DockStyle.Fill, BackColor = NavBg };
        leftPanel.Controls.Add(Framed(_nav, new Padding(8, 0, 6, 8)));
        leftPanel.Controls.Add(new Label
        {
            Text = "Разделы", Dock = DockStyle.Top, Height = 24, Font = _uiBold, ForeColor = TextDark,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(9, 5, 0, 0), BackColor = NavBg,
        });
        root.Controls.Add(leftPanel, 0, 1);

        // Контент — та же белая рамка-инсет на сером фоне (область просмотра справа в Renga)
        root.Controls.Add(Framed(_content, new Padding(0, 8, 8, 8)), 1, 1);

        // Нижняя панель: статус слева, кнопка «Закрыть» справа (как OK/Отмена в Renga)
        var bottom = new Panel { Dock = DockStyle.Fill, BackColor = NavBg };
        bottom.Paint += (_, e) => e.Graphics.DrawLine(new Pen(BorderColor), 0, 0, bottom.Width, 0);
        _status.Font = _ui;
        _status.ForeColor = TextMuted;
        var closeBtn = new Button { Text = "Закрыть", Width = 96, Height = 26, FlatStyle = FlatStyle.System, Font = _ui, Dock = DockStyle.Right };
        closeBtn.Click += (_, _) => Close();
        var closeHost = new Panel { Dock = DockStyle.Right, Width = 112, Padding = new Padding(8, 7, 8, 7), BackColor = NavBg };
        closeHost.Controls.Add(closeBtn);
        bottom.Controls.Add(closeHost);
        bottom.Controls.Add(_status);
        root.Controls.Add(bottom, 0, 2);
        root.SetColumnSpan(bottom, 2);

        Controls.Add(root);
    }

    /// <summary>Обернуть контрол в белую панель с тонкой серой рамкой на сером фоне (инсет Renga).</summary>
    private static Panel Framed(Control inner, Padding outerMargin)
    {
        inner.Dock = DockStyle.Fill;
        var box = new Panel { Dock = DockStyle.Fill, BackColor = PanelBg, Padding = new Padding(1) };
        box.Paint += (_, e) => e.Graphics.DrawRectangle(new Pen(BorderColor), 0, 0, box.Width - 1, box.Height - 1);
        box.Controls.Add(inner);
        var host = new Panel { Dock = DockStyle.Fill, BackColor = NavBg, Padding = outerMargin };
        host.Controls.Add(box);
        return host;
    }

    /// <summary>
    /// Компактная квадратная кнопка-значок без подписи (значок Segoe MDL2 Assets, подсказка по
    /// наведению) — как «+ / копия / карандаш / крестик» в тулбарах диалогов Renga. TabStop
    /// выключен, чтобы после клика не оставался рамка-фокус — тулбарные значки его не показывают.
    /// </summary>
    private Button IconToolButton(string glyph, string tooltip)
    {
        var b = new Button
        {
            Text = glyph, Font = _iconFont, Width = 32, Height = 30,
            FlatStyle = FlatStyle.Flat, BackColor = NavBg, ForeColor = TextDark,
            TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(0), Cursor = Cursors.Hand,
            TabStop = false, UseCompatibleTextRendering = false,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = ToolHover;
        b.FlatAppearance.MouseDownBackColor = Color.FromArgb(0xCC, 0xDA, 0xE8);
        _tip.SetToolTip(b, tooltip);
        return b;
    }

    /// <summary>Тонкий вертикальный разделитель между группами значков тулбара.</summary>
    private Panel Separator() => new()
    {
        Width = 1, Height = 20, Margin = new Padding(4, 5, 4, 5), BackColor = BorderColor,
    };

    /// <summary>Перечитать модель из Renga (окно немодальное — модель могла измениться) и пересчитать.</summary>
    public void ReloadModel()
    {
        _outcome = null;
        TryLoadModel();
        AutoCalculate();
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
        var panel = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = PanelBg, Padding = new Padding(16, 0, 16, 0) };
        panel.Paint += (_, e) => e.Graphics.DrawLine(new Pen(BorderColor), 0, panel.Height - 1, panel.Width, panel.Height - 1);
        panel.Controls.Add(new Label { Text = section, Dock = DockStyle.Fill, Font = _h1, ForeColor = TextDark, TextAlign = ContentAlignment.MiddleLeft });
        return panel;
    }

    // ---------- Разделы ----------

    private Control BuildOverview()
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };

        if (_model is null)
        {
            panel.Controls.Add(Info("Модель не загружена. Откройте проект Renga и нажмите «Обновить модель» на панели сверху."));
            return panel;
        }

        if (_model.Objects.Count == 0)
        {
            panel.Controls.Add(Card("Инженерных объектов не найдено",
                "В прочитанной модели нет объектов с портами трубопровода/воздуховода или трассами.\r\n\r\n" +
                "Возможные причины:\r\n" +
                "   • открыт проект без размещённой инженерной сети (только стили/каталог);\r\n" +
                "   • сеть смоделирована объектами, которые не опознаются как инженерные.\r\n\r\n" +
                "Полное распределение типов объектов записано в файл диагностики:\r\n" +
                "   %TEMP%\\RengaHeat_types.log\r\n" +
                "Откройте его (Win+R → %TEMP%) и пришлите — по нему точно видно, какие типы есть в проекте."));
            return panel;
        }

        var byType = _model.Objects.Values
            .GroupBy(o => string.IsNullOrEmpty(o.RengaTypeId) ? "(тип не задан)" : o.RengaTypeId)
            .OrderByDescending(g => g.Count()).Take(15)
            .Select(g => $"   • {g.Key}: {g.Count()}");
        panel.Controls.Add(Card("Модель",
            $"Всего объектов: {_model.Objects.Count}\r\nСоединений: {_model.Connections.Count}\r\n\r\n" +
            "Наиболее частые типы объектов:\r\n" + string.Join("\r\n", byType)));

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
        // Авто-классификатор + правила инженера «тип Renga → роль» (приоритет 50 бьёт эвристику).
        var classifier = SessionFactory.DefaultClassifier();
        foreach (var (typeS, role) in _config.ResolvedTypeRoles())
            classifier.AddRule(new RoleRule($"Тип Renga → {RoleNames.Of(role)}",
                new RoleCriteria { RengaTypeId = typeS }, role, 50));

        // Сопоставление: свойство инженера по каждому полю в начало цепочки, затем стандартный резерв.
        var mappings = SessionFactory.MappingsFor(_config.FieldProperties, DefaultLoadNames);

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
        panel.Controls.Add(Info("Профиль применяется как данные (лимиты, запасы, требования ЧТУ). Редактирование — в следующей версии."));
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

        // Авто-подсказка роли по типам — таблица заполняется сразу, инженер лишь правит исключения.
        var suggested = SessionFactory.DefaultClassifier().SuggestRolesByType(_model);
        foreach (var g in _model.Objects.Values.GroupBy(o => o.RengaTypeId ?? "").OrderByDescending(g => g.Count()))
        {
            var role = _config.TypeRoles.TryGetValue(g.Key, out var rn) && Enum.TryParse<ObjectRole>(rn, out var rr)
                ? rr
                : suggested.GetValueOrDefault(g.Key, ObjectRole.Unknown);
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
            Text = "Роли распознаны автоматически по имени, портам и DN. Поправьте, где нужно — выбор сохраняется.",
            Dock = DockStyle.Fill, ForeColor = TextMuted, Font = _ui, TextAlign = ContentAlignment.MiddleLeft,
        };
        var apply = PrimaryButton("Применить и пересчитать");
        apply.Dock = DockStyle.Fill;
        apply.Click += (_, _) => RunCalculation();

        return VStack(caption, 30, grid, apply, 48);
    }

    private Control BuildMapping()
    {
        if (_model is null) return Info("Модель не загружена.");

        // Текущая цепочка источников по каждому полю (с учётом выбранных свойств) — для показа резерва.
        var chains = BuildSession().Mappings.Rules
            .GroupBy(r => r.Field.Key)
            .ToDictionary(g => g.Key, g => string.Join(" → ", g.First().SourceChain.Select(s => s.Kind)));

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, EditMode = DataGridViewEditMode.EditOnEnter,
        };
        StyleGrid(grid);
        var cField = new DataGridViewTextBoxColumn { HeaderText = "Расчётное поле", ReadOnly = true, FillWeight = 32 };
        var cProp = new DataGridViewTextBoxColumn { HeaderText = "Свойство-источник", FillWeight = 24 };
        var cChain = new DataGridViewTextBoxColumn { HeaderText = "Цепочка резерва", ReadOnly = true, FillWeight = 34 };
        var cUnit = new DataGridViewTextBoxColumn { HeaderText = "Ед.", ReadOnly = true, FillWeight = 10 };
        grid.Columns.AddRange(cField, cProp, cChain, cUnit);

        foreach (var f in StandardFields.All)
        {
            var prop = _config.FieldProperties.GetValueOrDefault(f.Key, "");
            var row = grid.Rows[grid.Rows.Add(f.DisplayName, prop, chains.GetValueOrDefault(f.Key, "—"), f.BaseUnitSymbol)];
            row.Tag = f.Key;   // устойчивый ключ поля
        }

        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        grid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex < 0 || grid.Columns[e.ColumnIndex] != cProp) return;
            var key = grid.Rows[e.RowIndex].Tag as string ?? "";
            var v = grid.Rows[e.RowIndex].Cells[cProp.Index].Value?.ToString()?.Trim() ?? "";
            if (string.IsNullOrEmpty(v)) _config.FieldProperties.Remove(key);
            else _config.FieldProperties[key] = v;
            _config.Save();
        };

        var caption = new Label
        {
            Text = "Свойство-источник по каждому полю (пусто — авто-поиск). Оно ставится первым в цепочке резерва.",
            Dock = DockStyle.Fill, ForeColor = TextMuted, Font = _ui, TextAlign = ContentAlignment.MiddleLeft,
        };
        var apply = PrimaryButton("Применить и пересчитать");
        apply.Dock = DockStyle.Fill;
        apply.Click += (_, _) => RunCalculation();

        return VStack(caption, 30, grid, apply, 48);
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
            return Info("Нет результатов: не найден источник (ИТП) или приборы. " +
                        "Проверьте роли в «Классификаторе» и «Проверку модели».");

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
            return Info("Изменений нет — включён режим только анализа (модель не меняется). " +
                        "Запись включается настройкой свойств-приёмников.");

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
            Text = "Отметьте изменения и нажмите «Применить» — одной операцией, с поддержкой отмены (Undo).",
            Dock = DockStyle.Fill, ForeColor = TextMuted, Font = _ui, TextAlign = ContentAlignment.MiddleLeft,
        };
        return VStack(caption, 30, grid, apply, 48);
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
        panel.Controls.Add(Info("\r\nВНИМАНИЕ: RengaHeat не заменяет обязательный расчёт в Sankom/DCad и согласование " +
                                "арматуры по ЧТУ. Пакет сверки — для проверки методик."));
        return panel;
    }

    // ---------- Вспомогательное ----------

    // Нативные кнопки Windows (как OK/Отмена в диалогах Renga).
    private Button PrimaryButton(string text) => new()
    {
        Text = text, AutoSize = false, Height = 30, Width = 230, Margin = new Padding(0, 0, 8, 0),
        Font = _ui, FlatStyle = FlatStyle.System, UseVisualStyleBackColor = true,
    };

    private Button SecondaryButton(string text) => new()
    {
        Text = text, AutoSize = false, Height = 30, Width = 200, Margin = new Padding(0, 0, 8, 0),
        Font = _ui, FlatStyle = FlatStyle.System, UseVisualStyleBackColor = true,
    };

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
        var card = new Panel { AutoSize = true, BackColor = PanelBg, Margin = new Padding(0, 0, 0, 10), Padding = new Padding(12, 8, 12, 10) };
        card.Paint += (_, e) => e.Graphics.DrawRectangle(new Pen(BorderColor), 0, 0, card.Width - 1, card.Height - 1);
        var flow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        flow.Controls.Add(new Label { Text = title, AutoSize = true, Font = _uiBold, ForeColor = TextDark, Margin = new Padding(0, 0, 0, 4) });
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
        grid.BorderStyle = BorderStyle.FixedSingle;
        grid.BackgroundColor = PanelBg;
        grid.GridColor = Color.FromArgb(0xD6, 0xD6, 0xD6);
        grid.EnableHeadersVisualStyles = true;   // нативные серые заголовки в стиле Renga
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersHeight = 26;
        grid.ColumnHeadersDefaultCellStyle.Font = _ui;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
        grid.DefaultCellStyle.Font = _ui;
        grid.DefaultCellStyle.ForeColor = TextDark;
        grid.DefaultCellStyle.SelectionBackColor = SelBg;
        grid.DefaultCellStyle.SelectionForeColor = TextDark;
        grid.DefaultCellStyle.Padding = new Padding(4, 2, 4, 2);
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.AllowUserToResizeRows = false;
        grid.RowTemplate.Height = 22;
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
