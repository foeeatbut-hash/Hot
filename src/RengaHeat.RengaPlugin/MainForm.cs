using System.Data;
using System.Drawing;
using System.Windows.Forms;
using RengaHeat.Core.Calculation;
using RengaHeat.Core.Model;
using RengaHeat.Core.Reporting;
using RengaHeat.Core.Validation;

namespace RengaHeat.RengaPlugin;

/// <summary>
/// Главное окно плагина: хаб с навигацией по разделам. По открытию читает модель (быстро),
/// но НЕ считает — расчёт запускается явной командой в разделе «Обзор».
/// Форма зависит только от ядра RengaHeat.Core и делегатов PluginContext.
/// </summary>
public sealed class MainForm : Form
{
    private readonly PluginContext _ctx;
    private HeatingModel? _model;
    private SessionOutcome? _outcome;

    private readonly ListBox _nav = new();
    private readonly Panel _content = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12) };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0) };

    private static readonly string[] Sections =
    {
        "Обзор", "Профиль", "Классификатор", "Сопоставление",
        "Проверка модели", "Расчёт", "Балансировка", "Предпросмотр изменений", "Отчёты и экспорт",
    };

    public MainForm(PluginContext ctx)
    {
        _ctx = ctx;
        Text = "RengaHeat — гидравлический расчёт отопления";
        Width = 1040;
        Height = 700;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 560);

        BuildLayout();
        TryLoadModel();
        _nav.SelectedIndex = 0; // вызывает ShowSection("Обзор")
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _status.Font = new Font(FontFamily.GenericSansSerif, 9.5f);
        _status.BackColor = Color.FromArgb(238, 242, 248);
        root.Controls.Add(_status, 0, 0);
        root.SetColumnSpan(_status, 2);

        _nav.Dock = DockStyle.Fill;
        _nav.Font = new Font(FontFamily.GenericSansSerif, 10.5f);
        _nav.IntegralHeight = false;
        _nav.Items.AddRange(Sections);
        _nav.SelectedIndexChanged += (_, _) =>
        {
            if (_nav.SelectedItem is string s) ShowSection(s);
        };
        root.Controls.Add(_nav, 0, 1);
        root.Controls.Add(_content, 1, 1);
        Controls.Add(root);
    }

    private void TryLoadModel()
    {
        try
        {
            _model = _ctx.ReadModel();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            _model = null;
            _status.Text = "Не удалось прочитать модель: " + ex.Message;
        }
    }

    private void UpdateStatus()
    {
        var profile = $"Профиль: {_ctx.Profile.Name} (вер. {_ctx.Profile.Version}), график {_ctx.Profile.HeatingSchedule}";
        var model = _model is null ? "модель не загружена" : $"объектов в модели: {_model.Objects.Count}";
        var ready = _outcome is null ? "расчёт не выполнялся"
            : (_outcome.IsReady ? "статус: ГОТОВО" : "статус: есть замечания");
        _status.Text = $"{profile}    |    {model}    |    {ready}";
    }

    private void ShowSection(string section)
    {
        _content.Controls.Clear();
        var control = section switch
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
        control.Dock = DockStyle.Fill;
        _content.Controls.Add(control);
    }

    // ---------- Разделы ----------

    private Control BuildOverview()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoScroll = true };
        panel.Controls.Add(Header("Обзор модели"));

        if (_model is null)
        {
            panel.Controls.Add(Info("Модель не загружена. Откройте проект Renga и переоткройте плагин."));
            return panel;
        }

        var byType = _model.Objects.Values
            .GroupBy(o => string.IsNullOrEmpty(o.RengaTypeId) ? "(тип не задан)" : o.RengaTypeId)
            .OrderByDescending(g => g.Count())
            .Take(20)
            .Select(g => $"   {g.Key}: {g.Count()}");
        panel.Controls.Add(Info(
            $"Всего объектов: {_model.Objects.Count}\r\n" +
            $"Соединений: {_model.Connections.Count}\r\n\r\n" +
            "Топ типов объектов:\r\n" + string.Join("\r\n", byType)));

        var runBtn = new Button
        {
            Text = "▶  Рассчитать",
            Height = 40, Width = 200,
            Font = new Font(FontFamily.GenericSansSerif, 11f, FontStyle.Bold),
            BackColor = Color.FromArgb(46, 125, 50), ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
            Margin = new Padding(3, 12, 3, 6),
        };
        runBtn.Click += (_, _) => RunCalculation();
        panel.Controls.Add(runBtn);

        panel.Controls.Add(Info(
            "Расчёт выполняется по текущему профилю и настройкам классификатора/сопоставления.\r\n" +
            "Если роли объектов не распознаны, сначала настройте разделы «Классификатор» и «Сопоставление» —\r\n" +
            "иначе появится много замечаний «роль не определена»."));

        if (_outcome is not null)
        {
            var errors = _outcome.AllFindings.Count(f => f.Status == FindingStatus.Error);
            var decisions = _outcome.AllFindings.Count(f => f.Status == FindingStatus.NeedsDecision);
            panel.Controls.Add(Info(
                $"\r\nПоследний расчёт:\r\n" +
                $"   источников (ИТП): {_outcome.Topology.Sources.Count}\r\n" +
                $"   рассчитано контуров: {_outcome.Results.Count}\r\n" +
                $"   ошибок: {errors}, требуют решения: {decisions}\r\n" +
                $"   готовность: {(_outcome.IsReady ? "ГОТОВО" : "есть замечания")}"));
        }
        return panel;
    }

    private void RunCalculation()
    {
        if (_model is null) { MessageBox.Show("Модель не загружена."); return; }
        try
        {
            Cursor = Cursors.WaitCursor;
            _outcome = _ctx.RunSession(_model);
            UpdateStatus();
            MessageBox.Show(this,
                $"Расчёт выполнен.\r\nКонтуров: {_outcome.Results.Count}, замечаний: {_outcome.AllFindings.Count()}.\r\n" +
                "Перейдите в разделы «Проверка модели», «Расчёт», «Балансировка».",
                "RengaHeat", MessageBoxButtons.OK, MessageBoxIcon.Information);
            ShowSection("Расчёт");
            _nav.SelectedItem = "Расчёт";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Ошибка расчёта: " + ex.Message, "RengaHeat",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { Cursor = Cursors.Default; }
    }

    private Control BuildProfile()
    {
        var p = _ctx.Profile;
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        panel.Controls.Add(Header($"Профиль требований: {p.Name} (вер. {p.Version})"));
        panel.Controls.Add(Info(
            $"Источник: {p.SourceDocument}\r\n\r\n" +
            $"График отопления:            {p.HeatingSchedule}\r\n" +
            $"График теплоснабжения вент.: {p.VentilationSchedule}\r\n\r\n" +
            $"Не более квартир на коллектор:      {p.MaxApartmentsPerManifold}\r\n" +
            $"Не более коллекторов в секции:      {p.MaxManifoldsPerSection}\r\n" +
            $"Не более приборов в кольце:         {p.MaxDevicesPerHorizontalLoop}\r\n" +
            $"Этажей нижней зоны (макс.):         {p.MaxFloorsLowerZone}\r\n" +
            $"Запас мощности (терморег.):         {p.PowerMarginThermostaticPercent} %\r\n" +
            $"Запас мощности (техпомещения):      {p.PowerMarginTechnicalPercent} %\r\n" +
            $"Длина радиатора в квартире (макс.): {p.MaxApartmentRadiatorLengthM * 1000:0} мм\r\n" +
            $"Сталь ВГП до Ду{p.MaxVgpDn}, выше — электросварные\r\n" +
            $"Поквартирные PE-Xa до Ду{p.MaxApartmentPexDn}\r\n" +
            $"Регулятор перепада перед коллектором: {(p.RequireDprBeforeManifold ? "требуется" : "нет")}\r\n" +
            $"Теплосчётчик на обратке:             {(p.HeatMeterOnReturn ? "да" : "нет")}\r\n\r\n" +
            $"Лимит скорости (квартиры/магистрали): {p.MaxVelocityApartmentMS} / {p.MaxVelocityMainMS} м/с\r\n" +
            $"Лимит удельных потерь:                {p.MaxSpecificLossPaM} Па/м"));
        panel.Controls.Add(Info("\r\nРедактирование профиля и создание собственных профилей — в следующей версии UI."));
        return panel;
    }

    private Control BuildClassifier()
    {
        if (_outcome is null)
            return Info("Роли определяются при расчёте. Нажмите «Рассчитать» в разделе «Обзор».");
        if (_model is null) return Info("Модель не загружена.");

        var table = new DataTable();
        table.Columns.Add("Объект");
        table.Columns.Add("Тип Renga");
        table.Columns.Add("Роль");
        table.Columns.Add("Источник роли");
        table.Columns.Add("ObjectId");
        foreach (var o in _model.Objects.Values.OrderBy(o => o.Role.Role == ObjectRole.Unknown ? 0 : 1))
            table.Rows.Add(o.Name, o.RengaTypeId ?? "", RoleNames.Of(o.Role.Role), o.Role.Source.ToString(), o.Id);

        var unknown = _model.Objects.Values.Count(o => o.Role.Role == ObjectRole.Unknown);
        return WithGrid($"Классификатор ролей. Не определено ролей: {unknown} из {_model.Objects.Count}. " +
                        "Двойной клик — показать объект в Renga.", table, "ObjectId");
    }

    private Control BuildMapping()
    {
        var mappings = SessionFactory.DefaultMappings();
        var table = new DataTable();
        table.Columns.Add("Расчётное поле");
        table.Columns.Add("Роли");
        table.Columns.Add("Цепочка источников");
        table.Columns.Add("Единица");
        foreach (var r in mappings.Rules)
            table.Rows.Add(
                r.Field.DisplayName,
                r.AppliesToRoles.Count == 0 ? "все" : string.Join(", ", r.AppliesToRoles.Select(RoleNames.Of)),
                string.Join(" → ", r.SourceChain.Select(s => s.Kind)),
                r.SourceUnitSymbol ?? r.Field.BaseUnitSymbol);
        return WithGrid("Сопоставление: откуда ядро берёт значения. Значения ищутся по цепочке " +
                        "(если первый источник пуст — берётся следующий). Выбор конкретных свойств вашей " +
                        "модели — в следующей версии UI.", table, null);
    }

    private Control BuildValidation()
    {
        if (_outcome is null)
            return Info("Замечания появляются после расчёта. Нажмите «Рассчитать» в разделе «Обзор».");

        var all = _outcome.AllFindings.ToList();
        var table = new DataTable();
        table.Columns.Add("Статус");
        table.Columns.Add("Код");
        table.Columns.Add("Сообщение");
        table.Columns.Add("Группа");
        table.Columns.Add("ObjectId");
        // Ограничиваем вывод, чтобы грид не «завис» на десятках тысяч строк.
        const int cap = 3000;
        foreach (var f in all.OrderBy(f => f.Status).Take(cap))
            table.Rows.Add(StatusText(f.Status), f.Code, f.Message, f.Grouping ?? "", f.ObjectId ?? "");

        var summary = string.Join("   ", all.GroupBy(f => f.Status)
            .Select(g => $"{StatusText(g.Key)}: {g.Count()}"));
        var note = all.Count > cap ? $" (показаны первые {cap} из {all.Count})" : "";
        return WithGrid($"Проверка модели. {summary}.{note} Двойной клик — показать объект в Renga.",
            table, "ObjectId");
    }

    private Control BuildCalculation()
    {
        if (_outcome is null)
            return Info("Результаты появляются после расчёта. Нажмите «Рассчитать» в разделе «Обзор».");
        if (_outcome.Results.Count == 0)
            return Info("Расчёт не дал результатов: не найден источник (ИТП) или во фрагменте нет приборов. " +
                        "Проверьте роли в «Классификаторе» и замечания в «Проверке модели».");

        var tabs = new TabControl { Dock = DockStyle.Fill };
        foreach (var r in _outcome.Results)
        {
            var page = new TabPage(r.SourceName);
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 90 };

            var crit = r.Devices.FirstOrDefault(d => d.DeviceId == r.CriticalRingDeviceId)?.DeviceName ?? "—";
            split.Panel1.Controls.Add(Info(
                $"Суммарный расход: {r.TotalFlowKgS * 3600:0.0} кг/ч    " +
                $"Требуемый напор: {r.RequiredHeadPa / 1000:0.00} кПа    " +
                $"Критическое кольцо: {crit}\r\n" +
                $"Насос: {(r.Pump?.Pump is { } pump ? $"{pump.Article} ({r.Pump.DutyFlowM3H:0.00} м³/ч / {r.Pump.DutyHeadKPa:0.0} кПа)" : "не подобран")}    " +
                $"Сходимость: {(r.Converged ? "да" : "нет")}"));

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
            split.Panel2.Controls.Add(MakeGrid(devTable, "ObjectId"));

            page.Controls.Add(split);
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
        table.Columns.Add("Прибор (ObjectId)");
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
        return WithGrid("Балансировка колец: преднастройка n балансировочных клапанов и авторитет.",
            table, "ObjectId");
    }

    private Control BuildPreview()
    {
        if (_outcome is null) return Info("Изменения формируются после расчёта.");
        var changes = _outcome.PreviewChanges.Changes;
        if (changes.Count == 0)
            return Info("Нет предлагаемых изменений. Изменения появляются, когда включена запись результатов " +
                        "и настроены свойства-приёмники (по умолчанию плагин работает в режиме только анализа).");

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, ReadOnly = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        };
        var colApprove = new DataGridViewCheckBoxColumn { HeaderText = "✔", Width = 40, FillWeight = 8 };
        grid.Columns.Add(colApprove);
        grid.Columns.Add("obj", "Объект");
        grid.Columns.Add("target", "Что меняется");
        grid.Columns.Add("old", "Было");
        grid.Columns.Add("new", "Станет");
        grid.Columns.Add("reason", "Основание");
        foreach (var c in grid.Columns) ((DataGridViewColumn)c).ReadOnly = c == grid.Columns[0] ? false : true;
        foreach (var ch in changes)
            grid.Rows.Add(ch.Approved, ch.ObjectName, ch.Target, ch.OldValue?.ToString() ?? "—",
                ch.NewValue?.ToString() ?? "—", ch.Reason);

        var apply = new Button { Text = "Применить отмеченные (с Undo)", Dock = DockStyle.Bottom, Height = 36 };
        apply.Click += (_, _) =>
        {
            if (_ctx.ApplyChanges is null)
            {
                MessageBox.Show(this, "Применение изменений недоступно: включён режим только анализа.", "RengaHeat");
                return;
            }
            var approved = new List<ModelChange>();
            for (var i = 0; i < changes.Count; i++)
                if (grid.Rows[i].Cells[0].Value is true) { changes[i].Approved = true; approved.Add(changes[i]); }
            if (approved.Count == 0) { MessageBox.Show(this, "Не отмечено ни одного изменения."); return; }
            var report = _ctx.ApplyChanges(approved);
            MessageBox.Show(this, report, "RengaHeat — применение", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };

        var host = new Panel { Dock = DockStyle.Fill };
        host.Controls.Add(grid);
        host.Controls.Add(apply);
        var wrap = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        wrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        wrap.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        wrap.Controls.Add(Header("Предпросмотр изменений — применяются только после подтверждения"), 0, 0);
        wrap.Controls.Add(host, 0, 1);
        return wrap;
    }

    private Control BuildReports()
    {
        if (_outcome is null) return Info("Отчёты доступны после расчёта.");
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        panel.Controls.Add(Header("Отчёты и экспорт"));

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

    private Button SaveButton(string text, string defaultName, string filter, Func<string> content)
    {
        var btn = new Button { Text = text, Width = 380, Height = 32, Margin = new Padding(3, 3, 3, 3), TextAlign = ContentAlignment.MiddleLeft };
        btn.Click += (_, _) =>
        {
            using var dlg = new SaveFileDialog { FileName = defaultName, Filter = filter };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                System.IO.File.WriteAllText(dlg.FileName, content(), new System.Text.UTF8Encoding(true));
                MessageBox.Show(this, "Сохранено: " + dlg.FileName, "RengaHeat");
            }
            catch (Exception ex) { MessageBox.Show(this, "Ошибка сохранения: " + ex.Message, "RengaHeat"); }
        };
        return btn;
    }

    private Control WithGrid(string caption, DataTable table, string? idColumn)
    {
        var wrap = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        wrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        wrap.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        wrap.Controls.Add(new Label { Text = caption, Dock = DockStyle.Fill, AutoSize = false }, 0, 0);
        wrap.Controls.Add(MakeGrid(table, idColumn), 0, 1);
        return wrap;
    }

    private DataGridView MakeGrid(DataTable table, string? idColumn)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, DataSource = table, ReadOnly = true, AllowUserToAddRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        };
        grid.DataBindingComplete += (_, _) =>
        {
            if (idColumn is not null && grid.Columns.Contains(idColumn))
                grid.Columns[idColumn].Visible = false;
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

    private static Label Header(string text) => new()
    {
        Text = text, AutoSize = false, Dock = DockStyle.Top, Height = 30,
        Font = new Font(FontFamily.GenericSansSerif, 12f, FontStyle.Bold),
    };

    private static Label Info(string text) => new()
    {
        Text = text, AutoSize = true, Font = new Font(FontFamily.GenericSansSerif, 9.75f),
        Margin = new Padding(3, 6, 3, 6), MaximumSize = new Size(760, 0),
    };

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
