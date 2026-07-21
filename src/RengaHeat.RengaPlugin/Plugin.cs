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
    private MainForm? _window;   // единственное немодальное окно плагина

    /// <summary>Обёртка HWND главного окна Renga, чтобы окно плагина было им «владелось».</summary>
    private sealed class RengaOwner(IntPtr handle) : System.Windows.Forms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }

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
        try { _window?.Close(); } catch { /* окно уже закрыто */ }
        _window = null;
        foreach (var source in _eventSources) source.Dispose();
        _eventSources.Clear();
        _panel = null;
        _application = null;
    }

    /// <summary>
    /// Диагностический лог. Пишем во временную папку пользователя (%TEMP%), а не рядом с плагином:
    /// папка плагина обычно в Program Files и недоступна Renga для записи под обычными правами.
    /// Путь: %TEMP%\RengaHeat_init.log.
    /// </summary>
    private void Log(string message)
    {
        try
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "RengaHeat_init.log");
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
                    "Откройте проект Renga с системой отопления перед запуском плагина.");
                return;
            }

            UiLog.Write("renga", "Нажата кнопка плагина на панели Renga.");
            // Одно окно на сессию: повторное нажатие кнопки — просто активирует существующее.
            // Модель НЕ перечитываем автоматически (крупные проекты читаются долго): загрузку
            // запускает сам инженер кнопкой в окне.
            if (_window is { IsDisposed: false })
            {
                _window.WindowState = System.Windows.Forms.FormWindowState.Normal;
                _window.Activate();
                return;
            }

            // Немодальное окно-хаб. Владелец — главное окно Renga (окно поверх Renga, сворачивается
            // вместе с ней), но Renga остаётся интерактивной: можно работать в программе параллельно.
            var gateway = new RengaModelGateway(_application);
            var ctx = new PluginContext
            {
                Profile = RequirementsProfile.Novosaratovka(),
                ReadModel = gateway.ReadModel,
                ReadSelectedModel = gateway.ReadSelected,    // читать только выделенное (изолированные уровни)
                ApplyChanges = null,        // режим только анализа: запись отключена
                SelectInRenga = gateway.SelectByUniqueId,   // двойной клик в таблице — выделить объект в Renga
                SelectManyInRenga = gateway.SelectManyByUniqueId,   // подсветка группы объектов по роли/стороне
                GetSelectedUniqueIds = gateway.GetSelectedUniqueIds, // выделение Renga → переназначение в «Карте»
            };
            _window = new MainForm(ctx);
            _window.FormClosed += (_, _) => _window = null;
            var owner = new RengaOwner((IntPtr)_application.GetMainWindowHandle());
            _window.Show(owner);
        }
        catch (Exception ex)
        {
            UiLog.Error("запуск окна плагина", ex);
            Log("ОШИБКА RengaHeat:\r\n" + ex);
            try
            {
                _application.UI.ShowMessageBox(Renga.MessageIcon.MessageIcon_Error, "RengaHeat",
                    "Ошибка: " + ex.Message +
                    "\r\n\r\nПодробности (стек вызовов) записаны в файл RengaHeat_init.log " +
                    "во временной папке (%TEMP%).");
            }
            catch { /* не даём вторичному сбою UI перекрыть исходную ошибку */ }
        }
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
