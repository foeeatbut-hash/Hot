using RengaHeat.Core.Catalogs;

namespace RengaHeat.Core.Hydraulics;

/// <summary>Ограничения подбора диаметра (из профиля/правил; могут отличаться для квартир и МОП).</summary>
public sealed record SizingConstraints(
    double MaxVelocityMS,
    double MaxSpecificLossPaM,
    int? MaxDn = null,
    int? FixedDn = null);

public sealed record PipeSizingResult(
    PipeSeriesItem? Pipe,
    double VelocityMS,
    double SpecificLossPaM,
    bool Satisfied,
    string? Conflict);

/// <summary>Итерационный подбор диаметра трубы по каталогу под ограничения скорости и удельных потерь.</summary>
public static class PipeSizing
{
    public static PipeSizingResult SelectDiameter(
        PipeCatalog catalog, string series, double massFlowKgS, double meanTemperatureC,
        SizingConstraints constraints, FrictionMethod method = FrictionMethod.Churchill) =>
        SelectDiameter(catalog.BySeries(series).ToList(), massFlowKgS, meanTemperatureC, constraints, method,
            $"серии «{series}»");

    /// <summary>
    /// Подбор по произвольному списку типоразмеров (объединённые серии — например, стальные ВГП
    /// до Ду50 и электросварные выше). Берётся наименьший диаметр, где скорость и удельные потери
    /// в пределах лимитов; при нулевом расходе — наименьший типоразмер. Ряд сортируется по d_вн.
    /// </summary>
    public static PipeSizingResult SelectDiameter(
        IReadOnlyList<PipeSeriesItem> candidateList, double massFlowKgS, double meanTemperatureC,
        SizingConstraints constraints, FrictionMethod method = FrictionMethod.Churchill,
        string? seriesLabel = null)
    {
        var candidates = candidateList.OrderBy(i => i.InnerDiameterM).ToList();
        var label = seriesLabel ?? "каталога";
        if (candidates.Count == 0)
            return new PipeSizingResult(null, 0, 0, false, $"Нет типоразмеров {label}.");

        if (constraints.FixedDn is { } fixedDn)
        {
            var fixedPipe = candidates.FirstOrDefault(c => c.Dn == fixedDn);
            if (fixedPipe is null)
                return new PipeSizingResult(null, 0, 0, false,
                    $"Зафиксированный DN{fixedDn} отсутствует у {label}.");
            var (v, r) = Evaluate(fixedPipe, massFlowKgS, meanTemperatureC, method);
            var ok = v <= constraints.MaxVelocityMS && r <= constraints.MaxSpecificLossPaM;
            return new PipeSizingResult(fixedPipe, v, r, ok,
                ok ? null : $"DN{fixedDn} зафиксирован, но нарушает лимиты (v={v:0.00} м/с, R={r:0} Па/м).");
        }

        // Нулевой расход (обесточенная ветвь/ноль нагрузки): диаметр по формуле не определить —
        // берём наименьший типоразмер, отметив что подбор не по лимитам.
        if (massFlowKgS <= 1e-9)
            return new PipeSizingResult(candidates[0], 0, 0, true, null);

        foreach (var pipe in candidates)
        {
            if (constraints.MaxDn is { } maxDn && pipe.Dn > maxDn) break;
            var (v, r) = Evaluate(pipe, massFlowKgS, meanTemperatureC, method);
            if (v <= constraints.MaxVelocityMS && r <= constraints.MaxSpecificLossPaM)
                return new PipeSizingResult(pipe, v, r, true, null);
        }

        var last = constraints.MaxDn is { } cap
            ? candidates.LastOrDefault(c => c.Dn <= cap) ?? candidates[^1]
            : candidates[^1];
        var (lv, lr) = Evaluate(last, massFlowKgS, meanTemperatureC, method);
        return new PipeSizingResult(last, lv, lr, false,
            $"Ни один типоразмер {label}" +
            (constraints.MaxDn is { } m ? $" (до DN{m})" : "") +
            $" не проходит по лимитам: наибольший DN{last.Dn} даёт v={lv:0.00} м/с, R={lr:0} Па/м.");
    }

    public static (double VelocityMS, double SpecificLossPaM) Evaluate(
        PipeSeriesItem pipe, double massFlowKgS, double meanTemperatureC, FrictionMethod method)
    {
        var rho = Water.Density(meanTemperatureC);
        var nu = Water.KinematicViscosity(meanTemperatureC);
        var d = pipe.InnerDiameterM;
        var v = Friction.Velocity(massFlowKgS, rho, d);
        var re = Friction.Reynolds(v, d, nu);
        var lambda = Friction.FrictionFactor(method, re, pipe.RoughnessM / d);
        var rPaM = Friction.DarcyWeisbach(lambda, 1.0, d, rho, v);
        return (v, rPaM);
    }
}

public sealed record BalancingResult(
    string DeviceObjectId,
    double AvailablePressurePa,     // располагаемый перепад на кольце прибора
    double ExcessPressurePa,        // избыток, который должен погасить клапан
    double RequiredKv,
    BalancingValveModel? Valve,
    double? PresetN,
    double? ValveAuthority,
    bool IsCriticalRing,
    string? Problem);

/// <summary>
/// Балансировка колец: определение критического кольца, подбор преднастройки n
/// и проверка авторитета клапана.
/// </summary>
public static class Balancing
{
    /// <summary>Минимальный рекомендуемый авторитет клапана (обычно 0.3–0.5).</summary>
    public const double MinRecommendedAuthority = 0.3;

    /// <summary>
    /// Для каждого прибора: располагаемый перепад из решателя, собственное сопротивление прибора —
    /// избыток гасится балансировочным клапаном. Критическое кольцо — с минимальным избытком.
    /// </summary>
    public static IReadOnlyList<BalancingResult> BalanceRings(
        IReadOnlyList<(string DeviceId, double MassFlowKgS, double AvailablePa, double DeviceOwnDropPa, int ValveDn)> rings,
        ValveCatalog valves, double meanTemperatureC)
    {
        if (rings.Count == 0) return Array.Empty<BalancingResult>();

        var rho = Water.Density(meanTemperatureC);
        var excesses = rings.ToDictionary(r => r.DeviceId, r => r.AvailablePa - r.DeviceOwnDropPa);
        var criticalId = excesses.MinBy(kv => kv.Value).Key;

        var results = new List<BalancingResult>();
        // Порог «около-критического» кольца: гасить почти нечего, клапан оставляем открытым.
        const double negligibleExcessPa = 5;

        foreach (var ring in rings)
        {
            var excess = excesses[ring.DeviceId];
            var isCritical = ring.DeviceId == criticalId;
            var volumeFlowM3H = ring.MassFlowKgS / rho * 3600;

            if (ring.AvailablePa + 1e-6 < ring.DeviceOwnDropPa)
            {
                // Даже без клапана прибор не обеспечен напором — это дефицит насоса, а не балансировки.
                results.Add(new BalancingResult(ring.DeviceId, ring.AvailablePa, excess, 0,
                    null, null, null, isCritical,
                    $"Располагаемый перепад {ring.AvailablePa:0} Па меньше потерь прибора " +
                    $"{ring.DeviceOwnDropPa:0} Па — не хватает напора насоса, увеличьте расчётный напор."));
                continue;
            }

            // Крупнейший клапан своего DN — точка отсчёта; при малом избытке оставляем его открытым.
            var largest = valves.ByDn(ring.ValveDn).MaxBy(v => v.MaxKv);
            if (largest is null)
            {
                results.Add(new BalancingResult(ring.DeviceId, ring.AvailablePa, excess, 0,
                    null, null, null, isCritical,
                    $"В каталоге нет балансировочного клапана DN{ring.ValveDn}."));
                continue;
            }

            if (excess <= negligibleExcessPa || isCritical)
            {
                // Около-критическое кольцо: клапан полностью открыт, дросселирование минимально.
                var maxPreset = largest.PresetKv.Max(p => p.Preset);
                results.Add(new BalancingResult(ring.DeviceId, ring.AvailablePa, Math.Max(excess, 0),
                    largest.MaxKv, largest, maxPreset, isCritical ? 0 : Math.Round(excess / Math.Max(ring.AvailablePa, 1e-9), 3),
                    isCritical, null));
                continue;
            }

            var requiredKv = Friction.RequiredKv(volumeFlowM3H, excess);
            // Клапан должен создать дроп = excess при расходе кольца: нужна Kv ≤ requiredKv,
            // берём наибольшую доступную Kv, не превышающую требуемую (наиболее открытый вариант).
            var valve = valves.ByDn(ring.ValveDn).OrderBy(v => v.MaxKv)
                            .LastOrDefault(v => v.PresetKv.Min(p => p.Kv) <= requiredKv)
                        ?? largest;
            var targetKv = Math.Min(requiredKv, valve.MaxKv);
            var preset = valve.PresetForKv(targetKv) ?? valve.PresetKv.Max(p => p.Preset);
            var authority = excess / Math.Max(ring.AvailablePa, 1e-9);
            results.Add(new BalancingResult(ring.DeviceId, ring.AvailablePa, excess, requiredKv,
                valve, preset, Math.Round(authority, 3), isCritical,
                authority < MinRecommendedAuthority
                    ? $"Авторитет клапана {authority:0.00} ниже рекомендуемого {MinRecommendedAuthority}."
                    : null));
        }
        return results;
    }
}

public sealed record PumpSelectionResult(
    double DutyFlowM3H, double DutyHeadKPa, PumpModel? Pump, string? Problem);

/// <summary>Подбор насоса по расчётной точке (расход + напор) с запасом по напору.</summary>
public static class PumpSelection
{
    public static PumpSelectionResult Select(
        PumpCatalog catalog, double massFlowKgS, double requiredHeadPa,
        double meanTemperatureC, double headMarginFraction = 0.1)
    {
        var rho = Water.Density(meanTemperatureC);
        var flowM3H = massFlowKgS / rho * 3600;
        var headKPa = requiredHeadPa * (1 + headMarginFraction) / 1000;

        var pump = catalog.Items
            .Where(p => flowM3H >= p.MinFlowM3H && flowM3H <= p.MaxFlowM3H && headKPa <= p.MaxHeadKPa)
            .MinBy(p => p.MaxHeadKPa);

        return new PumpSelectionResult(Math.Round(flowM3H, 3), Math.Round(headKPa, 2), pump,
            pump is null
                ? $"В каталоге нет насоса на точку {flowM3H:0.00} м³/ч / {headKPa:0.0} кПа."
                : null);
    }
}
