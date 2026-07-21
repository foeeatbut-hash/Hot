namespace RengaHeat.RengaPlugin;

/// <summary>
/// Сквозной журнал сессии плагина: каждое действие инженера, каждый ответ плагина, длительности
/// операций и ошибки. Показывается в разделе «Журнал» и параллельно дописывается в файл
/// %TEMP%\RengaHeat_ui.log — при сбое или закрытии Renga записи не теряются.
/// </summary>
internal static class UiLog
{
    private static readonly object Gate = new();
    private static readonly List<string> Entries = new();
    private const int Cap = 8000;   // в памяти держим хвост; полный журнал — в файле

    public static string FilePath =>
        Path.Combine(Path.GetTempPath(), "RengaHeat_ui.log");

    /// <summary>Записать событие: категория (клик/загрузка/расчёт/диалог/ошибка…) и суть.</summary>
    public static void Write(string category, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{category}] {message}";
        lock (Gate)
        {
            Entries.Add(line);
            if (Entries.Count > Cap) Entries.RemoveRange(0, Entries.Count - Cap);
            try { File.AppendAllText(FilePath, line + Environment.NewLine); }
            catch { /* журнал не должен ломать работу */ }
        }
    }

    /// <summary>Ошибка с полным стеком — по ней видно точное место сбоя.</summary>
    public static void Error(string context, Exception ex) =>
        Write("ОШИБКА", $"{context}: {ex.GetType().Name}: {ex.Message}\r\n{ex.StackTrace}");

    public static string Snapshot()
    {
        lock (Gate) return string.Join("\r\n", Entries);
    }

    public static int Count
    {
        get { lock (Gate) return Entries.Count; }
    }

    public static void Clear()
    {
        lock (Gate) Entries.Clear();
        Write("журнал", "Журнал очищен инженером.");
    }
}
