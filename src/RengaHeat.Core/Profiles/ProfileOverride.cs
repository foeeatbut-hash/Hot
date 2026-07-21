namespace RengaHeat.Core.Profiles;

/// <summary>
/// Пользовательские исходные данные: переопределения параметров профиля, задаваемые инженером
/// в разделе «Исходные». Хранит только изменённые значения (nullable); незаданные берутся из
/// базового профиля ЧТУ. Сериализуется в config.json и переносится между запусками.
/// </summary>
public sealed class ProfileOverride
{
    public double? HeatingSupplyC { get; set; }
    public double? HeatingReturnC { get; set; }
    public double? VentSupplyC { get; set; }
    public double? VentReturnC { get; set; }

    public int? MaxApartmentsPerManifold { get; set; }
    public int? MaxManifoldsPerSection { get; set; }
    public int? MaxDevicesPerHorizontalLoop { get; set; }
    public int? MaxFloorsLowerZone { get; set; }

    public double? PowerMarginThermostaticPercent { get; set; }
    public double? PowerMarginTechnicalPercent { get; set; }
    public double? MaxApartmentRadiatorLengthM { get; set; }

    public int? MaxVgpDn { get; set; }
    public int? MaxApartmentPexDn { get; set; }

    public bool? RequireDprBeforeManifold { get; set; }
    public bool? HeatMeterOnReturn { get; set; }

    public double? MaxVelocityApartmentMS { get; set; }
    public double? MaxVelocityMainMS { get; set; }
    public double? MaxSpecificLossPaM { get; set; }
    public double? AutoStitchToleranceMm { get; set; }

    /// <summary>Есть ли хотя бы одно переопределение (для отметки «изменено» в UI).</summary>
    public bool Any =>
        HeatingSupplyC is not null || HeatingReturnC is not null || VentSupplyC is not null ||
        VentReturnC is not null || MaxApartmentsPerManifold is not null || MaxManifoldsPerSection is not null ||
        MaxDevicesPerHorizontalLoop is not null || MaxFloorsLowerZone is not null ||
        PowerMarginThermostaticPercent is not null || PowerMarginTechnicalPercent is not null ||
        MaxApartmentRadiatorLengthM is not null || MaxVgpDn is not null || MaxApartmentPexDn is not null ||
        RequireDprBeforeManifold is not null || HeatMeterOnReturn is not null ||
        MaxVelocityApartmentMS is not null || MaxVelocityMainMS is not null ||
        MaxSpecificLossPaM is not null || AutoStitchToleranceMm is not null;

    /// <summary>Применить переопределения к базовому профилю, вернув новый профиль (record with).</summary>
    public RequirementsProfile ApplyTo(RequirementsProfile p) => p with
    {
        HeatingSchedule = new TemperatureSchedule(
            HeatingSupplyC ?? p.HeatingSchedule.SupplyC, HeatingReturnC ?? p.HeatingSchedule.ReturnC),
        VentilationSchedule = new TemperatureSchedule(
            VentSupplyC ?? p.VentilationSchedule.SupplyC, VentReturnC ?? p.VentilationSchedule.ReturnC),
        MaxApartmentsPerManifold = MaxApartmentsPerManifold ?? p.MaxApartmentsPerManifold,
        MaxManifoldsPerSection = MaxManifoldsPerSection ?? p.MaxManifoldsPerSection,
        MaxDevicesPerHorizontalLoop = MaxDevicesPerHorizontalLoop ?? p.MaxDevicesPerHorizontalLoop,
        MaxFloorsLowerZone = MaxFloorsLowerZone ?? p.MaxFloorsLowerZone,
        PowerMarginThermostaticPercent = PowerMarginThermostaticPercent ?? p.PowerMarginThermostaticPercent,
        PowerMarginTechnicalPercent = PowerMarginTechnicalPercent ?? p.PowerMarginTechnicalPercent,
        MaxApartmentRadiatorLengthM = MaxApartmentRadiatorLengthM ?? p.MaxApartmentRadiatorLengthM,
        MaxVgpDn = MaxVgpDn ?? p.MaxVgpDn,
        MaxApartmentPexDn = MaxApartmentPexDn ?? p.MaxApartmentPexDn,
        RequireDprBeforeManifold = RequireDprBeforeManifold ?? p.RequireDprBeforeManifold,
        HeatMeterOnReturn = HeatMeterOnReturn ?? p.HeatMeterOnReturn,
        MaxVelocityApartmentMS = MaxVelocityApartmentMS ?? p.MaxVelocityApartmentMS,
        MaxVelocityMainMS = MaxVelocityMainMS ?? p.MaxVelocityMainMS,
        MaxSpecificLossPaM = MaxSpecificLossPaM ?? p.MaxSpecificLossPaM,
        AutoStitchToleranceMm = AutoStitchToleranceMm ?? p.AutoStitchToleranceMm,
    };
}
