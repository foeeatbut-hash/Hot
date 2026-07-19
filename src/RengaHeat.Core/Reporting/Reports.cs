using System.Globalization;
using System.Text;
using RengaHeat.Core.Calculation;
using RengaHeat.Core.Validation;

namespace RengaHeat.Core.Reporting;

/// <summary>Формирование расчётных таблиц, ведомостей, отчёта проверки и пакета сверки.</summary>
public static class Reports
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    /// <summary>CSV-ведомость участков: DN, расход, скорость, потери (разделитель «;»).</summary>
    public static string SegmentsCsv(CalculationResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Участок;Система;Расход_кг/ч;Скорость_м/с;Удельные_Па/м;Длина_м;Линейные_Па;Местные_Па;Реверс");
        foreach (var s in result.Segments)
            sb.AppendLine(string.Join(';',
                Esc(s.ObjectName), Esc(s.Grouping ?? ""),
                F(s.MassFlowKgS * 3600, 1), F(s.VelocityMS, 3), F(s.SpecificLossPaM, 0),
                F(s.LengthM, 2), F(s.LinearLossPa, 0), F(s.MinorLossPa, 0),
                s.FlowReversed ? "да" : "нет"));
        return sb.ToString();
    }

    /// <summary>CSV-таблица приборов и балансировки: нагрузка, расход, перепад, преднастройка n.</summary>
    public static string DevicesCsv(CalculationResult result)
    {
        var balByDevice = result.Balancing.ToDictionary(b => b.DeviceObjectId);
        var sb = new StringBuilder();
        sb.AppendLine("Прибор;Группа;Нагрузка_Вт;Расход_кг/ч;Расход_м3/ч;Располагаемый_Па;" +
                      "ТребуемаяKv;Клапан;Преднастройка_n;Авторитет;Критическое;Уверенность");
        foreach (var d in result.Devices)
        {
            balByDevice.TryGetValue(d.DeviceId, out var bal);
            sb.AppendLine(string.Join(';',
                Esc(d.DeviceName), Esc(d.Grouping ?? ""),
                F(d.LoadW, 0), F(d.MassFlowKgS * 3600, 1), F(d.VolumeFlowM3H, 3),
                F(d.AvailablePressurePa, 0),
                bal is null ? "" : F(bal.RequiredKv, 3),
                bal?.Valve?.Article ?? "",
                bal?.PresetN is { } n ? F(n, 1) : "",
                bal?.ValveAuthority is { } a ? F(a, 2) : "",
                d.DeviceId == result.CriticalRingDeviceId ? "да" : "нет",
                d.Confidence.ToString()));
        }
        return sb.ToString();
    }

    /// <summary>Отчёт проверки модели по статусам.</summary>
    public static string FindingsReport(IEnumerable<Finding> findings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ОТЧЁТ О ПРОВЕРКЕ МОДЕЛИ");
        sb.AppendLine(new string('=', 60));
        foreach (var group in findings.GroupBy(f => f.Status))
        {
            sb.AppendLine($"\n[{group.Key}] — {group.Count()}");
            foreach (var f in group)
                sb.AppendLine($"  {f.Code}: {f.Message}" +
                              (f.Grouping is null ? "" : $" (группа: {f.Grouping})"));
        }
        return sb.ToString();
    }

    /// <summary>Печатный отчёт по сессии — воспроизводимый: включает происхождение и допущения.</summary>
    public static string SessionReport(SessionOutcome outcome)
    {
        var p = outcome.Provenance;
        var sb = new StringBuilder();
        sb.AppendLine("ГИДРАВЛИЧЕСКИЙ РАСЧЁТ СИСТЕМЫ ОТОПЛЕНИЯ");
        sb.AppendLine(new string('=', 60));
        sb.AppendLine($"Ядро расчёта:      {p.EngineVersion}");
        sb.AppendLine($"Профиль:           {p.ProfileName} (вер. {p.ProfileVersion})");
        sb.AppendLine($"Первоисточник:     {p.ProfileSource}");
        sb.AppendLine($"Сценарий:          {p.ScenarioName}");
        sb.AppendLine($"Режим:             {outcome.Mode}");
        sb.AppendLine($"Дата (UTC):        {p.TimestampUtc:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"Статус готовности: {(outcome.IsReady ? "ГОТОВО" : "НЕ ГОТОВО (есть ошибки/неоднозначности)")}");
        sb.AppendLine("\nКаталоги:");
        foreach (var c in p.Catalogs)
            sb.AppendLine($"  - {c.Name} (вер. {c.Version}): {c.SourceDescription}");
        if (p.Assumptions.Count > 0)
        {
            sb.AppendLine("\nДопущения:");
            foreach (var a in p.Assumptions)
                sb.AppendLine($"  - {a}");
        }

        foreach (var r in outcome.Results)
        {
            sb.AppendLine($"\n{new string('-', 60)}");
            sb.AppendLine($"Источник: {r.SourceName}");
            sb.AppendLine($"  Сходимость:        {(r.Converged ? "да" : "нет")} ({r.Iterations} итер.)");
            sb.AppendLine($"  Суммарный расход:  {r.TotalFlowKgS * 3600:0.0} кг/ч");
            sb.AppendLine($"  Требуемый напор:   {r.RequiredHeadPa / 1000:0.00} кПа");
            if (r.CriticalRingDeviceId is { } crit)
                sb.AppendLine($"  Критическое кольцо: {r.Devices.FirstOrDefault(d => d.DeviceId == crit)?.DeviceName ?? crit}");
            if (r.Pump is { Pump: { } pump })
                sb.AppendLine($"  Подобран насос:    {pump.Article} " +
                              $"({r.Pump.DutyFlowM3H:0.00} м³/ч / {r.Pump.DutyHeadKPa:0.0} кПа)");
            else if (r.Pump is not null)
                sb.AppendLine($"  Насос:             не подобран — {r.Pump.Problem}");
        }

        sb.AppendLine($"\n{new string('=', 60)}");
        sb.AppendLine("ВНИМАНИЕ: данный расчёт не является юридической заменой обязательного");
        sb.AppendLine("гидравлического расчёта в Sankom/DCad и согласования производителя арматуры");
        sb.AppendLine("по ЧТУ. Используйте пакет сверки для подтверждения эквивалентности методик.");
        return sb.ToString();
    }

    /// <summary>
    /// Пакет данных для сверки с Sankom/DCad: прозрачные исходные данные и результаты по кольцам,
    /// без выдачи расчёта за юридически обязательный. CSV с ключевыми величинами.
    /// </summary>
    public static string ReconciliationPackage(SessionOutcome outcome)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Пакет сверки RengaHeat ↔ Sankom/DCad");
        sb.AppendLine($"# Профиль: {outcome.Provenance.ProfileName} вер. {outcome.Provenance.ProfileVersion}");
        sb.AppendLine($"# Ядро: {outcome.Provenance.EngineVersion}; сценарий: {outcome.Provenance.ScenarioName}");
        sb.AppendLine("# Назначение: сверка методик, не замена обязательного расчёта.");
        sb.AppendLine("Источник;Прибор;Нагрузка_Вт;Расход_кг/ч;Располагаемый_Па;Преднастройка_n;Критическое");
        foreach (var r in outcome.Results)
        {
            var bal = r.Balancing.ToDictionary(b => b.DeviceObjectId);
            foreach (var d in r.Devices)
                sb.AppendLine(string.Join(';',
                    Esc(r.SourceName), Esc(d.DeviceName),
                    F(d.LoadW, 0), F(d.MassFlowKgS * 3600, 1), F(d.AvailablePressurePa, 0),
                    bal.TryGetValue(d.DeviceId, out var b) && b.PresetN is { } n ? F(n, 1) : "",
                    d.DeviceId == r.CriticalRingDeviceId ? "да" : "нет"));
        }
        return sb.ToString();
    }

    private static string F(double v, int digits) => Math.Round(v, digits).ToString(Ci);
    private static string Esc(string s) => s.Contains(';') || s.Contains('"')
        ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}
