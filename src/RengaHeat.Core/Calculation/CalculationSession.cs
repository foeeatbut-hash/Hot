using RengaHeat.Core.Catalogs;
using RengaHeat.Core.Classification;
using RengaHeat.Core.Mapping;
using RengaHeat.Core.Model;
using RengaHeat.Core.Profiles;
using RengaHeat.Core.Rules;
using RengaHeat.Core.Topology;
using RengaHeat.Core.Validation;

namespace RengaHeat.Core.Calculation;

/// <summary>Режим работы плагина.</summary>
public enum SessionMode
{
    AnalysisOnly,   // по умолчанию: только анализ, модель не меняется
    Audit,          // аудит существующей модели без изменений
    Design,         // проектирование новой системы
    Verify,         // проверка уже рассчитанного проекта
    ExportOnly,     // экспорт в Sankom/DCad и сверка
}

/// <summary>
/// Снимок исходных данных расчёта для воспроизводимости: версия ядра, профиль, каталоги,
/// правила, сценарий, допущения. Любой отчёт восстановим по этому снимку.
/// </summary>
public sealed record CalculationProvenance(
    string EngineVersion,
    string ProfileName, string ProfileVersion, string ProfileSource,
    string ScenarioName,
    IReadOnlyList<CatalogInfo> Catalogs,
    DateTime TimestampUtc,
    IReadOnlyList<string> Assumptions);

/// <summary>Итог работы сессии: результаты по фрагментам, проверки, предпросмотр изменений, происхождение.</summary>
public sealed class SessionOutcome
{
    public required CalculationProvenance Provenance { get; init; }
    public required SessionMode Mode { get; init; }
    public List<CalculationResult> Results { get; } = new();
    public List<Finding> ModelFindings { get; } = new();
    public required TopologyAnalysis Topology { get; init; }
    public required ChangeSet PreviewChanges { get; init; }
    public IReadOnlyList<ProvenanceRecord> ValueJournal { get; init; } = Array.Empty<ProvenanceRecord>();

    /// <summary>
    /// Сеть с критической неоднозначностью не получает статус «готово».
    /// Готово ⇔ нет ошибок/конфликтов, все фрагменты сошлись.
    /// </summary>
    public bool IsReady =>
        !Topology.HasCriticalAmbiguity &&
        ModelFindings.All(f => f.Status is not (FindingStatus.Error or FindingStatus.NeedsDecision)) &&
        Results.Count > 0 &&
        Results.All(r => r.Converged &&
                         r.Findings.All(f => f.Status is not (FindingStatus.Error or FindingStatus.NeedsDecision)));

    public IEnumerable<Finding> AllFindings =>
        ModelFindings.Concat(Results.SelectMany(r => r.Findings));
}

/// <summary>
/// Оркестратор полного цикла: классификация → топология → проверка → расчёт → подбор → предпросмотр.
/// Собирает конфигурацию (профиль, правила, сопоставление, каталоги) и прогоняет пайплайн.
/// По умолчанию не меняет модель: формирует ChangeSet для последующего подтверждения.
/// </summary>
public sealed class CalculationSession
{
    public const string EngineVersion = "0.1.0";

    public required RequirementsProfile Profile { get; init; }
    public required MappingSet Mappings { get; init; }
    public required Classifier Classifier { get; init; }
    public required RuleEngine Rules { get; init; }
    public CalculationScenario Scenario { get; init; } = CalculationScenario.Base;
    public SessionMode Mode { get; init; } = SessionMode.AnalysisOnly;
    public PipeCatalog PipeCatalog { get; init; } = PipeCatalog.CreateDefault();
    public ValveCatalog ValveCatalog { get; init; } = ValveCatalog.CreateDefault();
    public PumpCatalog PumpCatalog { get; init; } = PumpCatalog.CreateDefault();
    public DirectionInference Direction { get; init; } = new();

    /// <summary>Явно выбранная сторона источника (для приборов, соседи которых неизвестны).</summary>
    public ResolutionContext? ResolutionContextOverride { get; init; }

    public SessionOutcome Run(HeatingModel model)
    {
        // 1. Классификация ролей (правила + ОВ_Роль + ручные назначения)
        Classifier.ClassifyAll(model);

        // 2. Топология: связность, стороны, направления, источники
        var topology = Direction.Analyze(model);

        // 3. Контекст разрешения значений (сопоставление)
        var ctx = ResolutionContextOverride ?? new ResolutionContext
        {
            Model = model,
            ProfileConstants = Profile.Constants,
            CatalogLookup = CatalogLookup,
        };
        var resolver = new ValueResolver(Mappings, ctx);

        // 4. Проверка модели
        var validator = new ModelValidator(Profile);
        var modelFindings = validator.Validate(model, topology);

        var assumptions = new List<string>(topology.Notes);
        var changeSet = new ChangeSet();

        // 5. Расчёт по фрагментам от каждого источника
        var engine = new CalculationEngine(Profile, Scenario, PipeCatalog);
        var results = new List<CalculationResult>();
        var supplyC = Profile.HeatingSchedule.SupplyC;
        var returnC = Profile.HeatingSchedule.ReturnC;

        foreach (var source in topology.Sources)
        {
            var fragment = topology.Fragments.FirstOrDefault(f => f.Contains(source.Id));
            if (fragment is null) continue;

            var result = engine.Calculate(model, topology, source, fragment,
                deviceLoad: o => ResolveNumber(resolver, o, StandardFields.DeviceLoad, 0),
                deviceFlow: o => DeviceFlow(resolver, o, supplyC, returnC),
                lengthOf: o => ResolveNumber(resolver, o, StandardFields.PipeLength, 0),
                innerDiameterOf: o => ResolveDiameter(resolver, o),
                roughnessOf: o => { var rv = resolver.Resolve(o, StandardFields.PipeRoughness);
                    return rv.HasValue ? rv.Number!.Value : DefaultRoughness(o); },
                zetaOf: o => ResolveNumber(resolver, o, StandardFields.LocalResistanceZeta, 0),
                kvOf: o => ResolveOptional(resolver, o, StandardFields.ValveKv),
                deviceKvOf: o => ResolveOptional(resolver, o, StandardFields.ValveKv) ?? 2.0,
                valveDnOf: o => o.Ports.Select(p => p.Dn).FirstOrDefault(d => d is not null) ?? 15);

            results.Add(result);

            // Предпросмотр записи результатов (не применяется без подтверждения)
            foreach (var d in result.Devices)
                changeSet.Add(new ModelChange(ChangeKind.SetProperty, d.DeviceId, d.DeviceName,
                    "Расход, кг/ч", null, Math.Round(d.MassFlowKgS * 3600, 1),
                    "Результат гидравлического расчёта", ApiSupported: true));
            foreach (var bal in result.Balancing.Where(b => b.PresetN is not null))
                changeSet.Add(new ModelChange(ChangeKind.SetValvePreset, bal.DeviceObjectId,
                    model.Get(bal.DeviceObjectId).Name, "Преднастройка n", null, bal.PresetN,
                    "Балансировка кольца"));
            // Рекомендуемые диаметры труб по расчёту (подбор по расходу под лимиты СП/профиля).
            foreach (var seg in result.Segments.Where(s => s.DiameterChangeRecommended))
                changeSet.Add(new ModelChange(ChangeKind.SetPipeStyle, seg.ObjectId, seg.ObjectName,
                    "Диаметр (Ду)", seg.CurrentDn, seg.RecommendedDn,
                    $"Подбор по расходу: серия «{seg.RecommendedSeries}», лимиты v≤{seg.VelocityLimitMS:0.##} м/с, R≤{seg.SpecificLossLimitPaM:0} Па/м",
                    ApiSupported: false));
        }

        var outcome = new SessionOutcome
        {
            Mode = Mode,
            Topology = topology,
            PreviewChanges = changeSet,
            ValueJournal = resolver.Journal,
            Provenance = new CalculationProvenance(
                EngineVersion, Profile.Name, Profile.Version, Profile.SourceDocument,
                Scenario.Name,
                new[] { PipeCatalog.Info, ValveCatalog.Info, PumpCatalog.Info },
                DateTime.UtcNow, assumptions),
        };
        outcome.ModelFindings.AddRange(modelFindings);
        outcome.Results.AddRange(results);
        return outcome;
    }

    private object? CatalogLookup(string catalog, string key)
    {
        if (catalog.Contains("труб", StringComparison.OrdinalIgnoreCase))
            return PipeCatalog.Items.FirstOrDefault(i =>
                string.Equals(i.Series, key, StringComparison.OrdinalIgnoreCase))?.InnerDiameterM;
        return null;
    }

    private static double DeviceFlow(ValueResolver resolver, NetworkObject o, double supplyC, double returnC)
    {
        // Приоритет: исходный расход из свойства, иначе расчёт из нагрузки и графика.
        var overridden = resolver.Resolve(o, StandardFields.DeviceFlowOverride);
        if (overridden.HasValue) return overridden.Number!.Value;
        var load = ResolveNumber(resolver, o, StandardFields.DeviceLoad, 0);
        return load > 0 ? Hydraulics.Water.MassFlowFromLoad(load, supplyC, returnC) : 0;
    }

    private double ResolveDiameter(ValueResolver resolver, NetworkObject o)
    {
        var d = resolver.Resolve(o, StandardFields.PipeInnerDiameter);
        if (d.HasValue) return d.Number!.Value;
        // Резерв: диаметр из DN порта по каталогу.
        var dn = o.Ports.Select(p => p.Dn).FirstOrDefault(x => x is not null);
        if (dn is not null)
        {
            var item = PipeCatalog.Items.FirstOrDefault(i => i.Dn == dn);
            if (item is not null) return item.InnerDiameterM;
        }
        return 0.0125; // черновой диаметр по умолчанию (DN15), фиксируется как допущение
    }

    /// <summary>
    /// Шероховатость по материалу, когда не задана явно: поквартирные полимерные трубы (PE-Xa) —
    /// гладкие (7e-6 м), стальные — 0.2 мм (СП/справочные данные для новых ВГП).
    /// </summary>
    private static double DefaultRoughness(NetworkObject o)
    {
        var isPolymer = o.Context.Apartment is not null ||
            (o.Material is { } m && (m.Contains("PE", StringComparison.OrdinalIgnoreCase) ||
                                     m.Contains("полим", StringComparison.OrdinalIgnoreCase) ||
                                     m.Contains("пласт", StringComparison.OrdinalIgnoreCase)));
        return isPolymer ? 7e-6 : 0.0002;
    }

    private static double ResolveNumber(ValueResolver resolver, NetworkObject o, FieldDefinition field, double fallback)
    {
        var v = resolver.Resolve(o, field);
        return v.HasValue ? v.Number!.Value : fallback;
    }

    private static double? ResolveOptional(ValueResolver resolver, NetworkObject o, FieldDefinition field)
    {
        var v = resolver.Resolve(o, field);
        return v.HasValue ? v.Number : null;
    }
}
