// Точка входа плагина Renga: реализация Renga.IPlugin.
// Регистрирует команду на панели действий Renga, запускает расчётную сессию ядра
// и показывает предпросмотр изменений перед их применением.
//
// Компилируется только при наличии Renga SDK. Без SDK — безопасная заглушка.

using RengaHeat.Core.Calculation;
using RengaHeat.Core.Profiles;
using RengaHeat.Core.Reporting;

namespace RengaHeat.RengaPlugin;

#if !RENGA_SDK_ABSENT

public sealed class Plugin : Renga.IPlugin
{
    private Renga.IApplication? _application;
    private Renga.IUIPanelExtension? _panel;
    private readonly List<Renga.ActionEventSource> _eventSources = new();
    private string _pluginFolder = "";

    public bool Initialize(string pluginFolder)
    {
        _pluginFolder = pluginFolder;
        // Любой сбой на этом этапе Renga показывает как «не удаётся инициализировать модуль».
        // Записываем точную причину (тип, сообщение, стек) в файл рядом с плагином —
        // это избавляет от поиска системного лога Renga при диагностике.
        try
        {
            _application = new Renga.Application();

            var ui = _application.UI;
            var action = ui.CreateAction();
            action.DisplayName = "Гидравлический расчёт отопления";
            action.ToolTip = "RengaHeat: анализ сети, расчёт, подбор и предпросмотр изменений";

            var events = new Renga.ActionEventSource(action);
            events.Triggered += (_, _) => RunCalculation();
            _eventSources.Add(events);

            _panel = ui.CreateUIPanelExtension();
            _panel.AddToolButton(action);
            ui.AddExtensionToPrimaryPanel(_panel);

            Log("Плагин RengaHeat инициализирован успешно.");
            return true;
        }
        catch (Exception ex)
        {
            Log("ОШИБКА инициализации RengaHeat:\r\n" + ex);
            return false; // честно сообщаем Renga о сбое, но причина уже записана в лог
        }
    }

    public void Stop()
    {
        foreach (var source in _eventSources) source.Dispose();
        _eventSources.Clear();
        _panel = null;
        _application = null;
    }

    /// <summary>Диагностический лог рядом с плагином: RengaHeat_init.log.</summary>
    private void Log(string message)
    {
        try
        {
            var path = System.IO.Path.Combine(
                string.IsNullOrEmpty(_pluginFolder) ? AppContext.BaseDirectory : _pluginFolder,
                "RengaHeat_init.log");
            System.IO.File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\r\n");
        }
        catch { /* лог не должен ломать инициализацию */ }
    }

    private void RunCalculation()
    {
        if (_application is null) return;
        try
        {
            if (!_application.HasProject())
            {
                _application.UI.ShowMessageBox(Renga.MessageIcon.MessageIcon_Warning, "RengaHeat",
                    "Откройте проект Renga с системой отопления перед запуском расчёта.");
                return;
            }
            var gateway = new RengaModelGateway(_application);
            var model = gateway.ReadModel();

            // Профиль по умолчанию — ЧТУ Новосаратовки; в UI плагина он выбирается/редактируется.
            var session = SessionFactory.CreateSession(RequirementsProfile.Novosaratovka());
            var outcome = session.Run(model);

            // По умолчанию — режим «только анализ»: показываем отчёт и предпросмотр,
            // ничего не применяя. Применение — отдельная подтверждаемая команда.
            var report = Reports.SessionReport(outcome);
            ShowReport(report, outcome);
        }
        catch (Exception ex)
        {
            // Пишем ПОЛНЫЙ стек в лог, чтобы видеть точное место сбоя, а не только текст.
            Log("ОШИБКА расчёта RengaHeat:\r\n" + ex);
            try
            {
                _application.UI.ShowMessageBox(Renga.MessageIcon.MessageIcon_Error, "RengaHeat",
                    "Ошибка расчёта: " + ex.Message +
                    "\r\n\r\nПодробности (стек вызовов) записаны в файл RengaHeat_init.log " +
                    "в папке плагина.");
            }
            catch { /* не даём вторичному сбою UI перекрыть исходную ошибку */ }
        }
    }

    private void ShowReport(string report, SessionOutcome outcome)
    {
        // Здесь открывается диалог плагина с вкладками (см. раздел «Интерфейс» в README):
        // проверка модели, расчёт, балансировка, предпросмотр изменений, отчёты/экспорт.
        // Диалог WPF/WinForms реализуется отдельно; ядро уже отдаёт все данные:
        //   outcome.ModelFindings, outcome.Results, outcome.PreviewChanges, outcome.ValueJournal.
        _application?.UI.ShowMessageBox(
            Renga.MessageIcon.MessageIcon_Info,
            "RengaHeat — результат",
            $"{(outcome.IsReady ? "Готово" : "Есть замечания")}. " +
            $"Замечаний: {outcome.AllFindings.Count()}, изменений в предпросмотре: {outcome.PreviewChanges.Changes.Count}.");
    }
}

#else

/// <summary>Заглушка точки входа для сред без Renga SDK.</summary>
public static class Plugin
{
    public const string Note =
        "Renga SDK не подключён. Точка входа плагина активна только при сборке с Renga SDK.";
}

#endif
