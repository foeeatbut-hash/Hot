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

    /// <summary>
    /// Базовый авто-классификатор: распознаёт роли по имени объекта, числу портов и DN,
    /// чтобы модель классифицировалась без ручной разметки. Ручное назначение и «ОВ_Роль»
    /// имеют более высокий приоритет; правила типа Renga из UI добавляются с приоритетом выше этих.
    /// </summary>
    public static Classifier DefaultClassifier()
    {
        var c = new Classifier();
        void R(string name, string? nameRegex, ObjectRole role, int prio,
            int? minPorts = null, int? maxDn = null, string? category = null) =>
            c.AddRule(new RoleRule(name, new RoleCriteria
            {
                NameRegex = nameRegex, MinPortCount = minPorts, MaxDn = maxDn, Category = category,
            }, role, prio));

        // Оборудование и приборы (по имени — высокий приоритет)
        R("Насос", @"насос|pump", ObjectRole.Pump, 25);
        R("Источник/ИТП", @"итп|цтп|тепловой пункт|источник тепла|котёл|котел|бойлер|теплообмен", ObjectRole.HeatSource, 25);
        R("Радиатор", @"радиатор|radiator|\bR[-\s]?\d", ObjectRole.Radiator, 20);
        R("Конвектор", @"конвектор|convector", ObjectRole.Convector, 20);
        R("Полотенцесушитель", @"полотенцесуш|полотенце", ObjectRole.TowelRail, 20);
        R("Воздухонагреватель", @"воздухонагрев|калорифер", ObjectRole.AirHeater, 20);

        // Арматура (по имени/артикулу производителей)
        R("Термостатический клапан", @"термостат|термоклапан|\brtr\b|ra-n", ObjectRole.ThermostaticValve, 22);
        R("Балансировочный клапан", @"баланс|\basv\b|leno|штремакс|\bmsv\b|\busv\b", ObjectRole.BalancingValve, 22);
        R("Регулятор перепада", @"регулятор перепад|перепад давл|asv-pv", ObjectRole.DifferentialPressureRegulator, 22);
        R("Фильтр", @"фильтр|грязевик|strainer", ObjectRole.Strainer, 22);
        R("Счётчик", @"счётчик|счетчик|теплосч|meter", ObjectRole.HeatMeter, 22);
        R("Запорная арматура", @"вентиль|задвижк|шаровой|запорн|шаровый|\bball\b|\bvalve\b|\bкран\b", ObjectRole.ShutoffValve, 18);

        // Коллекторы, стояки, магистрали
        R("Коллектор подающий", @"коллектор.*подающ|подающ.*коллектор|гребёнк|гребенк", ObjectRole.SupplyManifold, 12);
        R("Коллектор обратный", @"коллектор.*обратн|обратн.*коллектор", ObjectRole.ReturnManifold, 12);
        R("Стояк", @"стояк|riser", ObjectRole.Riser, 10);
        R("Подающая магистраль", @"подающ|подача|supply", ObjectRole.SupplyMain, 8);
        R("Обратная магистраль", @"обратн|обратка|return", ObjectRole.ReturnMain, 8);

        // Фитинги
        R("Отвод", @"отвод|угольник|elbow|\bbend\b", ObjectRole.Elbow, 6);
        R("Переход", @"переход|reducer|муфта переход", ObjectRole.Reducer, 6);
        R("Тройник (имя)", @"тройник|крестовин|\btee\b", ObjectRole.Tee, 6);
        R("Компенсатор", @"компенсатор|компенс", ObjectRole.Compensator, 6);
        R("Опора", @"опора|support", ObjectRole.FixedSupport, 6);

        // Труба (базовая трубная роль)
        R("Труба (имя)", @"труба|трубопровод|\bpipe\b", ObjectRole.Pipe, 4);
        R("Стальные малые трубы = ВГП", null, ObjectRole.Pipe, 1, maxDn: 50, category: "Труба");

        // Резерв по геометрии: 3+ портов — тройник, если ничего не подошло по имени
        R("Тройник (по портам)", null, ObjectRole.Tee, 2, minPorts: 3);
        return c;
    }

    /// <summary>
    /// Сопоставление с пользовательскими свойствами по каждому полю: для поля, у которого инженер
    /// указал имя свойства, оно ставится в начало цепочки (экземпляр → стиль), затем — стандартный
    /// резерв. Так параметризуется каждый вход расчёта, а не только нагрузка.
    /// </summary>
    public static MappingSet MappingsFor(
        IReadOnlyDictionary<string, string>? fieldProperties,
        IEnumerable<string>? loadPropertyNames = null)
    {
        var loadNames = new List<string>();
        if (fieldProperties is not null &&
            fieldProperties.TryGetValue(StandardFields.DeviceLoad.Key, out var lp) && !string.IsNullOrWhiteSpace(lp))
            loadNames.Add(lp);
        if (loadPropertyNames is not null) loadNames.AddRange(loadPropertyNames);

        var m = DefaultMappings(loadNames.Count > 0 ? loadNames : null);
        if (fieldProperties is null || fieldProperties.Count == 0) return m;

        static ValueSource[] Prepend(string prop) => new ValueSource[]
        {
            new InstancePropertySource(ModelBuilder.PropId(prop), prop),
            new StylePropertySource(ModelBuilder.PropId(prop), prop),
        };

        var result = new MappingSet();
        var handled = new HashSet<string>();
        foreach (var rule in m.Rules)
        {
            // Нагрузка уже учтена через loadNames; остальные поля получают свойство в начало цепочки.
            if (rule.Field.Key != StandardFields.DeviceLoad.Key &&
                fieldProperties.TryGetValue(rule.Field.Key, out var prop) && !string.IsNullOrWhiteSpace(prop))
                result.Add(rule with { SourceChain = Prepend(prop).Concat(rule.SourceChain).ToList() });
            else
                result.Add(rule);
            handled.Add(rule.Field.Key);
        }

        // Поля без стандартного правила (шероховатость, ζ, длина прибора, расход): создаём правило,
        // если инженер задал для них свойство — иначе параметризация таких полей не срабатывала бы.
        foreach (var (key, prop) in fieldProperties)
        {
            if (handled.Contains(key) || string.IsNullOrWhiteSpace(prop)) continue;
            var field = StandardFields.All.FirstOrDefault(f => f.Key == key);
            if (field is null) continue;
            result.Add(new MappingRule
            {
                Field = field,
                AppliesToRoles = Array.Empty<ObjectRole>(),
                SourceChain = Prepend(prop),
                SourceUnitSymbol = field.BaseUnitSymbol,
                Priority = 5,
            });
        }
        return result;
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
