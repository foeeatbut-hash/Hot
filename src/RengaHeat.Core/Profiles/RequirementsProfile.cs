using RengaHeat.Core.Model;
using RengaHeat.Core.Rules;

namespace RengaHeat.Core.Profiles;

/// <summary>Температурный график системы.</summary>
public sealed record TemperatureSchedule(double SupplyC, double ReturnC)
{
    public double DeltaT => SupplyC - ReturnC;
    public double MeanC => (SupplyC + ReturnC) / 2;
    public override string ToString() => $"{SupplyC:0.#}/{ReturnC:0.#} °C";
}

/// <summary>
/// Профиль требований проекта (ЧТУ/организации/заказчика). Все значения — данные, не код:
/// профиль сериализуется, версионируется и переносится на другой корпус.
/// Ключи констант доступны параметризации через источник «Константа профиля».
/// </summary>
public sealed record RequirementsProfile
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string SourceDocument { get; init; } = "";

    public TemperatureSchedule HeatingSchedule { get; init; } = new(80, 60);
    public TemperatureSchedule VentilationSchedule { get; init; } = new(90, 65);

    public int MaxApartmentsPerManifold { get; init; } = 8;
    public int MaxManifoldsPerSection { get; init; } = 2;
    public int MaxDevicesPerHorizontalLoop { get; init; } = 10;
    public int MaxFloorsLowerZone { get; init; } = 18;

    /// <summary>Запас тепловой мощности приборов с терморегуляторами, %.</summary>
    public double PowerMarginThermostaticPercent { get; init; } = 10;
    /// <summary>Запас тепловой мощности приборов технических помещений, %.</summary>
    public double PowerMarginTechnicalPercent { get; init; } = 5;

    /// <summary>Максимальная длина радиатора в квартире, м.</summary>
    public double MaxApartmentRadiatorLengthM { get; init; } = 1.4;

    /// <summary>Максимальный DN стальной ВГП трубы (выше — электросварные).</summary>
    public int MaxVgpDn { get; init; } = 50;

    /// <summary>Максимальный DN поквартирных PE-Xa труб — параметр профиля по ЧТУ.</summary>
    public int MaxApartmentPexDn { get; init; } = 20;

    /// <summary>Требование регулятора перепада давления перед коллекторами.</summary>
    public bool RequireDprBeforeManifold { get; init; } = true;

    /// <summary>Теплосчётчики располагаются на обратном трубопроводе.</summary>
    public bool HeatMeterOnReturn { get; init; } = true;

    /// <summary>Лимиты скорости, м/с (могут переопределяться правилами для МОП и др.).</summary>
    public double MaxVelocityApartmentMS { get; init; } = 0.8;
    public double MaxVelocityMainMS { get; init; } = 1.2;
    /// <summary>Лимит удельных линейных потерь, Па/м.</summary>
    public double MaxSpecificLossPaM { get; init; } = 250;

    /// <summary>Серии труб по умолчанию для ролей (переопределяются правилами).</summary>
    public string SteelSmallSeries { get; init; } = "ВГП ГОСТ 3262";
    public string SteelLargeSeries { get; init; } = "Электросварная ГОСТ 10704";
    public string ApartmentPipeSeries { get; init; } = "PE-Xa EVOH";

    /// <summary>Константы профиля для параметризации (источник «Константа профиля»).</summary>
    public IReadOnlyDictionary<string, double> Constants => new Dictionary<string, double>
    {
        ["profile.heating.supplyC"] = HeatingSchedule.SupplyC,
        ["profile.heating.returnC"] = HeatingSchedule.ReturnC,
        ["profile.ventilation.supplyC"] = VentilationSchedule.SupplyC,
        ["profile.ventilation.returnC"] = VentilationSchedule.ReturnC,
        ["profile.maxApartmentsPerManifold"] = MaxApartmentsPerManifold,
        ["profile.maxManifoldsPerSection"] = MaxManifoldsPerSection,
        ["profile.maxDevicesPerLoop"] = MaxDevicesPerHorizontalLoop,
        ["profile.maxFloorsLowerZone"] = MaxFloorsLowerZone,
        ["profile.powerMarginThermostaticPercent"] = PowerMarginThermostaticPercent,
        ["profile.powerMarginTechnicalPercent"] = PowerMarginTechnicalPercent,
        ["profile.maxApartmentRadiatorLengthM"] = MaxApartmentRadiatorLengthM,
        ["profile.maxVgpDn"] = MaxVgpDn,
        ["profile.maxApartmentPexDn"] = MaxApartmentPexDn,
        ["profile.maxVelocityApartmentMS"] = MaxVelocityApartmentMS,
        ["profile.maxVelocityMainMS"] = MaxVelocityMainMS,
        ["profile.maxSpecificLossPaM"] = MaxSpecificLossPaM,
    };

    /// <summary>
    /// Профиль ЧТУ «Новосаратовка» — значения раздела отопления ЧТУ как данные профиля.
    /// Документ-первоисточник: «ЧТУ Новосаратовка 2.pdf» (раздел отопления).
    /// </summary>
    public static RequirementsProfile Novosaratovka() => new()
    {
        Name = "ЧТУ Новосаратовка",
        Version = "2",
        SourceDocument = "ЧТУ Новосаратовка 2.pdf",
        // Все значения по ЧТУ совпадают со значениями по умолчанию выше:
        // 80/60, 90/65, 8 квартир/коллектор, 2 коллектора/секция, 10 приборов/кольцо,
        // 18 этажей нижней зоны, запасы 10 %/5 %, радиатор ≤ 1400 мм, ВГП до Ду50,
        // регуляторы перепада перед коллекторами, теплосчётчики на обратке.
    };

    /// <summary>Преобразование лимитов профиля в правила движка (конструктор правил).</summary>
    public IEnumerable<EngineeringRule> ToEngineeringRules()
    {
        yield return new EngineeringRule
        {
            Name = $"{Name}: не более {MaxDevicesPerHorizontalLoop} приборов в горизонтальном кольце",
            Action = new RuleAction(RuleActionKind.SetLimit, LimitKeys.MaxDevicesPerLoop, MaxDevicesPerHorizontalLoop),
            Priority = 10,
        };
        yield return new EngineeringRule
        {
            Name = $"{Name}: не более {MaxApartmentsPerManifold} квартир на коллектор",
            TargetRoles = new[] { ObjectRole.SupplyManifold, ObjectRole.ReturnManifold },
            Action = new RuleAction(RuleActionKind.SetLimit, LimitKeys.MaxApartmentsPerManifold, MaxApartmentsPerManifold),
            Priority = 10,
        };
        yield return new EngineeringRule
        {
            Name = $"{Name}: длина радиатора в квартире не более {MaxApartmentRadiatorLengthM * 1000:0} мм",
            TargetRoles = new[] { ObjectRole.Radiator },
            Condition = o => o.Context.Apartment is not null,
            Action = new RuleAction(RuleActionKind.SetLimit, LimitKeys.MaxDeviceLength, MaxApartmentRadiatorLengthM),
            Priority = 10,
        };
        yield return new EngineeringRule
        {
            Name = $"{Name}: лимит скорости в квартирных кольцах {MaxVelocityApartmentMS} м/с",
            TargetRoles = new[] { ObjectRole.Pipe, ObjectRole.ApartmentLoop },
            Condition = o => o.Context.Apartment is not null,
            Action = new RuleAction(RuleActionKind.SetLimit, LimitKeys.MaxVelocity, MaxVelocityApartmentMS),
            Priority = 10,
        };
        yield return new EngineeringRule
        {
            Name = $"{Name}: лимит скорости магистралей и стояков {MaxVelocityMainMS} м/с",
            TargetRoles = new[] { ObjectRole.SupplyMain, ObjectRole.ReturnMain, ObjectRole.Riser },
            Action = new RuleAction(RuleActionKind.SetLimit, LimitKeys.MaxVelocity, MaxVelocityMainMS),
            Priority = 10,
        };
        yield return new EngineeringRule
        {
            Name = $"{Name}: лимит удельных потерь {MaxSpecificLossPaM} Па/м",
            Action = new RuleAction(RuleActionKind.SetLimit, LimitKeys.MaxSpecificLoss, MaxSpecificLossPaM),
            Priority = 10,
        };
        yield return new EngineeringRule
        {
            Name = $"{Name}: поквартирные трубы не более DN{MaxApartmentPexDn}",
            TargetRoles = new[] { ObjectRole.Pipe },
            Condition = o => o.Context.Apartment is not null,
            Action = new RuleAction(RuleActionKind.SetLimit, LimitKeys.MaxPipeDn, MaxApartmentPexDn),
            Priority = 10,
        };
    }
}
