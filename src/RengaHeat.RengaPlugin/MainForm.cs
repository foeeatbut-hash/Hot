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
    // Современная светлая палитра: мягкий сине-серый фон-подложка, белые поверхности-карточки,
    // тонкие деликатные рамки и единый синий акцент. Имена сохранены — используются по всему файлу.
    private static readonly Color NavBg = Color.FromArgb(0xEE, 0xF1, 0xF6);       // фон-подложка окна/шапки
    private static readonly Color PanelBg = Color.White;                          // поверхности: контент, карточки, таблицы
    private static readonly Color SurfaceAlt = Color.FromArgb(0xF6, 0xF8, 0xFB);  // фон панели навигации
    private static readonly Color BorderColor = Color.FromArgb(0xDD, 0xE3, 0xEB); // деликатная рамка
    private static readonly Color BorderStrong = Color.FromArgb(0xC9, 0xD2, 0xDE);
    private static readonly Color TextDark = Color.FromArgb(0x1E, 0x2A, 0x38);
    private static readonly Color TextMuted = Color.FromArgb(0x6B, 0x76, 0x84);
    private static readonly Color Accent = Color.FromArgb(0x2C, 0x6B, 0xED);      // основной акцент (синий)
    private static readonly Color AccentDark = Color.FromArgb(0x1E, 0x54, 0xC8);
    private static readonly Color AccentSoft = Color.FromArgb(0xE7, 0xEF, 0xFD);  // подсветка выбора/выделения
    private static readonly Color SelBg = Color.FromArgb(0xE4, 0xEE, 0xFC);       // выделение строки таблицы
    private static readonly Color ToolHover = Color.FromArgb(0xE7, 0xED, 0xF6);
    private static readonly Color SoftFill = Color.FromArgb(0xEC, 0xF1, 0xF8);    // вторичные кнопки
    private static readonly Color SoftFillHover = Color.FromArgb(0xDF, 0xE7, 0xF2);
    private static readonly Color Success = Color.FromArgb(0x15, 0x80, 0x3D);
    private static readonly Color Warn = Color.FromArgb(0xB4, 0x53, 0x09);
    private static readonly Color Danger = Color.FromArgb(0xD0, 0x2B, 0x2B);

    // Видимый штамп версии плагина. Увеличивайте при каждом изменении UI — по нему сразу
    // видно в заголовке окна, свежая DLL загружена или старая.
    private const string Build = "сборка 20";

    private readonly Font _ui = new("Segoe UI", 9f);
    private readonly Font _uiBold = new("Segoe UI", 9f, FontStyle.Bold);
    private readonly Font _uiSmall = new("Segoe UI", 8.25f);
    private readonly Font _h1 = new("Segoe UI Semibold", 12f, FontStyle.Bold);
    private readonly Font _brandFont = new("Segoe UI Semibold", 12.5f, FontStyle.Bold);
    private readonly Font _navFont = new("Segoe UI", 9.75f);
    private readonly Font _groupFont = new("Segoe UI", 7.75f, FontStyle.Bold);
    // Segoe MDL2 Assets — системный шрифт векторных значков Windows 10/11.
    private readonly Font _iconFont = new("Segoe MDL2 Assets", 13f);
    private readonly Font _navIconFont = new("Segoe MDL2 Assets", 12f);
    private readonly ToolTip _tip = new() { AutoPopDelay = 6000, InitialDelay = 350, ReshowDelay = 100 };

    private readonly PluginContext _ctx;
    private readonly SessionConfig _config = SessionConfig.Load();
    private HeatingModel? _model;
    private SessionOutcome? _outcome;

    // Распространённые имена свойства тепловой нагрузки — резервная цепочка после выбранного пользователем.
    private static readonly string[] DefaultLoadNames =
        { "Q_расч", "Qрасч", "Q", "Тепловая мощность", "Мощность 80/60", "Теплопотери" };

    private readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = PanelBg };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16, 0, 0, 0) };
    private Label? _readyChip;

    // Кастомная навигация: строки с иконкой, сгруппированные по этапам работы. Раздел активен
    // по имени (_currentSection); ListBox заменён на управляемые панели ради иконок, групп и акцента.
    private string _currentSection = "Обзор";
    private readonly Dictionary<string, Action<bool>> _navApply = new();

    /// <summary>Описание раздела: группа, имя, значок (Segoe MDL2), подпись под заголовком.</summary>
    private readonly record struct NavItem(string Group, string Name, string Icon, string Subtitle);

    // Значки заданы \uXXXX (а не вставкой PUA-символа) — так они видимы в исходнике и не ломают правки.
    private static readonly NavItem[] NavModel =
    {
        new("Модель",     "Обзор",                  "", "Сводка по загруженной модели"),
        new("Модель",     "Уровни",                 "", "Фильтр расчёта по этажам"),
        new("Модель",     "Карта",                  "", "Подсветка ролей и переназначение"),
        new("Настройка",  "Исходные",               "", "Параметры расчёта и лимиты"),
        new("Настройка",  "Классификатор",          "", "Роли объектов по типам"),
        new("Настройка",  "Сопоставление",          "", "Откуда брать значения расчёта"),
        new("Расчёт",     "Проверка модели",        "", "Что мешает расчёту и как исправить"),
        new("Расчёт",     "Расчёт",                 "", "Расходы, потери, диаметры"),
        new("Расчёт",     "Балансировка",           "", "Клапаны и преднастройки"),
        new("Результат",  "Предпросмотр изменений", "", "Что записать обратно в модель"),
        new("Результат",  "Отчёты и экспорт",       "", "Отчёты, ведомости, пакет сверки"),
        new("Сервис",     "Журнал",                 "", "Протокол всех действий и ошибок"),
        new("Сервис",     "О программе",            "", "Версия, возможности, разработчик"),
    };

    private static NavItem MetaOf(string section)
    {
        foreach (var m in NavModel) if (m.Name == section) return m;
        return new NavItem("", section, "", "");
    }

    // Глобальные обработчики ставятся один раз на процесс: ни одна необработанная ошибка
    // (UI-поток, фоновые задачи, домен) не должна пройти мимо журнала.
    private static bool _globalHandlersWired;

    public MainForm(PluginContext ctx)
    {
        _ctx = ctx;
        RengaHeat.Core.Diagnostics.CoreLog.Sink = UiLog.Write;   // этапы ядра — в общий журнал
        if (!_globalHandlersWired)
        {
            _globalHandlersWired = true;
            try
            {
                Application.ThreadException += (_, e) =>
                    UiLog.Error("НЕОБРАБОТАННАЯ ошибка UI", e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                {
                    if (e.ExceptionObject is Exception ex) UiLog.Error("НЕОБРАБОТАННАЯ ошибка процесса", ex);
                    else UiLog.Write("ОШИБКА", $"Необработанная ошибка процесса: {e.ExceptionObject}");
                };
                System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
                    UiLog.Error("НЕОБРАБОТАННАЯ фоновая ошибка", e.Exception);
            }
            catch { /* обработчики — лучшая попытка */ }
        }
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
        NavigateTo("Обзор");
        // Модель НЕ читаем при открытии: на больших проектах это долго. Инженер сам выбирает,
        // загрузить всё или только выделенное (изолированные уровни), кнопками на панели.
        UiLog.Write("окно", $"Открыто окно плагина ({Build}). Настройки: {SessionConfig.DefaultPath}; " +
                            $"уровней выбрано {_config.SelectedLevels.Count}, ручных назначений " +
                            $"{_config.ObjectRoles.Count + _config.ObjectSides.Count}.");
        FormClosed += (_, _) => UiLog.Write("окно", "Окно плагина закрыто.");
    }

    /// <summary>
    /// ВСЕ ошибки и требующие решения замечания расчёта — в журнал построчно (полные тексты),
    /// предупреждения — по кодам с первым примером. Ничего не остаётся невидимым.
    /// </summary>
    private static void LogOutcomeProblems(SessionOutcome o)
    {
        const int cap = 60;
        var problems = o.AllFindings
            .Where(f => f.Status is FindingStatus.Error or FindingStatus.NeedsDecision)
            .ToList();
        foreach (var f in problems.Take(cap))
            UiLog.Write("ошибка-расчёта", $"[{f.Status}] {f.Code}: {f.Message}" +
                                          (f.ObjectId is null ? "" : $" (объект {f.ObjectId})"));
        if (problems.Count > cap)
            UiLog.Write("ошибка-расчёта", $"…и ещё {problems.Count - cap} (полный список — в «Проверке модели»).");

        foreach (var g in o.AllFindings.Where(f => f.Status == FindingStatus.Warning).GroupBy(f => f.Code))
            UiLog.Write("предупреждение", $"{g.Key} ×{g.Count()}. Пример: {g.First().Message}");
        foreach (var a in o.Provenance.Assumptions)
            UiLog.Write("допущение", a);
    }

    /// <summary>
    /// Авто-расчёт при открытии/обновлении: результат появляется без кликов. Для очень больших
    /// моделей пропускаем (чтобы не подвешивать окно) — там расчёт запускается кнопкой.
    /// </summary>
    private void AutoCalculate()
    {
        var work = WorkingModel();
        if (work is null || work.Objects.Count is 0 or > 20000)
        {
            if (work is { Objects.Count: > 20000 })
                UiLog.Write("расчёт", $"Авто-расчёт пропущен: {work.Objects.Count} объектов (> 20000), запустите ▶ вручную.");
            return;
        }
        try
        {
            Cursor = Cursors.WaitCursor;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _outcome = RunSessionOn(work);
            UiLog.Write("расчёт", $"Авто-расчёт: {sw.Elapsed.TotalSeconds:0.0} с. {OutcomeSummary(_outcome)}");
            LogOutcomeProblems(_outcome);
            UpdateStatus();
        }
        catch (Exception ex) { UiLog.Error("авто-расчёт", ex); /* инженер запустит вручную кнопкой ▶ */ }
        finally { Cursor = Cursors.Default; }
        ShowSection(_currentSection);
    }

    /// <summary>Краткий итог расчёта для журнала: источники, контуры, замечания, готовность.</summary>
    private static string OutcomeSummary(SessionOutcome o)
    {
        var errors = o.AllFindings.Count(f => f.Status == FindingStatus.Error);
        var warnings = o.AllFindings.Count(f => f.Status == FindingStatus.Warning);
        var decisions = o.AllFindings.Count(f => f.Status == FindingStatus.NeedsDecision);
        var codes = string.Join(", ", o.AllFindings
            .GroupBy(f => f.Code).OrderByDescending(g => g.Count()).Take(10)
            .Select(g => $"{g.Key}×{g.Count()}"));
        return $"Источников {o.Topology.Sources.Count}, контуров {o.Results.Count}, " +
               $"фрагментов {o.Topology.Fragments.Count}, автосшивок {o.Stitched.Count}, " +
               $"направлений против потока {o.DirectionAudit.Issues.Count}; " +
               $"ошибок {errors}, предупреждений {warnings}, требуют решения {decisions}; " +
               $"готовность: {(o.IsReady ? "ГОТОВО" : "есть замечания")}. " +
               (codes.Length > 0 ? $"Коды замечаний: {codes}." : "Замечаний нет.");
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
                var selectedModel = _ctx.ReadSelectedModel();
                if (selectedModel.Objects.Count > 0)
                {
                    _model = selectedModel;
                }
                else
                {
                    // Пустое выделение — не затираем прежнюю модель, а предлагаем полную загрузку.
                    Cursor = Cursors.Default;
                    UiLog.Write("диалог", "Показан вопрос: выделение пусто — загрузить всю модель?");
                    var answer = MessageBox.Show(this,
                        "В Renga ничего не выделено.\r\n\r\n" +
                        "Чтобы загрузить только нужное:\r\n" +
                        "   1) изолируйте уровни ОВ (скрыв АР/КЖ);\r\n" +
                        "   2) кликните в окно модели и нажмите Ctrl+A — выделятся видимые объекты;\r\n" +
                        "   3) вернитесь сюда и снова нажмите «Загрузить выделенное».\r\n\r\n" +
                        "Или загрузить всю модель сейчас? Чтение ускорено, а нужные этажи " +
                        "можно отметить в разделе «Уровни».",
                        "RengaHeat", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    UiLog.Write("диалог", $"Ответ инженера: {(answer == DialogResult.Yes ? "Да — читаем всю модель" : "Нет")}.");
                    if (answer != DialogResult.Yes) return;
                    Cursor = Cursors.WaitCursor;
                    _model = _ctx.ReadModel();
                }
            }
            else
            {
                _model = _ctx.ReadModel();
            }
        }
        catch (Exception ex)
        {
            _model = null;
            UiLog.Error("загрузка модели", ex);
            Msg("Не удалось прочитать модель: " + ex.Message, MessageBoxIcon.Error);
        }
        finally { Cursor = Cursors.Default; }

        if (_model is not null)
            UiLog.Write("модель", $"В плагин загружено: объектов {_model.Objects.Count}, " +
                                  $"связей {_model.Connections.Count}, уровней в сводке {_model.LevelSummary().Count}.");
        UpdateStatus();
        AutoCalculate();
        ShowSection(_currentSection);
    }

    /// <summary>Рабочая модель: исходная, отфильтрованная по выбранным уровням (раздел «Уровни»).</summary>
    private HeatingModel? WorkingModel() =>
        _model?.FilterByLevels(new HashSet<string>(_config.SelectedLevels));

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, BackColor = NavBg };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 236));   // навигация
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));    // контент
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));          // шапка-аппбар
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));          // навигация + контент
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));          // статус-бар

        var header = BuildHeader();
        root.Controls.Add(header, 0, 0);
        root.SetColumnSpan(header, 2);

        root.Controls.Add(BuildNav(), 0, 1);

        // Контент — белая поверхность с тонкой рамкой слева (отделяет от навигации).
        var contentHost = new Panel { Dock = DockStyle.Fill, BackColor = PanelBg };
        contentHost.Paint += (_, e) =>
        {
            using var pen = new Pen(BorderColor);
            e.Graphics.DrawLine(pen, 0, 0, 0, contentHost.Height);   // левая грань
        };
        contentHost.Controls.Add(_content);
        root.Controls.Add(contentHost, 1, 1);

        var status = BuildStatusBar();
        root.Controls.Add(status, 0, 2);
        root.SetColumnSpan(status, 2);

        Controls.Add(root);
    }

    /// <summary>Верхняя панель-аппбар: логотип-плитка, название и кнопки действий справа.</summary>
    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Fill, BackColor = PanelBg };
        header.Paint += (_, e) =>
        {
            using var pen = new Pen(BorderColor);
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };

        // Плитка-логотип с акцентной заливкой и значком.
        var logo = new Panel { Dock = DockStyle.Left, Width = 60, BackColor = PanelBg };
        logo.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rect = new Rectangle(14, 13, 34, 34);
            using var path = RoundedRect(rect, 9);
            using var br = new System.Drawing.Drawing2D.LinearGradientBrush(rect, Accent, AccentDark, 60f);
            g.FillPath(br, path);
            using var f = new Font("Segoe MDL2 Assets", 15f);
            TextRenderer.DrawText(g, "", f, rect, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };

        var titleWrap = new Panel { Dock = DockStyle.Left, Width = 260, BackColor = PanelBg, Padding = new Padding(0, 10, 0, 0) };
        titleWrap.Controls.Add(new Label
        {
            Text = $"Гидравлический расчёт отопления · {Build}", Dock = DockStyle.Top, Height = 18,
            Font = _uiSmall, ForeColor = TextMuted, TextAlign = ContentAlignment.TopLeft,
            Padding = new Padding(3, 1, 0, 0), BackColor = PanelBg,
        });
        titleWrap.Controls.Add(new Label
        {
            Text = "RengaHeat", Dock = DockStyle.Top, Height = 24, Font = _brandFont, ForeColor = TextDark,
            TextAlign = ContentAlignment.BottomLeft, Padding = new Padding(2, 0, 0, 0), BackColor = PanelBg,
        });

        // Кнопки действий справа (значок + подпись). Первичная — акцентная «Рассчитать».
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            BackColor = PanelBg, Padding = new Padding(0, 13, 14, 0), AutoSize = true,
        };
        var runBtn = HeaderButton("", "Рассчитать", primary: true);
        runBtn.Click += (_, _) => { UiLog.Write("клик", "Кнопка «Рассчитать» (шапка)."); RunCalculation(); };
        var loadAllBtn = HeaderButton("", "Вся модель", primary: false);
        loadAllBtn.Click += (_, _) => { UiLog.Write("клик", "Кнопка «Загрузить всю модель»."); LoadModel(selectedOnly: false); };
        var loadSelBtn = HeaderButton("", "Выделенное", primary: false);
        loadSelBtn.Click += (_, _) => { UiLog.Write("клик", "Кнопка «Загрузить выделенное»."); LoadModel(selectedOnly: true); };
        _tip.SetToolTip(loadSelBtn, "Прочитать только выделенное в Renga (изолированные уровни)");
        _tip.SetToolTip(loadAllBtn, "Прочитать всю модель проекта");
        _tip.SetToolTip(runBtn, "Выполнить гидравлический расчёт");
        actions.Controls.Add(runBtn);
        actions.Controls.Add(loadAllBtn);
        actions.Controls.Add(loadSelBtn);

        header.Controls.Add(actions);
        header.Controls.Add(titleWrap);
        header.Controls.Add(logo);
        return header;
    }

    /// <summary>Кнопка действия в шапке: значок + подпись, скруглённая; primary — акцентная заливка.</summary>
    private Button HeaderButton(string glyph, string text, bool primary)
    {
        var b = new Button
        {
            Text = "      " + text, Font = _uiBold, Height = 34, AutoSize = false, Width = 134,
            FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Margin = new Padding(8, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft, UseCompatibleTextRendering = false, TabStop = false,
        };
        b.FlatAppearance.BorderSize = 0;
        if (primary)
        {
            b.BackColor = Accent; b.ForeColor = Color.White;
            b.FlatAppearance.MouseOverBackColor = AccentDark;
            b.FlatAppearance.MouseDownBackColor = AccentDark;
        }
        else
        {
            b.BackColor = SoftFill; b.ForeColor = TextDark;
            b.FlatAppearance.MouseOverBackColor = SoftFillHover;
            b.FlatAppearance.MouseDownBackColor = BorderStrong;
        }
        // Значок рисуем поверх, слева, шрифтом Segoe MDL2 — значок и текст в одной кнопке.
        b.Paint += (_, e) =>
        {
            var col = primary ? Color.White : Accent;
            var rect = new Rectangle(14, 0, 20, b.Height);
            TextRenderer.DrawText(e.Graphics, glyph, _iconFont, rect, col,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };
        Round(b, 8);
        return b;
    }

    /// <summary>Панель навигации: группы этапов и строки-разделы со значками и акцентом выбора.</summary>
    private Control BuildNav()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = SurfaceAlt, AutoScroll = true };
        host.Paint += (_, e) =>
        {
            using var pen = new Pen(BorderColor);
            e.Graphics.DrawLine(pen, host.Width - 1, 0, host.Width - 1, host.Height);   // правая грань
        };
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = SurfaceAlt, Padding = new Padding(0, 6, 0, 10),
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        string? lastGroup = null;
        foreach (var item in NavModel)
        {
            if (item.Group != lastGroup)
            {
                lastGroup = item.Group;
                stack.Controls.Add(new Label
                {
                    Text = item.Group.ToUpperInvariant(), Dock = DockStyle.Top, Height = 26,
                    Font = _groupFont, ForeColor = TextMuted, BackColor = SurfaceAlt,
                    TextAlign = ContentAlignment.BottomLeft, Padding = new Padding(18, 0, 0, 4),
                });
            }
            stack.Controls.Add(BuildNavRow(item));
        }
        host.Controls.Add(stack);
        return host;
    }

    private Control BuildNavRow(NavItem item)
    {
        var row = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = SurfaceAlt, Cursor = Cursors.Hand };
        var icon = new Label
        {
            Text = item.Icon, Font = _navIconFont, Dock = DockStyle.Left, Width = 42,
            ForeColor = TextMuted, TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.Transparent,
            UseCompatibleTextRendering = false,
        };
        var text = new Label
        {
            Text = item.Name, Font = _navFont, Dock = DockStyle.Fill,
            ForeColor = TextDark, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent,
        };

        var selected = false;
        var hover = false;
        void Style()
        {
            row.BackColor = selected ? AccentSoft : hover ? ToolHover : SurfaceAlt;
            text.ForeColor = selected ? AccentDark : TextDark;
            text.Font = selected ? _uiBold : _navFont;
            icon.ForeColor = selected ? Accent : TextMuted;
            row.Invalidate();
        }
        row.Paint += (_, e) =>
        {
            if (!selected) return;
            using var br = new SolidBrush(Accent);
            e.Graphics.FillRectangle(br, 0, 6, 3, row.Height - 12);   // акцентная полоса слева
        };

        void OnEnter(object? s, EventArgs e) { if (!selected) { hover = true; Style(); } }
        void OnLeave(object? s, EventArgs e) { hover = false; Style(); }
        void OnClick(object? s, EventArgs e) => NavigateTo(item.Name);
        foreach (Control c in new Control[] { row, icon, text })
        {
            c.MouseEnter += OnEnter;
            c.MouseLeave += OnLeave;
            c.Click += OnClick;
        }

        row.Controls.Add(text);
        row.Controls.Add(icon);
        _navApply[item.Name] = sel => { selected = sel; hover = false; Style(); };
        return row;
    }

    /// <summary>Переключиться на раздел: подсветить строку навигации, записать в журнал, показать содержимое.</summary>
    private void NavigateTo(string section)
    {
        _currentSection = section;
        foreach (var (name, apply) in _navApply) apply(name == section);
        UiLog.Write("раздел", $"Открыт раздел «{section}».");
        ShowSection(section);
    }

    /// <summary>Нижний статус-бар: индикатор готовности слева, сводка модели, кнопка «Закрыть».</summary>
    private Control BuildStatusBar()
    {
        var bottom = new Panel { Dock = DockStyle.Fill, BackColor = PanelBg };
        bottom.Paint += (_, e) =>
        {
            using var pen = new Pen(BorderColor);
            e.Graphics.DrawLine(pen, 0, 0, bottom.Width, 0);
        };
        _status.Font = _ui;
        _status.ForeColor = TextMuted;

        var closeBtn = new Button
        {
            Text = "Закрыть", Width = 96, Height = 26, FlatStyle = FlatStyle.Flat, Font = _ui,
            Dock = DockStyle.Right, BackColor = SoftFill, ForeColor = TextDark, Cursor = Cursors.Hand, TabStop = false,
        };
        closeBtn.FlatAppearance.BorderSize = 0;
        closeBtn.FlatAppearance.MouseOverBackColor = SoftFillHover;
        Round(closeBtn, 6);
        closeBtn.Click += (_, _) => Close();
        var closeHost = new Panel { Dock = DockStyle.Right, Width = 116, Padding = new Padding(8, 6, 12, 6), BackColor = PanelBg };
        closeHost.Controls.Add(closeBtn);

        _readyChip = new Label
        {
            Dock = DockStyle.Left, Width = 176, Font = _uiBold, TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = TextMuted, BackColor = PanelBg, Text = "● расчёт не выполнялся",
        };

        // Порядок докинга: растягивающийся _status добавляем первым (index 0 — раскладывается
        // последним и занимает остаток), затем пристыкованные к краям индикатор и кнопка.
        bottom.Controls.Add(_status);
        bottom.Controls.Add(_readyChip);
        bottom.Controls.Add(closeHost);
        return bottom;
    }

    /// <summary>Обернуть контрол в белую панель с тонкой рамкой на фоне-подложке.</summary>
    private static Panel Framed(Control inner, Padding outerMargin)
    {
        inner.Dock = DockStyle.Fill;
        var box = new Panel { Dock = DockStyle.Fill, BackColor = PanelBg, Padding = new Padding(1) };
        box.Paint += (_, e) => e.Graphics.DrawRectangle(new Pen(BorderColor), 0, 0, box.Width - 1, box.Height - 1);
        box.Controls.Add(inner);
        var host = new Panel { Dock = DockStyle.Fill, BackColor = PanelBg, Padding = outerMargin };
        host.Controls.Add(box);
        return host;
    }

    /// <summary>Скруглённый прямоугольник (для кнопок и карточек).</summary>
    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        if (d <= 0 || d > r.Width || d > r.Height) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Скруглить углы контрола обрезкой региона (пересчитывается при изменении размера).</summary>
    private static void Round(Control c, int radius)
    {
        void Apply()
        {
            if (c.Width <= 0 || c.Height <= 0) return;
            using var path = RoundedRect(new Rectangle(0, 0, c.Width, c.Height), radius);
            c.Region = new Region(path);
        }
        c.Resize += (_, _) => Apply();
        Apply();
    }

    private void UpdateStatus()
    {
        var p = EffectiveProfile();
        var over = _config.Overrides.Any ? " · изменён" : "";
        var work = WorkingModel();
        var lvl = _config.SelectedLevels.Count > 0 ? $" · уровней: {_config.SelectedLevels.Count}" : "";
        var model = work is null ? "модель не загружена"
            : $"объектов: {work.Objects.Count} · связей: {work.Connections.Count}{lvl}";
        _status.Text = $"Профиль: {p.Name}{over}  ·  график {p.HeatingSchedule}      {model}";

        if (_readyChip is not null)
        {
            if (_outcome is null)
            {
                _readyChip.Text = "●  расчёт не выполнялся";
                _readyChip.ForeColor = TextMuted;
            }
            else if (_outcome.IsReady)
            {
                _readyChip.Text = "●  готово";
                _readyChip.ForeColor = Success;
            }
            else
            {
                var errors = _outcome.AllFindings.Count(f => f.Status == FindingStatus.Error);
                _readyChip.Text = errors > 0 ? $"●  ошибок: {errors}" : "●  есть замечания";
                _readyChip.ForeColor = errors > 0 ? Danger : Warn;
            }
        }
    }

    private void ShowSection(string section)
    {
        _content.Controls.Clear();
        Control body;
        try
        {
            body = BuildSectionBody(section);
        }
        catch (Exception ex)
        {
            // Ошибка построения раздела не должна ронять окно — и обязана попасть в журнал.
            UiLog.Error($"построение раздела «{section}»", ex);
            body = Info($"Не удалось построить раздел: {ex.Message}\r\n\r\nПодробности (стек) — в разделе «Журнал».");
        }
        body.Dock = DockStyle.Fill;
        body.BackColor = PanelBg;   // единый белый фон содержимого раздела

        var inner = new Panel { Dock = DockStyle.Fill, BackColor = PanelBg, Padding = new Padding(26, 18, 26, 20), AutoScroll = true };
        inner.Controls.Add(body);

        var host = new Panel { Dock = DockStyle.Fill, BackColor = PanelBg };
        host.Controls.Add(inner);
        host.Controls.Add(SectionHeader(section));
        _content.Controls.Add(host);
    }

    private Control BuildSectionBody(string section) =>
        section switch
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
            "Журнал" => BuildLog(),
            "О программе" => BuildAbout(),
            _ => Info("Раздел в разработке."),
        };

    private Panel SectionHeader(string section)
    {
        var meta = MetaOf(section);
        var panel = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = PanelBg, Padding = new Padding(24, 0, 24, 0) };
        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(BorderColor);
            e.Graphics.DrawLine(pen, 0, panel.Height - 1, panel.Width, panel.Height - 1);
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rect = new Rectangle(24, 15, 32, 32);
            using var path = RoundedRect(rect, 8);
            using var br = new SolidBrush(AccentSoft);
            g.FillPath(br, path);
            TextRenderer.DrawText(g, meta.Icon, _iconFont, rect, Accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };
        var title = new Label
        {
            Text = section, Dock = DockStyle.Top, Height = 26, Font = _h1, ForeColor = TextDark,
            TextAlign = ContentAlignment.BottomLeft, Padding = new Padding(46, 0, 0, 0), BackColor = Color.Transparent,
        };
        var sub = new Label
        {
            Text = meta.Subtitle, Dock = DockStyle.Top, Height = 18, Font = _uiSmall, ForeColor = TextMuted,
            TextAlign = ContentAlignment.TopLeft, Padding = new Padding(47, 1, 0, 0), BackColor = Color.Transparent,
        };
        var wrap = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(0, 10, 0, 0) };
        wrap.Controls.Add(sub);
        wrap.Controls.Add(title);
        panel.Controls.Add(wrap);
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
                "   3) нажмите в шапке кнопку «Выделенное».\r\n\r\n" +
                "Либо «Вся модель» — читается весь проект, может быть долго."));
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
            var sw = System.Diagnostics.Stopwatch.StartNew();
            UiLog.Write("расчёт", $"Запуск расчёта: рабочая модель {work.Objects.Count} объектов, " +
                                  $"{work.Connections.Count} связей (выбрано уровней: {_config.SelectedLevels.Count}).");
            _outcome = RunSessionOn(work);
            UiLog.Write("расчёт", $"Расчёт завершён за {sw.Elapsed.TotalSeconds:0.0} с. {OutcomeSummary(_outcome)}");
            LogOutcomeProblems(_outcome);
            UpdateStatus();
            NavigateTo("Расчёт");
        }
        catch (Exception ex)
        {
            UiLog.Error("расчёт", ex);
            Msg("Ошибка расчёта: " + ex.Message, MessageBoxIcon.Error);
        }
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
        ShowSection(_currentSection);
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
        var baseP = _ctx.Profile;      // базовые значения (эталон для колонки «По умолчанию»)
        var rows = InputDescriptors();

        // Шапка: профиль + выбор сценария
        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        header.Controls.Add(new Label { Text = $"Профиль {baseP.Name} (вер. {baseP.Version}) · {baseP.SourceDocument}", AutoSize = true, Font = _ui, ForeColor = TextDark, Margin = new Padding(0, 9, 20, 0) });
        header.Controls.Add(new Label { Text = "Сценарий:", AutoSize = true, Font = _ui, ForeColor = TextDark, Margin = new Padding(0, 9, 4, 0) });
        var scenario = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, Font = _ui, Margin = new Padding(0, 5, 0, 0) };
        foreach (var s in Scenarios) scenario.Items.Add(s.Name);
        scenario.SelectedItem = _config.ScenarioName ?? "Базовый";
        if (scenario.SelectedIndex < 0) scenario.SelectedIndex = 0;
        scenario.SelectedIndexChanged += (_, _) =>
        {
            _config.ScenarioName = scenario.SelectedItem?.ToString();
            _config.Save();
            UiLog.Write("исходные", $"Сценарий → «{_config.ScenarioName}».");
        };
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
        var cBase = new DataGridViewTextBoxColumn { HeaderText = "По умолчанию", ReadOnly = true, FillWeight = 18 };
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
            if (ok)
            {
                _config.Save();
                UiLog.Write("исходные", $"«{rows[idx].Label}» → «{text}».");
            }
            else Msg($"Некорректное значение «{text}» для «{rows[idx].Label}» — оставлено прежнее.", MessageBoxIcon.Warning);
            // Показать нормализованное/унаследованное значение без повторного входа.
            updating = true;
            grid.Rows[e.RowIndex].Cells[cVal.Index].Value = rows[idx].Show(EffectiveProfile());
            updating = false;
        };

        var apply = PrimaryButton("Применить и пересчитать");
        apply.Click += (_, _) => RunCalculation();
        var reset = SecondaryButton("Сбросить значения");
        reset.Click += (_, _) =>
        {
            _config.Overrides = new ProfileOverride();
            _config.Save();
            UiLog.Write("исходные", "Сброс исходных данных к значениям по умолчанию.");
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
            UiLog.Write("уровни", _config.SelectedLevels.Count == 0
                ? "Выбраны все уровни (фильтр выключен)."
                : $"Выбраны уровни: {string.Join(", ", _config.SelectedLevels)}.");
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
        new("Точка трассировки (ввод от ИТП)", ObjectRole.RoutePoint, null),
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
            return Info("Сначала загрузите модель — кнопки «Выделенное» или «Вся модель» в шапке.");
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
            UiLog.Write("карта", $"Назначение «{opt.Label}» применено к {uids.Count} выделенным объектам.");
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
                UiLog.Write("карта", $"Сброшены ручные назначения ({_config.ObjectRoles.Count + _config.ObjectSides.Count}).");
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
            panel.Controls.Add(Info("Группы карты появятся после расчёта — нажмите «Рассчитать» в шапке."));
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
            var capturedName = name;
            btn.Click += (_, _) =>
            {
                UiLog.Write("карта", $"Подсветка группы «{capturedName}» ({captured.Count} объектов).");
                _ctx.SelectManyInRenga!(captured);
            };
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
            UiLog.Write("классификатор", $"Тип {typeS} → роль «{RoleNames.Of(role)}».");
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

    /// <summary>Метка пункта «авто» в выпадающих списках свойств.</summary>
    private const string AutoItem = "— авто —";

    private Control BuildMapping()
    {
        if (_model is null) return Info("Модель не загружена.");

        // Текущая цепочка источников по каждому полю (с учётом выбранных свойств) — для показа резерва.
        var chains = BuildSession().Mappings.Rules
            .GroupBy(r => r.Field.Key)
            .ToDictionary(g => g.Key, g => string.Join(" → ", g.First().SourceChain.Select(s => s.Kind)));

        // Все имена свойств/параметров, реально существующие в модели, — выбор из списка,
        // а не набор вслепую. Пример: создали у радиаторов свойство «Мощность» — выбираете его
        // для поля «Нагрузка прибора», и расчёт берёт число оттуда.
        var propNames = _model.Objects.Values
            .SelectMany(o => o.Properties.Values.Select(p => p.Name)
                .Concat(o.StyleProperties.Values.Select(p => p.Name))
                .Concat(o.Parameters.Keys))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, EditMode = DataGridViewEditMode.EditOnEnter,
        };
        StyleGrid(grid);
        grid.DataError += (_, e) => e.ThrowException = false;   // незнакомое значение не роняет грид
        var cField = new DataGridViewTextBoxColumn { HeaderText = "Расчётное поле", ReadOnly = true, FillWeight = 32 };
        var cProp = new DataGridViewComboBoxColumn { HeaderText = "Свойство-источник", FlatStyle = FlatStyle.Flat, FillWeight = 24 };
        cProp.Items.Add(AutoItem);
        foreach (var n in propNames) cProp.Items.Add(n);
        var cChain = new DataGridViewTextBoxColumn { HeaderText = "Цепочка резерва", ReadOnly = true, FillWeight = 34 };
        var cUnit = new DataGridViewTextBoxColumn { HeaderText = "Ед.", ReadOnly = true, FillWeight = 10 };
        grid.Columns.AddRange(cField, cProp, cChain, cUnit);

        foreach (var f in StandardFields.All)
        {
            var prop = _config.FieldProperties.GetValueOrDefault(f.Key, "");
            if (prop.Length > 0 && !cProp.Items.Contains(prop)) cProp.Items.Add(prop);   // сохранённое, но отсутствующее в модели
            var row = grid.Rows[grid.Rows.Add(f.DisplayName, prop.Length == 0 ? AutoItem : prop,
                chains.GetValueOrDefault(f.Key, "—"), f.BaseUnitSymbol)];
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
            if (string.IsNullOrEmpty(v) || v == AutoItem) { _config.FieldProperties.Remove(key); v = AutoItem; }
            else _config.FieldProperties[key] = v;
            _config.Save();
            UiLog.Write("сопоставление", $"Поле {key} → свойство «{v}».");
        };

        var caption = new Label
        {
            Text = "Откуда брать каждое расчётное значение: выберите свойство из модели (список — реальные " +
                   "свойства объектов). Пример: свойство «Мощность» у радиаторов → поле «Нагрузка прибора».",
            Dock = DockStyle.Fill, ForeColor = TextMuted, Font = _ui, TextAlign = ContentAlignment.MiddleLeft,
        };
        var apply = PrimaryButton("Применить и пересчитать");
        apply.Dock = DockStyle.Fill;
        apply.Click += (_, _) => RunCalculation();

        return VStack(caption, 44, grid, apply, 48);
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

    /// <summary>Что сделать по каждому коду замечания — конкретное действие, а не общие слова.</summary>
    private static readonly (string Prefix, string Advice)[] FindingAdvice =
    {
        ("NET-001", "Сеть распадается на фрагменты: соедините трассы в Renga (или включите/увеличьте автосоединение точек в «Исходных»)."),
        ("NET-002", "У объекта есть неподключённые порты — соедините его с сетью или подтвердите как границу."),
        ("SRC-001", "Источник не найден: назначьте объекту роль «Источник (ИТП)» в «Карте» (или поставьте открытую точку трассировки на вводе)."),
        ("SRC-002", "Несколько источников: проверьте границы зон в «Карте» (группа «Присоединения к ИТП»)."),
        ("SRC-003", "ИТП принят по открытому концу сети (допущение): проверьте точку в «Карте», при необходимости назначьте вручную."),
        ("CLS-001", "Роль объекта не определена: задайте в «Классификаторе» (тип → роль) или выделите объекты в Renga и назначьте в «Карте»."),
        ("CALC", "Не хватает исходных данных: укажите в «Сопоставлении», из какого свойства брать значение (например, «Мощность» для нагрузки)."),
        ("HYD", "Гидравлика не решается на этом кольце: проверьте связность (подача→прибор→обратка) и диаметры участков."),
        ("DIR-101", "Ориентация трассы против потока: расчёт уже использует правильное направление; разверните трассу в Renga (список — «Карта»)."),
        ("DIR", "Направление/сторона не определяется однозначно: назначьте «Сторона: подача/обратка» выделенным объектам в «Карте»."),
        ("CON-101", "Разрыв закрыт автосоединением (допущение): проверьте место в «Карте» (группа «Автосоединённые разрывы»)."),
        ("VEL-001", "Превышена скорость: примите рекомендованный Ду из «Расчёта» → «Участки» или увеличьте диаметр в модели."),
        ("LOSS-001", "Превышены удельные потери: увеличьте диаметр участка (рекомендация — в «Расчёте» → «Участки»)."),
        ("SIZE-002", "Диаметр участка не подходит под расход: см. колонку «Ду реком.» в «Расчёте»."),
        ("BAL-001", "Балансировка: подберите/проверьте клапан и преднастройку в разделе «Балансировка»."),
        ("PUMP-001", "Насос не подобран: проверьте каталог насосов и требуемые расход/напор в «Расчёте»."),
        ("CTU", "Превышен лимит профиля: измените значение лимита в «Исходных» или исправьте модель."),
    };

    private static string AdviceFor(string code)
    {
        foreach (var (prefix, advice) in FindingAdvice)
            if (code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return advice;
        return "См. текст замечания.";
    }

    private Control BuildValidation()
    {
        if (_outcome is null) return Info("Замечания появляются после расчёта. Нажмите «Рассчитать» в шапке.");

        var all = _outcome.AllFindings.ToList();
        if (all.Count == 0) return Info("Замечаний нет — модель и расчёт в порядке.");

        // Сводка-уведомления: по каждому коду — сколько, что это значит и что сделать.
        var groups = all.GroupBy(f => f.Code)
            .OrderBy(g => g.Min(f => (int)f.Status))     // сначала ошибки, затем решения/предупреждения
            .ThenByDescending(g => g.Count())
            .ToList();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"ЧТО НЕ ТАК И ЧТО СДЕЛАТЬ  (всего замечаний: {all.Count})");
        sb.AppendLine(new string('─', 100));
        foreach (var g in groups)
        {
            var f = g.First();
            sb.AppendLine($"{StatusText(f.Status).ToUpperInvariant()} · {g.Key} · {g.Count()} шт.");
            sb.AppendLine($"   Пример: {f.Message}");
            sb.AppendLine($"   Действие: {AdviceFor(g.Key)}");
            sb.AppendLine();
        }
        var summaryBox = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true,
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = PanelBg,
            Font = new Font("Consolas", 8.75f), Text = sb.ToString(),
        };

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
        var caption = new Label
        {
            Text = $"{summary}.{note}  Двойной клик по строке таблицы — показать объект в Renga.",
            Dock = DockStyle.Fill, ForeColor = TextMuted, Font = _ui, TextAlign = ContentAlignment.MiddleLeft,
        };

        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = PanelBg };
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 42));    // сводка-уведомления
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 58));    // полная таблица
        t.Controls.Add(caption, 0, 0);
        t.Controls.Add(Framed(summaryBox, new Padding(0, 0, 0, 6)), 0, 1);
        var grid = MakeGrid(table, "ObjectId");
        grid.Dock = DockStyle.Fill;
        t.Controls.Add(grid, 0, 2);
        return t;
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

            // «Почему это значение?» — журнал происхождения значения по выбранному прибору.
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
        panel.Controls.Add(Info("\r\nПеред выпуском документации проверьте результаты обязательным расчётом " +
                                "в специализированном ПО. Пакет сверки — для проверки методик."));
        return panel;
    }

    /// <summary>
    /// «Журнал»: полный протокол сессии — клики, загрузки, длительности, ответы плагина, диалоги,
    /// ошибки со стеком. Тот же журнал непрерывно пишется в %TEMP%\RengaHeat_ui.log.
    /// </summary>
    private Control BuildLog()
    {
        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = PanelBg,
            Font = new Font("Consolas", 8.75f), Text = UiLog.Snapshot(),
        };
        // Показать хвост журнала (последние события) сразу.
        box.SelectionStart = box.TextLength;
        box.ScrollToCaret();

        var caption = new Label
        {
            Text = $"Записей: {UiLog.Count}. Журнал также пишется в файл: {UiLog.FilePath}",
            Dock = DockStyle.Fill, ForeColor = TextMuted, Font = _ui, TextAlign = ContentAlignment.MiddleLeft,
        };

        var save = PrimaryButton("Сохранить журнал (.txt)");
        save.Click += (_, _) =>
        {
            using var dlg = new SaveFileDialog
            {
                FileName = $"RengaHeat_журнал_{DateTime.Now:yyyy-MM-dd_HH-mm}.txt",
                Filter = "Текст (*.txt)|*.txt",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                System.IO.File.WriteAllText(dlg.FileName, UiLog.Snapshot(), new System.Text.UTF8Encoding(true));
                Msg("Журнал сохранён: " + dlg.FileName);
            }
            catch (Exception ex) { Msg("Ошибка сохранения журнала: " + ex.Message, MessageBoxIcon.Error); }
        };
        var copy = SecondaryButton("Копировать всё");
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(UiLog.Snapshot()); UiLog.Write("журнал", "Журнал скопирован в буфер."); }
            catch (Exception ex) { Msg("Не удалось скопировать: " + ex.Message, MessageBoxIcon.Error); }
        };
        var refresh = SecondaryButton("Обновить");
        refresh.Click += (_, _) =>
        {
            box.Text = UiLog.Snapshot();
            caption.Text = $"Записей: {UiLog.Count}. Журнал также пишется в файл: {UiLog.FilePath}";
            box.SelectionStart = box.TextLength;
            box.ScrollToCaret();
        };
        var clear = SecondaryButton("Очистить");
        clear.Click += (_, _) => { UiLog.Clear(); box.Text = UiLog.Snapshot(); };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        buttons.Controls.Add(save);
        buttons.Controls.Add(copy);
        buttons.Controls.Add(refresh);
        buttons.Controls.Add(clear);

        return VStack(caption, 30, Framed(box, new Padding(0)), buttons, 44);
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
            "• Журнал «Почему это значение?», сквозной журнал действий, отчёты и экспорт CSV"));
        panel.Controls.Add(Card("Файлы",
            $"Настройки: {SessionConfig.DefaultPath}\r\n" +
            "Диагностика: %TEMP%\\RengaHeat_init.log (запуск), %TEMP%\\RengaHeat_types.log (типы модели),\r\n" +
            "%TEMP%\\RengaHeat_ui.log (полный журнал действий — раздел «Журнал»)"));
        panel.Controls.Add(Card("Ограничение",
            "Перед выпуском документации проверьте результаты обязательным расчётом " +
            "в специализированном ПО (например, Sankom/DCad): для сверки методик есть " +
            "пакет сверки в разделе «Отчёты и экспорт»."));
        return panel;
    }

    // ---------- Вспомогательное ----------

    // Акцентная первичная кнопка (заливка синим) — главное действие раздела.
    private Button PrimaryButton(string text)
    {
        var b = new Button
        {
            Text = text, AutoSize = false, Height = 32, Width = 230, Margin = new Padding(0, 0, 8, 0),
            Font = _uiBold, FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.White,
            Cursor = Cursors.Hand, UseCompatibleTextRendering = false, TabStop = false,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = AccentDark;
        b.FlatAppearance.MouseDownBackColor = AccentDark;
        Round(b, 7);
        return b;
    }

    // Вторичная кнопка — мягкая серо-синяя заливка без рамки.
    private Button SecondaryButton(string text)
    {
        var b = new Button
        {
            Text = text, AutoSize = false, Height = 32, Width = 200, Margin = new Padding(0, 0, 8, 0),
            Font = _ui, FlatStyle = FlatStyle.Flat, BackColor = SoftFill, ForeColor = TextDark,
            Cursor = Cursors.Hand, UseCompatibleTextRendering = false, TabStop = false,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = SoftFillHover;
        b.FlatAppearance.MouseDownBackColor = BorderStrong;
        Round(b, 7);
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
        // Мягкая карточка со скруглёнными углами и акцентной точкой у заголовка.
        var card = new Panel { AutoSize = true, BackColor = PanelBg, Margin = new Padding(0, 0, 0, 14), Padding = new Padding(18, 14, 18, 16) };
        card.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, card.Width - 1, card.Height - 1);
            using var path = RoundedRect(r, 10);
            using var fill = new SolidBrush(PanelBg);
            using var pen = new Pen(BorderColor);
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
            using var dot = new SolidBrush(Accent);
            g.FillEllipse(dot, 18, 19, 7, 7);   // акцентная точка у заголовка
        };
        var flow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
        flow.Controls.Add(new Label { Text = title, AutoSize = true, Font = _uiBold, ForeColor = TextDark, Margin = new Padding(14, 0, 0, 6), BackColor = Color.Transparent });
        flow.Controls.Add(new Label { Text = body, AutoSize = true, Font = _ui, ForeColor = TextDark, MaximumSize = new Size(760, 0), BackColor = Color.Transparent });
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
        grid.GridColor = Color.FromArgb(0xEC, 0xEF, 0xF3);
        grid.EnableHeadersVisualStyles = false;   // собственный светлый заголовок
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersHeight = 32;
        grid.ColumnHeadersDefaultCellStyle.Font = _uiBold;
        grid.ColumnHeadersDefaultCellStyle.BackColor = SurfaceAlt;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = TextMuted;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = SurfaceAlt;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = TextMuted;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 0, 8, 0);
        grid.DefaultCellStyle.Font = _ui;
        grid.DefaultCellStyle.ForeColor = TextDark;
        grid.DefaultCellStyle.BackColor = PanelBg;
        grid.DefaultCellStyle.SelectionBackColor = SelBg;
        grid.DefaultCellStyle.SelectionForeColor = TextDark;
        grid.DefaultCellStyle.Padding = new Padding(8, 3, 8, 3);
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(0xFA, 0xFB, 0xFD);   // «зебра»
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.RowHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.AllowUserToResizeRows = false;
        grid.RowTemplate.Height = 26;
        grid.BackColor = PanelBg;
    }

    /// <summary>Пояснительный блок: мягкая подложка со значком «i» — как info-плашка.</summary>
    private Control Info(string text)
    {
        var panel = new Panel { AutoSize = true, BackColor = PanelBg, Margin = new Padding(0, 4, 0, 6), Padding = new Padding(14, 11, 16, 12) };
        panel.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, panel.Width - 1, panel.Height - 1);
            using var path = RoundedRect(r, 9);
            using var fill = new SolidBrush(SurfaceAlt);
            g.FillPath(fill, path);
            using var bar = new SolidBrush(Accent);
            g.FillRectangle(bar, 0, 6, 3, panel.Height - 12);   // акцентная грань слева
        };
        panel.Controls.Add(new Label
        {
            Text = text, AutoSize = true, Font = _ui, ForeColor = TextDark,
            MaximumSize = new Size(780, 0), BackColor = Color.Transparent,
        });
        return panel;
    }

    private void Msg(string text, MessageBoxIcon icon = MessageBoxIcon.Information)
    {
        UiLog.Write("диалог", $"[{icon}] {text.Replace("\r\n", " | ")}");
        MessageBox.Show(this, text, "RengaHeat", MessageBoxButtons.OK, icon);
    }

    /// <summary>Журнал происхождения значений объекта — команда «Почему это значение?».</summary>
    private void ShowProvenance(string objectId)
    {
        if (_outcome is null) return;
        UiLog.Write("клик", $"«Почему это значение?» для объекта {objectId}.");
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
