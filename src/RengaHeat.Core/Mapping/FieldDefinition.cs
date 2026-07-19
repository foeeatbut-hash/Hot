using RengaHeat.Core.Units;

namespace RengaHeat.Core.Mapping;

public enum FieldDataType { Number, Text, Boolean, Integer }

/// <summary>Что делать с пустым, нулевым или отрицательным значением.</summary>
public enum InvalidValuePolicy
{
    Error,          // считать ошибкой
    TreatAsMissing, // перейти к следующему источнику цепочки
    AcceptAsIs,     // принять как есть
    ClampToRange,   // привести к границе диапазона (с предупреждением)
}

/// <summary>Действие, если значение не найдено ни в одном источнике цепочки.</summary>
public enum MissingValueAction
{
    Error,              // ошибка расчёта
    Warning,            // предупреждение + объект без значения
    UseDefault,         // подставить значение по умолчанию (фиксируется как допущение)
    ExcludeObject,      // исключить объект из расчёта (в журнал исключений)
    AskEngineer,        // статус «требует решения инженера»
}

/// <summary>
/// Расчётное поле: то, что нужно ядру («Тепловая нагрузка прибора, Вт», «DN», «kv» и т. д.).
/// Поле ничего не знает о конкретных свойствах Renga — привязку задаёт MappingRule.
/// </summary>
public sealed record FieldDefinition
{
    public required string Key { get; init; }               // устойчивый ключ поля, напр. "device.load"
    public required string DisplayName { get; init; }       // «Тепловая нагрузка прибора, Вт»
    public FieldDataType DataType { get; init; } = FieldDataType.Number;
    public Dimension Dimension { get; init; } = Dimension.Dimensionless;
    public string BaseUnitSymbol { get; init; } = "-";      // единица, в которой ядро хранит значение
    public double? Min { get; init; }
    public double? Max { get; init; }
    public int RoundDigits { get; init; } = 6;              // точность округления результата
    public InvalidValuePolicy EmptyPolicy { get; init; } = InvalidValuePolicy.TreatAsMissing;
    public InvalidValuePolicy ZeroPolicy { get; init; } = InvalidValuePolicy.AcceptAsIs;
    public InvalidValuePolicy NegativePolicy { get; init; } = InvalidValuePolicy.Error;
    public MissingValueAction MissingAction { get; init; } = MissingValueAction.Error;
    public double? DefaultValue { get; init; }              // для MissingValueAction.UseDefault
}

/// <summary>Стандартные поля ядра. Список открыт — профиль может добавлять свои.</summary>
public static class StandardFields
{
    public static readonly FieldDefinition DeviceLoad = new()
    {
        Key = "device.load",
        DisplayName = "Тепловая нагрузка прибора, Вт",
        Dimension = Dimension.Power,
        BaseUnitSymbol = "Вт",
        Min = 0, Max = 1_000_000,
        ZeroPolicy = InvalidValuePolicy.TreatAsMissing,
        RoundDigits = 1,
    };

    public static readonly FieldDefinition DeviceFlowOverride = new()
    {
        Key = "device.flow",
        DisplayName = "Расход через прибор (исходный), кг/ч",
        Dimension = Dimension.MassFlow,
        BaseUnitSymbol = "кг/с",
        Min = 0,
        MissingAction = MissingValueAction.Warning, // поле необязательное: расход считается из нагрузки
    };

    public static readonly FieldDefinition PipeLength = new()
    {
        Key = "pipe.length",
        DisplayName = "Длина участка, м",
        Dimension = Dimension.Length,
        BaseUnitSymbol = "м",
        Min = 0, Max = 10_000,
        ZeroPolicy = InvalidValuePolicy.TreatAsMissing,
        RoundDigits = 3,
    };

    public static readonly FieldDefinition PipeInnerDiameter = new()
    {
        Key = "pipe.innerDiameter",
        DisplayName = "Внутренний диаметр, м",
        Dimension = Dimension.Length,
        BaseUnitSymbol = "м",
        Min = 0.003, Max = 1.5,
        RoundDigits = 4,
    };

    public static readonly FieldDefinition PipeRoughness = new()
    {
        Key = "pipe.roughness",
        DisplayName = "Эквивалентная шероховатость, м",
        Dimension = Dimension.Length,
        BaseUnitSymbol = "м",
        Min = 0, Max = 0.01,
        RoundDigits = 7,
    };

    public static readonly FieldDefinition ValveKv = new()
    {
        Key = "valve.kv",
        DisplayName = "Пропускная способность Kv, м³/ч",
        Dimension = Dimension.Kv,
        BaseUnitSymbol = "Kv",
        Min = 0.001, Max = 10_000,
        RoundDigits = 3,
    };

    public static readonly FieldDefinition LocalResistanceZeta = new()
    {
        Key = "element.zeta",
        DisplayName = "Коэффициент местного сопротивления ζ",
        Dimension = Dimension.Dimensionless,
        BaseUnitSymbol = "-",
        Min = 0, Max = 1000,
        RoundDigits = 2,
        MissingAction = MissingValueAction.Warning,
    };

    public static readonly FieldDefinition RadiatorLength = new()
    {
        Key = "device.length",
        DisplayName = "Длина отопительного прибора, м",
        Dimension = Dimension.Length,
        BaseUnitSymbol = "м",
        Min = 0, Max = 10,
        RoundDigits = 3,
        MissingAction = MissingValueAction.Warning,
    };

    public static IReadOnlyList<FieldDefinition> All { get; } = new[]
    {
        DeviceLoad, DeviceFlowOverride, PipeLength, PipeInnerDiameter,
        PipeRoughness, ValveKv, LocalResistanceZeta, RadiatorLength,
    };
}
