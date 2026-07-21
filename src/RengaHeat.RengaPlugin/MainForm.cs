using System.Data;
using System.Drawing;
using System.Windows.Forms;
using RengaHeat.Core.Calculation;
using RengaHeat.Core.Classification;
using RengaHeat.Core.Mapping;
using RengaHeat.Core.Model;
using RengaHeat.Core.Profiles;
using RengaHeat.Core.Reporting;
using RengaHeat.Core.Topology;
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
    private const string Build = "сборка 12";

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
        "Обзор", "Исходные", "Уровни", "Карта", "Классификатор", "Сопоставление",
        "Проверка модели", "Расчёт", "Балансировка", "Предпросмотр изменений",
        "Отчёты и экспорт", "О программе",
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
        _nav.SelectedIndex = 0;
        // Модель НЕ читаем при открытии: на больших проектах это долго. Инженер сам выбирает,
        // загрузить всё или только выделенное (изолированные уровни), кнопками на панели.
    }

    /// <summary>
    /// Авто-расчёт при открытии/обновлении: результат появляется без кликов. Для очень больших
    /// моделей пропускаем (чтобы не подвешивать окно) — там расчёт запускается кнопкой.
    /// </summary>
    private void AutoCalculate()
    {
        var work = WorkingModel();
        if (work is null || work.Objects.Count is 0 or > 20000) return;
        try
        {
            Cursor = Cursors.WaitCursor;
            _outcome = RunSessionOn(work);
            UpdateStatus();
        }
        catch { /* авто-расчёт не критичен: инженер запустит вручную кнопкой «Рассчитать» */ }
        finally { Cursor = Cursors.Default; }
        if (_nav.SelectedItem is string s) ShowSection(s);
    }

    /// <summary>
    /// Загрузить модель по кнопке: всё или только выделенное в Renga (изолированные уровни).
    /// Чтение — единственная тяжёлая операция, поэтому запускается явно и с курсором ожидания.
    /// </summary>
    private void LoadModel(bool selectedOnly)
    {
        _outcome = null;
        try
        {
            Cursor = Cursors.WaitCursor;
            if (selectedOnly)
            {
                if (_ctx.ReadSelectedModel is null) { Msg("Загрузка выделенного доступна только в Renga."); return; }
                _model = _ctx.ReadSelectedModel();
                if (_model.Objects.Count == 0)
                    Msg("В Renga ничего не выделено. Изолируйте нужные уровни, выделите объекты " +
                        "(например, Ctrl+A выделяет видимые) и повторите загрузку.", MessageBoxIcon.Warning);
            }
            else
            {
                _model = _ctx.ReadModel();
            }
        }
        catch (Exception ex) { _model = null; Msg("Не удалось прочитать модель: " + ex.Message, MessageBoxIcon.Error); }
        finally { Cursor = Cursors.Default; }

        UpdateStatus();
        AutoCalculate();
        if (_nav.SelectedItem is string s) ShowSection(s);
    }

    /// <summary>Рабочая модель: исходная, отфильтрованная по выбранным уровням (раздел «Уровни»).</summary>
    private HeatingModel? WorkingModel() =>
        _model?.FilterByLevels(new HashSet<string>(_config.SelectedLevels));

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
        var loadSelBtn = IconToolButton("", "Загрузить выделенное в Renga (изолированные уровни)");   // Filter
        loadSelBtn.Click += (_, _) => LoadModel(selectedOnly: true);
        var loadAllBtn = IconToolButton("", "Загрузить всю модель (может быть долго)");              // Download
        loadAllBtn.Click += (_, _) => LoadModel(selectedOnly: false);
        var runBtn = IconToolButton("", "Рассчитать");                                                // Play
        runBtn.Click += (_, _) => RunCalculation();
        toolbar.Controls.Add(loadSelBtn);
        toolbar.Controls.Add(loadAllBtn);
        toolbar.Controls.Add(Separator());
        toolbar.Controls.Add(runBtn);
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

    private void UpdateStatus()
    {
        var p = EffectiveProfile();
        var over = _config.Overrides.Any ? " (изменён)" : "";
        var work = WorkingModel();
        var lvl = _config.SelectedLevels.Count > 0 ? $" · уровней: {_config.SelectedLevels.Count}" : "";
        var model = work is null ? "модель не загружена" : $"объектов: {work.Objects.Count}, связей: {work.Connections.Count}{lvl}";
        var ready = _outcome is null ? "расчёт не выполнялся"
            : (_outcome.IsReady ? "✓ готово" : "⚠ есть замечания");
        _status.Text = $"Профиль: {p.Name}{over} · график {p.HeatingSchedule}     |     {model}     |     {ready}";
    }

    private void ShowSection(string section)
    {
        _content.Controls.Clear();
        var body = section switch
        {
            "Обзор" => BuildOverview(),
            "Исходные" => BuildInputs(),
            "Уровни" => BuildLevels(),
            "Карта" => BuildMap(),
            "Классификатор" => BuildClassifier(),
            "Сопоставление" => BuildMapping(),
            "Проверка модели" => BuildValidation(),
            "Расчёт" => BuildCalculation(),
            "Балансировка" => BuildBalancing(),
            "Предпросмотр изменений" => BuildPreview(),
            "Отчёты и экспорт" => BuildReports(),
            "О программе" => BuildAbout(),
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
            panel.Controls.Add(Card("Модель не загружена",
                "Плагин не читает модель автоматически — на больших проектах это долго.\r\n\r\n" +
                "Для расчёта только нужной части (рекомендуется):\r\n" +
                "   1) в Renga изолируйте нужные уровни;\r\n" +
                "   2) выделите объекты (Ctrl+A выделяет видимые);\r\n" +
                "   3) нажмите на панели сверху «Загрузить выделенное» (значок фильтра).\r\n\r\n" +
                "Либо «Загрузить всю модель» (значок загрузки) — читается весь проект, может быть долго."));
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
        var work = WorkingModel();
        if (work is null) { Msg("Модель не загружена."); return; }
        try
        {
            Cursor = Cursors.WaitCursor;
            _config.Save();
            _outcome = RunSessionOn(work);
            UpdateStatus();
            _nav.SelectedItem = "Расчёт";
            ShowSection("Расчёт");
        }
        catch (Exception ex) { Msg("Ошибка расчёта: " + ex.Message, MessageBoxIcon.Error); }
        finally { Cursor = Cursors.Default; }
    }

    /// <summary>Пересчитать без смены раздела (после переназначений в «Карте»).</summary>
    private void RecalculateInPlace()
    {
        var work = WorkingModel();
        if (work is null) return;
        try
        {
            Cursor = Cursors.WaitCursor;
            _config.Save();
            _outcome = RunSessionOn(work);
            UpdateStatus();
        }
        catch (Exception ex) { Msg("Ошибка расчёта: " + ex.Message, MessageBoxIcon.Error); }
        finally { Cursor = Cursors.Default; }
        if (_nav.SelectedItem is string s) ShowSection(s);
    }

    /// <summary>Прогон сессии с учётом ручных назначений ролей из «Карты» (приоритет над авто).</summary>
    private SessionOutcome RunSessionOn(HeatingModel work)
    {
        ApplyManualRoles(work);
        return BuildSession().Run(work);
    }

    /// <summary>Ручные роли по UniqueId переживают перезагрузку модели — накладываем перед расчётом.</summary>
    private void ApplyManualRoles(HeatingModel m)
    {
        foreach (var (uid, roleName) in _config.ObjectRoles)
            if (m.Objects.TryGetValue(uid, out var o) &&
                Enum.TryParse<ObjectRole>(roleName, out var role) && role != ObjectRole.Unknown)
                o.Role = new RoleAssignment(role, RoleSource.Manual);
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
        var profile = EffectiveProfile();

        // Ручные назначения сторон (подача/обратка/источник) из «Карты» — абсолютный приоритет.
        var direction = new DirectionInference();
        foreach (var (uid, sideName) in _config.ObjectSides)
            if (Enum.TryParse<NetworkSide>(sideName, out var side) && side != NetworkSide.Unknown)
                direction.ManualSides[uid] = side;

        return new CalculationSession
        {
            Profile = profile,
            Mappings = mappings,
            Classifier = classifier,
            Rules = SessionFactory.RuleEngineFor(profile),
            Scenario = EffectiveScenario(),
            Direction = direction,
        };
    }

    /// <summary>Профиль с учётом пользовательских исходных данных (раздел «Исходные»).</summary>
    private RequirementsProfile EffectiveProfile() => _config.Overrides.ApplyTo(_ctx.Profile);

    private static readonly (string Name, CalculationScenario Scenario)[] Scenarios =
    {
        ("Базовый", CalculationScenario.Base),
        ("Экономичный", CalculationScenario.Economy),
        ("Тихий", CalculationScenario.Quiet),
    };

    private CalculationScenario EffectiveScenario()
    {
        foreach (var s in Scenarios)
            if (s.Name == _config.ScenarioName) return s.Scenario;
        return CalculationScenario.Base;
    }

    /// <summary>Одна редактируемая строка исходных: показать значение профиля и применить ввод к переопределению.</summary>
    private sealed record InputRow(string Label, string Unit,
        Func<RequirementsProfile, string> Show, Func<ProfileOverride, string, bool> Apply);

    private static string FmtNum(double d) => d.ToString("0.####", System.Globalization.CultureInfo.CurrentCulture);
    private static bool PD(string s, out double d) => double.TryParse(s.Replace(',', '.'),
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d);
    private static bool PB(string s, out bool b)
    {
        s = s.Trim().ToLowerInvariant();
        if (s is "да" or "true" or "1" or "+") { b = true; return true; }
        if (s is "нет" or "false" or "0" or "-") { b = false; return true; }
        b = false; return false;
    }

    private InputRow Dbl(string label, string unit, Func<RequirementsProfile, double> get, Action<ProfileOverride, double?> set)
        => new(label, unit, p => FmtNum(get(p)),
            (o, t) => { t = t.Trim(); if (t.Length == 0) { set(o, null); return true; } if (!PD(t, out var d)) return false; set(o, d); return true; });
    private InputRow Int(string label, string unit, Func<RequirementsProfile, int> get, Action<ProfileOverride, int?> set)
        => new(label, unit, p => get(p).ToString(),
            (o, t) => { t = t.Trim(); if (t.Length == 0) { set(o, null); return true; } if (!int.TryParse(t, out var i)) return false; set(o, i); return true; });
    private InputRow Bool(string label, Func<RequirementsProfile, bool> get, Action<ProfileOverride, bool?> set)
        => new(label, "да/нет", p => get(p) ? "да" : "нет",
            (o, t) => { t = t.Trim(); if (t.Length == 0) { set(o, null); return true; } if (!PB(t, out var b)) return false; set(o, b); return true; });

    private List<InputRow> InputDescriptors() => new()
    {
        Dbl("График отопления — подача", "°C", p => p.HeatingSchedule.SupplyC, (o, v) => o.HeatingSupplyC = v),
        Dbl("График отопления — обратка", "°C", p => p.HeatingSchedule.ReturnC, (o, v) => o.HeatingReturnC = v),
        Dbl("График вентиляции — подача", "°C", p => p.VentilationSchedule.SupplyC, (o, v) => o.VentSupplyC = v),
        Dbl("График вентиляции — обратка", "°C", p => p.VentilationSchedule.ReturnC, (o, v) => o.VentReturnC = v),
        Int("Не более квартир на коллектор", "шт", p => p.MaxApartmentsPerManifold, (o, v) => o.MaxApartmentsPerManifold = v),
        Int("Не более коллекторов в секции", "шт", p => p.MaxManifoldsPerSection, (o, v) => o.MaxManifoldsPerSection = v),
        Int("Не более приборов в кольце", "шт", p => p.MaxDevicesPerHorizontalLoop, (o, v) => o.MaxDevicesPerHorizontalLoop = v),
        Int("Этажей нижней зоны (макс.)", "эт", p => p.MaxFloorsLowerZone, (o, v) => o.MaxFloorsLowerZone = v),
        Dbl("Запас мощности (терморегуляторы)", "%", p => p.PowerMarginThermostaticPercent, (o, v) => o.PowerMarginThermostaticPercent = v),
        Dbl("Запас мощности (технические)", "%", p => p.PowerMarginTechnicalPercent, (o, v) => o.PowerMarginTechnicalPercent = v),
        Dbl("Длина радиатора в квартире (макс.)", "м", p => p.MaxApartmentRadiatorLengthM, (o, v) => o.MaxApartmentRadiatorLengthM = v),
        Int("ВГП сталь до Ду", "мм", p => p.MaxVgpDn, (o, v) => o.MaxVgpDn = v),
        Int("Поквартирные PE-Xa до Ду", "мм", p => p.MaxApartmentPexDn, (o, v) => o.MaxApartmentPexDn = v),
        Bool("Регулятор перепада перед коллектором", p => p.RequireDprBeforeManifold, (o, v) => o.RequireDprBeforeManifold = v),
        Bool("Теплосчётчик на обратке", p => p.HeatMeterOnReturn, (o, v) => o.HeatMeterOnReturn = v),
        Dbl("Лимит скорости — квартиры", "м/с", p => p.MaxVelocityApartmentMS, (o, v) => o.MaxVelocityApartmentMS = v),
        Dbl("Лимит скорости — магистрали", "м/с", p => p.MaxVelocityMainMS, (o, v) => o.MaxVelocityMainMS = v),
        Dbl("Лимит удельных потерь", "Па/м", p => p.MaxSpecificLossPaM, (o, v) => o.MaxSpecificLossPaM = v),
        Dbl("Автосоединение точек (0 — выкл.)", "мм", p => p.AutoStitchToleranceMm, (o, v) => o.AutoStitchToleranceMm = v),
    };

    private Control BuildInputs()
    {
        var baseP = _ctx.Profile;      // ЧТУ (эталон)
        var rows = InputDescriptors();

        // Шапка: профиль + выбор сценария
        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        header.Controls.Add(new Label { Text = $"Профиль {baseP.Name} (вер. {baseP.Version}) · {baseP.SourceDocument}", AutoSize = true, Font = _ui, ForeColor = TextDark, Margin = new Padding(0, 9, 20, 0) });
        header.Controls.Add(new Label { Text = "Сценарий:", AutoSize = true, Font = _ui, ForeColor = TextDark, Margin = new Padding(0, 9, 4, 0) });
        var scenario = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, Font = _ui, Margin = new Padding(0, 5, 0, 0) };
        foreach (var s in Scenarios) scenario.Items.Add(s.Name);
        scenario.SelectedItem = _config.ScenarioName ?? "Базовый";
        if (scenario.SelectedIndex < 0) scenario.SelectedIndex = 0;
        scenario.SelectedIndexChanged += (_, _) => { _config.ScenarioName = scenario.SelectedItem?.ToString(); _config.Save(); };
        header.Controls.Add(scenario);

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, EditMode = DataGridViewEditMode.EditOnEnter,
        };
        StyleGrid(grid);
        var cName = new DataGridViewTextBoxColumn { HeaderText = "Параметр", ReadOnly = true, FillWeight = 44 };
        var cVal = new DataGridViewTextBoxColumn { HeaderText = "Значение", FillWeight = 18 };
        var cUnit = new DataGridViewTextBoxColumn { HeaderText = "Ед.", ReadOnly = true, FillWeight = 12 };
        var cBase = new DataGridViewTextBoxColumn { HeaderText = "По ЧТУ", ReadOnly = true, FillWeight = 18 };
        grid.Columns.AddRange(cName, cVal, cUnit, cBase);

        // Страж против повторного входа: перерисовка/сброс сами меняют ячейки и иначе вызвали бы
        // обработчик рекурсивно (и ложно пометили бы профиль изменённым).
        var updating = false;
        void Fill()
        {
            updating = true;
            grid.Rows.Clear();
            var eff = EffectiveProfile();
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var row = grid.Rows[grid.Rows.Add(r.Label, r.Show(eff), r.Unit, r.Show(baseP))];
                row.Tag = i;
            }
            updating = false;
        }
        Fill();

        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        grid.CellValueChanged += (_, e) =>
        {
            if (updating || e.RowIndex < 0 || grid.Columns[e.ColumnIndex] != cVal) return;
            var idx = grid.Rows[e.RowIndex].Tag is int t ? t : -1;
            if (idx < 0) return;
            var text = grid.Rows[e.RowIndex].Cells[cVal.Index].Value?.ToString() ?? "";
            var ok = rows[idx].Apply(_config.Overrides, text);
            if (ok) _config.Save();
            else Msg("Некорректное значение — оставлено прежнее.", MessageBoxIcon.Warning);
            // Показать нормализованное/унаследованное значение без повторного входа.
            updating = true;
            grid.Rows[e.RowIndex].Cells[cVal.Index].Value = rows[idx].Show(EffectiveProfile());
            updating = false;
        };

        var apply = PrimaryButton("Применить и пересчитать");
        apply.Click += (_, _) => RunCalculation();
        var reset = SecondaryButton("Сбросить к ЧТУ");
        reset.Click += (_, _) =>
        {
            _config.Overrides = new ProfileOverride();
            _config.Save();
            Fill();
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        buttons.Controls.Add(apply);
        buttons.Controls.Add(reset);

        return VStack(header, 40, grid, buttons, 44);
    }

    private Control BuildLevels()
    {
        if (_model is null) return Info("Модель не загружена.");
        var summary = _model.LevelSummary();
        if (summary.Count == 1 && summary[0].Level == HeatingModel.NoLevel)
            return Info("Уровни из модели не считаны (все объекты без уровня) — расчёт учитывает все объекты. " +
                        "Если в проекте есть этажи, пришлите %TEMP%\\RengaHeat_types.log.");

        var levels = summary.Select(s => s.Level).ToList();
        var list = new CheckedListBox
        {
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Font = _ui, CheckOnClick = true, BackColor = PanelBg,
        };
        var selectedEmpty = _config.SelectedLevels.Count == 0;
        foreach (var (level, count) in summary)
            list.Items.Add($"{level}   ({count})", selectedEmpty || _config.SelectedLevels.Contains(level));

        void Store()
        {
            var chosen = new List<string>();
            for (var i = 0; i < list.Items.Count; i++)
                if (list.GetItemChecked(i)) chosen.Add(levels[i]);
            // Все отмечены — это «все уровни» (пустой список = фильтр выключен).
            _config.SelectedLevels = chosen.Count == levels.Count ? new List<string>() : chosen;
            _config.Save();
        }
        // ItemCheck срабатывает до применения галочки — читаем состояние после (BeginInvoke).
        list.ItemCheck += (_, _) => BeginInvoke(new Action(Store));

        var caption = new Label
        {
            Text = "Отметьте уровни для расчёта (по умолчанию — все). Объекты без уровня включаются всегда.",
            Dock = DockStyle.Fill, ForeColor = TextMuted, Font = _ui, TextAlign = ContentAlignment.MiddleLeft,
        };
        var apply = PrimaryButton("Применить и пересчитать");
        apply.Dock = DockStyle.Fill;
        apply.Click += (_, _) => RunCalculation();
        return VStack(caption, 30, Framed(list, new Padding(0)), apply, 48);
    }

    /// <summary>Пункт списка назначений «Карты»: подпись → роль или сторона (null/null — снять).</summary>
    private sealed record MapAssignOption(string Label, ObjectRole? Role, NetworkSide? Side)
    {
        public override string ToString() => Label;
    }

    private static readonly MapAssignOption[] MapAssignOptions =
    {
        new("Источник (ИТП)", ObjectRole.HeatSource, null),
        new("Радиатор", ObjectRole.Radiator, null),
        new("Конвектор", ObjectRole.Convector, null),
        new("Полотенцесушитель", ObjectRole.TowelRail, null),
        new("Подающий коллектор", ObjectRole.SupplyManifold, null),
        new("Обратный коллектор", ObjectRole.ReturnManifold, null),
        new("Стояк", ObjectRole.Riser, null),
        new("Подающая магистраль", ObjectRole.SupplyMain, null),
        new("Обратная магистраль", ObjectRole.ReturnMain, null),
        new("Труба", ObjectRole.Pipe, null),
        new("Насос", ObjectRole.Pump, null),
        new("Балансировочный клапан", ObjectRole.BalancingValve, null),
        new("Термостатический клапан", ObjectRole.ThermostaticValve, null),
        new("Запорная арматура", ObjectRole.ShutoffValve, null),
        new("Сторона: подача", null, NetworkSide.Supply),
        new("Сторона: обратка", null, NetworkSide.Return),
        new("— снять назначение —", null, null),
    };

    /// <summary>
    /// «Карта»: метки — подсветка групп выделением прямо в модели Renga; проверка и переназначение
    /// ролей/сторон по выделению; несовпадения направлений и автосоединения точек трассировки.
    /// </summary>
    private Control BuildMap()
    {
        if (_ctx.SelectManyInRenga is null) return Info("Карта работает только внутри Renga.");
        if (_model is null)
            return Info("Сначала загрузите модель: значок фильтра (выделенное) или загрузки (вся модель) на панели.");
        var work = WorkingModel();
        if (work is null) return Info("Модель не загружена.");

        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        panel.Controls.Add(Info(
            "Метки карты — подсветка выделением в модели Renga: нажмите группу, объекты подсветятся.\r\n" +
            "Если что-то определено неверно: выделите объекты в Renga → выберите, что это → «Назначить». " +
            "Назначение имеет приоритет над автоопределением и сохраняется между запусками."));

        // --- Переназначение по выделению Renga ---
        var assignRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 10) };
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240, Font = _ui, Margin = new Padding(0, 2, 8, 0) };
        combo.Items.AddRange(MapAssignOptions.Cast<object>().ToArray());
        combo.SelectedIndex = 0;
        var assignBtn = PrimaryButton("Назначить выделенным в Renga");
        assignBtn.Width = 240;
        assignBtn.Click += (_, _) =>
        {
            if (_ctx.GetSelectedUniqueIds is null) { Msg("Чтение выделения доступно только в Renga."); return; }
            if (combo.SelectedItem is not MapAssignOption opt) return;
            var uids = _ctx.GetSelectedUniqueIds();
            if (uids.Count == 0)
            {
                Msg("В Renga ничего не выделено. Выделите объекты в модели и повторите.", MessageBoxIcon.Warning);
                return;
            }
            foreach (var uid in uids)
            {
                if (opt.Role is { } role) { _config.ObjectRoles[uid] = role.ToString(); }
                else if (opt.Side is { } side) { _config.ObjectSides[uid] = side.ToString(); }
                else { _config.ObjectRoles.Remove(uid); _config.ObjectSides.Remove(uid); }
            }
            _config.Save();
            RecalculateInPlace();   // назначения сразу учитываются в топологии и расчёте
        };
        assignRow.Controls.Add(combo);
        assignRow.Controls.Add(assignBtn);
        var assigned = _config.ObjectRoles.Count + _config.ObjectSides.Count;
        if (assigned > 0)
        {
            var clearBtn = SecondaryButton($"Сбросить назначения ({assigned})");
            clearBtn.Click += (_, _) =>
            {
                _config.ObjectRoles.Clear();
                _config.ObjectSides.Clear();
                _config.Save();
                RecalculateInPlace();
            };
            assignRow.Controls.Add(clearBtn);
        }
        panel.Controls.Add(assignRow);

        if (_outcome is null)
        {
            panel.Controls.Add(Info("Группы карты появятся после расчёта — нажмите ▶ на панели."));
            return panel;
        }

        var topo = _outcome.Topology;
        List<string> BySide(NetworkSide side) =>
            topo.Sides.Where(kv => kv.Value.Side == side).Select(kv => kv.Key).ToList();
        List<string> ByRoles(params ObjectRole[] roles) =>
            work.Objects.Values.Where(o => roles.Contains(o.Role.Role)).Select(o => o.Id).ToList();

        var dirIds = _outcome.DirectionAudit.Issues
            .SelectMany(i => new[] { i.ObjectAId, i.ObjectBId }).Distinct().ToList();
        var stitchIds = _outcome.Stitched
            .SelectMany(s => new[] { s.ObjectAId, s.ObjectBId }).Distinct().ToList();
        var conflictIds = topo.Sides
            .Where(kv => kv.Value.Confidence == DirectionConfidence.Conflict).Select(kv => kv.Key).ToList();
        var manualIds = _config.ObjectRoles.Keys.Concat(_config.ObjectSides.Keys)
            .Where(work.Objects.ContainsKey).Distinct().ToList();

        var groups = new (string Name, List<string> Ids)[]
        {
            ("Подача", BySide(NetworkSide.Supply)),
            ("Обратка", BySide(NetworkSide.Return)),
            ("Приборы (радиаторы/конвекторы)", ByRoles(ObjectRole.Radiator, ObjectRole.Convector, ObjectRole.TowelRail, ObjectRole.AirHeater)),
            ("Коллекторы", ByRoles(ObjectRole.SupplyManifold, ObjectRole.ReturnManifold)),
            ("Стояки", ByRoles(ObjectRole.Riser)),
            ("Магистрали", ByRoles(ObjectRole.SupplyMain, ObjectRole.ReturnMain)),
            ("Арматура", ByRoles(ObjectRole.BalancingValve, ObjectRole.ThermostaticValve, ObjectRole.ShutoffValve,
                ObjectRole.DifferentialPressureRegulator, ObjectRole.Strainer, ObjectRole.HeatMeter)),
            ("Присоединения к ИТП", topo.ItpConnections.Select(o => o.Id).ToList()),
            ("Критическое кольцо", _outcome.Results.Select(r => r.CriticalRingDeviceId)
                .Where(id => id is not null).Cast<string>().Distinct().ToList()),
            ("⚠ Направление против потока", dirIds),
            ("Автосоединённые разрывы", stitchIds),
            ("Открытые концы сети", topo.OpenEnds.Select(o => o.Id).ToList()),
            ("⚠ Конфликты сторон", conflictIds),
            ("Назначено вручную", manualIds),
        };

        foreach (var (name, ids) in groups)
        {
            var btn = SecondaryButton($"Показать: {name}  ({ids.Count})");
            btn.Width = 440; btn.TextAlign = ContentAlignment.MiddleLeft; btn.Margin = new Padding(0, 0, 0, 6);
            btn.Enabled = ids.Count > 0;
            var captured = ids;
            btn.Click += (_, _) => _ctx.SelectManyInRenga!(captured);
            panel.Controls.Add(btn);
        }

        // Итог аудита направлений: расчёт всегда идёт по правильным направлениям, несовпадения —
        // указание, где в модели ориентация трасс «нарисована» против потока.
        var audit = _outcome.DirectionAudit;
        if (audit.Checked > 0)
            panel.Controls.Add(Info(
                $"Направления: проверено {audit.Checked}, совпадает {audit.Confirmed}, " +
                $"против потока {audit.Issues.Count}, неопределимо {audit.Undecidable}. " +
                "Расчёт ориентации модели не доверяет и всегда использует правильные направления; " +
                "список несовпадений — в «Проверке модели» (код DIR-101) и «Предпросмотре изменений»."));
        var hasCoords = work.Objects.Values.SelectMany(o => o.Ports).Any(p => p.HasLocation);
        if (!hasCoords)
            panel.Controls.Add(Info("Координаты портов эта версия API Renga не отдаёт — " +
                                    "автосоединение близких точек неактивно (разрывы ищите через «Открытые концы»)."));
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
            var recDn = r.Segments.Count(s => s.DiameterChangeRecommended);
            var velEx = r.Segments.Count(s => s.VelocityExceeded);
            var lossEx = r.Segments.Count(s => s.SpecificLossExceeded);
            var summary = Card("Итоги контура",
                $"Суммарный расход: {r.TotalFlowKgS * 3600:0.0} кг/ч        Требуемый напор: {r.RequiredHeadPa / 1000:0.00} кПа\r\n" +
                $"Критическое кольцо: {crit}\r\n" +
                $"Насос: {(r.Pump?.Pump is { } pump ? $"{pump.Article} ({r.Pump.DutyFlowM3H:0.00} м³/ч / {r.Pump.DutyHeadKPa:0.0} кПа)" : "не подобран")}        " +
                $"Сходимость: {(r.Converged ? "да" : "нет")}\r\n" +
                $"Диаметры: рекомендовано изменить {recDn}; превышений скорости {velEx}, удельных потерь {lossEx}");
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

            // «Почему это значение?» — журнал происхождения по выбранному прибору (требование ЧТУ).
            var why = SecondaryButton("Почему это значение?");
            why.Width = 200; why.Dock = DockStyle.Left;
            why.Click += (_, _) =>
            {
                var id = grid.CurrentRow?.Cells["ObjectId"].Value?.ToString();
                if (string.IsNullOrEmpty(id)) { Msg("Выберите прибор в таблице."); return; }
                ShowProvenance(id);
            };
            var bottomBar = new Panel { Dock = DockStyle.Bottom, Height = 40, BackColor = PanelBg, Padding = new Padding(0, 6, 0, 4) };
            bottomBar.Controls.Add(why);

            var devicesPanel = new Panel { Dock = DockStyle.Fill, BackColor = PanelBg };
            devicesPanel.Controls.Add(grid);        // приборы — центр
            devicesPanel.Controls.Add(bottomBar);   // «Почему это значение?» — низ

            // Таблица участков: скорость, удельные потери, лимиты СП, подбор диаметра.
            var segTable = new DataTable();
            segTable.Columns.Add("Участок");
            segTable.Columns.Add("Расход, кг/ч");
            segTable.Columns.Add("Скорость, м/с");
            segTable.Columns.Add("Лимит v");
            segTable.Columns.Add("Уд.потери, Па/м");
            segTable.Columns.Add("Лимит R");
            segTable.Columns.Add("Ду модель");
            segTable.Columns.Add("Ду реком.");
            segTable.Columns.Add("Серия");
            segTable.Columns.Add("Превышение");
            segTable.Columns.Add("ObjectId");
            foreach (var s in r.Segments)
                segTable.Rows.Add(s.ObjectName, $"{s.MassFlowKgS * 3600:0.0}", $"{s.VelocityMS:0.000}",
                    s.VelocityLimitMS > 0 ? $"{s.VelocityLimitMS:0.##}" : "", $"{s.SpecificLossPaM:0}",
                    s.SpecificLossLimitPaM > 0 ? $"{s.SpecificLossLimitPaM:0}" : "",
                    s.CurrentDn?.ToString() ?? "", s.RecommendedDn?.ToString() ?? "", s.RecommendedSeries ?? "",
                    (s.VelocityExceeded ? "v " : "") + (s.SpecificLossExceeded ? "R" : ""), s.ObjectId);
            var segGrid = MakeGrid(segTable, "ObjectId");
            segGrid.Dock = DockStyle.Fill;

            var innerTabs = new TabControl { Dock = DockStyle.Fill };
            var devPage = new TabPage("Приборы") { BackColor = PanelBg, Padding = new Padding(4) };
            devPage.Controls.Add(devicesPanel);
            var segPage = new TabPage("Участки") { BackColor = PanelBg, Padding = new Padding(4) };
            segPage.Controls.Add(segGrid);
            innerTabs.TabPages.Add(devPage);
            innerTabs.TabPages.Add(segPage);

            page.Controls.Add(innerTabs);   // центр
            page.Controls.Add(summary);     // верх
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

    private Control BuildAbout()
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        panel.Controls.Add(Card("RengaHeat",
            "Параметрический гидравлический расчёт систем отопления для Renga Professional.\r\n\r\n" +
            $"Версия ядра: {CalculationSession.EngineVersion} · интерфейс: {Build}\r\n" +
            "Разработчик: Раупов Хусрав"));
        panel.Controls.Add(Card("Что умеет",
            "• Чтение сети из Renga (вся модель или только выделенное/изолированные уровни)\r\n" +
            "• Автоопределение ролей, сторон подачи/обратки и присоединений к ИТП\r\n" +
            "• Карта: подсветка групп в модели, ручное переназначение ролей и сторон\r\n" +
            "• Аудит направлений потока и автосоединение близких точек трассировки\r\n" +
            "• Гидравлика по СП: расходы, потери, критическое кольцо, балансировка, насос\r\n" +
            "• Подбор диаметров труб по каталогам под лимиты скорости и удельных потерь\r\n" +
            "• Журнал «Почему это значение?», отчёты и пакет сверки Sankom/DCad"));
        panel.Controls.Add(Card("Файлы",
            $"Настройки: {SessionConfig.DefaultPath}\r\n" +
            "Диагностика: %TEMP%\\RengaHeat_init.log (запуск), %TEMP%\\RengaHeat_types.log (типы модели)"));
        panel.Controls.Add(Card("Ограничение",
            "Расчёт не является юридической заменой обязательного гидравлического расчёта " +
            "в Sankom/DCad и согласования арматуры по ЧТУ. Для подтверждения эквивалентности " +
            "методик используйте пакет сверки (раздел «Отчёты и экспорт»)."));
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

    /// <summary>Журнал происхождения значений объекта — команда «Почему это значение?».</summary>
    private void ShowProvenance(string objectId)
    {
        if (_outcome is null) return;
        var records = _outcome.ValueJournal.Where(r => r.ObjectId == objectId).ToList();
        var name = _model is not null && _model.Objects.TryGetValue(objectId, out var mo) ? mo.Name : objectId;
        var text = records.Count == 0
            ? "Нет записей журнала для объекта (значение не разрешалось для этого прибора)."
            : string.Join("\r\n\r\n", records.Select(r => r.Explain()));
        ShowTextDialog($"Почему это значение? — {name}", text);
    }

    /// <summary>Модальное окно с прокручиваемым текстом (журнал, пояснения).</summary>
    private void ShowTextDialog(string title, string text)
    {
        using var dlg = new Form
        {
            Text = title, Width = 740, Height = 480, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, BackColor = NavBg, Font = _ui,
        };
        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None, BackColor = PanelBg, Font = _ui, Text = text, WordWrap = true,
        };
        var pad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12), BackColor = PanelBg };
        pad.Controls.Add(box);
        var close = new Button { Text = "Закрыть", Dock = DockStyle.Right, Width = 100, Height = 28, FlatStyle = FlatStyle.System };
        close.Click += (_, _) => dlg.Close();
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8), BackColor = NavBg };
        bottom.Controls.Add(close);
        dlg.Controls.Add(pad);
        dlg.Controls.Add(bottom);
        dlg.ShowDialog(this);
    }

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
