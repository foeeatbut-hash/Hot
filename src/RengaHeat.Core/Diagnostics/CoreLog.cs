namespace RengaHeat.Core.Diagnostics;

/// <summary>
/// Мост диагностики ядра: ядро не зависит от UI, но обязано быть прозрачным. Плагин подставляет
/// Sink (журнал UI), и каждый этап пайплайна расчёта пишет туда подробности. Без Sink вызовы
/// ничего не стоят и ничего не делают — тесты и CLI работают как раньше.
/// </summary>
public static class CoreLog
{
    /// <summary>Приёмник записей: (категория, сообщение). Подставляется плагином.</summary>
    public static Action<string, string>? Sink { get; set; }

    public static void Write(string category, string message)
    {
        try { Sink?.Invoke(category, message); }
        catch { /* диагностика не должна влиять на расчёт */ }
    }
}
