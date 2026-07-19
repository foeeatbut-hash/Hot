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

    public bool Initialize(string pluginFolder)
    {
        _application = new Renga.Application();

        // Регистрируем действие «Гидравлический расчёт отопления».
        var ui = _application.UI;
        var action = ui.CreateAction();
        action.DisplayName = "Гидравлический расчёт отопления";
        action.ToolTip = "RengaHeat: анализ сети, расчёт, подбор и предпросмотр изменений";

        var events = new Renga.ActionEventSource(action);
        events.Triggered += (_, _) => RunCalculation();

        // Размещаем действие на вкладке/панели инструментов (уточните API вашей версии Renga).
        _panel = ui.CreateUIPanelExtension();
        _panel.AddToolButton(action);
        ui.AddExtensionToPrimaryPanel(_panel);

        return true;
    }

    public void Stop()
    {
        _panel = null;
        _application = null;
    }

    private void RunCalculation()
    {
        if (_application is null) return;
        try
        {
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
            _application.UI.ShowMessageBox(
                Renga.MessageIcon.MessageIcon_Error, "RengaHeat", ex.Message);
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
