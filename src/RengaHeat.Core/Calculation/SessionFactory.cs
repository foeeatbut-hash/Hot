using RengaHeat.Core.Classification;
using RengaHeat.Core.Mapping;
using RengaHeat.Core.Model;
using RengaHeat.Core.Profiles;
using RengaHeat.Core.Rules;

namespace RengaHeat.Core.Calculation;

/// <summary>
/// Собирает готовую к запуску сессию с типовой конфигурацией под профиль.
/// Сопоставление демонстрирует резервные цепочки источников; всё параметризовано и переносимо.
/// </summary>
public static class SessionFactory
{
    /// <summary>
    /// Стандартное сопоставление: нагрузка прибора берётся по цепочке
    /// свойство экземпляра (несколько распространённых имён) → свойство стиля → ручной ввод → ошибка.
    /// Имена свойств здесь — лишь пример конфигурации; в плагине их выбирает инженер в UI.
    /// </summary>
    public static MappingSet DefaultMappings(IEnumerable<string>? loadPropertyNames = null)
    {
        var names = (loadPropertyNames ?? new[]
        {
            "Q_расч", "Qрасч", "Q", "Тепловая мощность", "Мощность 80/60", "Теплопотери",
        }).ToList();

        var mappings = new MappingSet();

        // Нагрузка прибора — резервная цепочка по нескольким возможным именам свойства.
        var loadChain = names
            .Select(ValueSource (n) => new InstancePropertySource(ModelBuilder.PropId(n), n))
            .Concat(names.Select(ValueSource (n) => new StylePropertySource(ModelBuilder.PropId(n), n)))
            .Append(new ManualInputSource())
            .ToList();
        mappings.Add(new MappingRule
        {
            Name = "Нагрузка приборов: Q_расч → стиль → ручной ввод",
            Field = StandardFields.DeviceLoad,
            AppliesToRoles = new[] { ObjectRole.Radiator, ObjectRole.Convector, ObjectRole.TowelRail, ObjectRole.AirHeater },
            SourceChain = loadChain,
            SourceUnitSymbol = "Вт",
            Priority = 10,
        });

        // Длина участка: количество Renga «Длина» → свойство «L».
        mappings.Add(new MappingRule
        {
            Name = "Длина участка: количество → свойство",
            Field = StandardFields.PipeLength,
            AppliesToRoles = Array.Empty<ObjectRole>(),
            SourceChain = new ValueSource[]
            {
                new QuantitySource("Длина"),
                new InstancePropertySource(ModelBuilder.PropId("L"), "L"),
            },
            SourceUnitSymbol = "м",
            Priority = 5,
        });

        // Внутренний диаметр: свойство «d_вн» → каталог по DN разрешается в сессии.
        mappings.Add(new MappingRule
        {
            Name = "Внутренний диаметр: свойство d_вн",
            Field = StandardFields.PipeInnerDiameter,
            AppliesToRoles = Array.Empty<ObjectRole>(),
            SourceChain = new ValueSource[]
            {
                new InstancePropertySource(ModelBuilder.PropId("d_вн"), "d_вн"),
            },
            SourceUnitSymbol = "м",
            Priority = 5,
        });

        // Kv арматуры: свойство «Kv».
        mappings.Add(new MappingRule
        {
            Name = "Kv арматуры: свойство",
            Field = StandardFields.ValveKv,
            AppliesToRoles = new[]
            {
                ObjectRole.BalancingValve, ObjectRole.ThermostaticValve,
                ObjectRole.ShutoffValve, ObjectRole.Strainer, ObjectRole.HeatMeter,
            },
            SourceChain = new ValueSource[] { new InstancePropertySource(ModelBuilder.PropId("Kv"), "Kv") },
            SourceUnitSymbol = "Kv",
            Priority = 5,
        });

        return mappings;
    }

    /// <summary>Базовый классификатор по ролям (в дополнение к ручному назначению и «ОВ_Роль»).</summary>
    public static Classifier DefaultClassifier()
    {
        var c = new Classifier();
        c.AddRule(new RoleRule("Стальные малые трубы = ВГП",
            new RoleCriteria { Category = "Труба", MaxDn = 50 }, ObjectRole.Pipe, 1));
        c.AddRule(new RoleRule("Радиатор по имени",
            new RoleCriteria { NameRegex = @"радиатор|radiator|R-\d" }, ObjectRole.Radiator, 5));
        return c;
    }

    /// <summary>Движок правил, наполненный правилами профиля (лимиты ЧТУ).</summary>
    public static RuleEngine RuleEngineFor(RequirementsProfile profile)
    {
        var engine = new RuleEngine();
        foreach (var rule in profile.ToEngineeringRules())
            engine.Add(rule);
        return engine;
    }

    /// <summary>Полностью собранная сессия для профиля (по умолчанию — только анализ).</summary>
    public static CalculationSession CreateSession(RequirementsProfile profile,
        CalculationScenario? scenario = null, SessionMode mode = SessionMode.AnalysisOnly) => new()
    {
        Profile = profile,
        Mappings = DefaultMappings(),
        Classifier = DefaultClassifier(),
        Rules = RuleEngineFor(profile),
        Scenario = scenario ?? CalculationScenario.Base,
        Mode = mode,
    };
}
