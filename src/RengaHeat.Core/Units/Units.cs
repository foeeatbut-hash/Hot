namespace RengaHeat.Core.Units;

/// <summary>Физическая размерность расчётного поля.</summary>
public enum Dimension
{
    Dimensionless,
    Power,          // базовая единица: Вт
    MassFlow,       // кг/с
    VolumeFlow,     // м³/с
    Pressure,       // Па
    Length,         // м
    Velocity,       // м/с
    Temperature,    // °C (интервалы — К)
    Area,           // м²
    Kv,             // м³/ч при 1 бар
    SpecificLoss,   // Па/м
    Money,          // руб.
}

/// <summary>
/// Единица измерения: имя, размерность и линейное преобразование к базовой единице
/// (base = raw * Factor + Offset). Offset нужен только температурным шкалам.
/// </summary>
public sealed record Unit(string Symbol, Dimension Dimension, double Factor, double Offset = 0.0)
{
    public double ToBase(double value) => value * Factor + Offset;
    public double FromBase(double value) => (value - Offset) / Factor;
    public override string ToString() => Symbol;
}

/// <summary>Реестр единиц. Расширяем: профиль проекта может регистрировать свои единицы.</summary>
public sealed class UnitRegistry
{
    private readonly Dictionary<string, Unit> _units = new(StringComparer.OrdinalIgnoreCase);

    public static UnitRegistry Default { get; } = CreateDefault();

    public void Register(Unit unit) => _units[unit.Symbol] = unit;

    public Unit Get(string symbol) =>
        _units.TryGetValue(symbol, out var u)
            ? u
            : throw new KeyNotFoundException($"Единица «{symbol}» не зарегистрирована.");

    public bool TryGet(string symbol, out Unit unit) => _units.TryGetValue(symbol, out unit!);

    private static UnitRegistry CreateDefault()
    {
        var r = new UnitRegistry();
        r.Register(new Unit("-", Dimension.Dimensionless, 1));
        r.Register(new Unit("Вт", Dimension.Power, 1));
        r.Register(new Unit("кВт", Dimension.Power, 1000));
        r.Register(new Unit("ккал/ч", Dimension.Power, 1.163));
        r.Register(new Unit("кг/с", Dimension.MassFlow, 1));
        r.Register(new Unit("кг/ч", Dimension.MassFlow, 1.0 / 3600));
        r.Register(new Unit("т/ч", Dimension.MassFlow, 1000.0 / 3600));
        r.Register(new Unit("м³/с", Dimension.VolumeFlow, 1));
        r.Register(new Unit("м³/ч", Dimension.VolumeFlow, 1.0 / 3600));
        r.Register(new Unit("л/с", Dimension.VolumeFlow, 1e-3));
        r.Register(new Unit("Па", Dimension.Pressure, 1));
        r.Register(new Unit("кПа", Dimension.Pressure, 1e3));
        r.Register(new Unit("бар", Dimension.Pressure, 1e5));
        r.Register(new Unit("м вод. ст.", Dimension.Pressure, 9806.65));
        r.Register(new Unit("м", Dimension.Length, 1));
        r.Register(new Unit("мм", Dimension.Length, 1e-3));
        r.Register(new Unit("см", Dimension.Length, 1e-2));
        r.Register(new Unit("м/с", Dimension.Velocity, 1));
        r.Register(new Unit("°C", Dimension.Temperature, 1));
        r.Register(new Unit("К", Dimension.Temperature, 1, -273.15));
        r.Register(new Unit("м²", Dimension.Area, 1));
        r.Register(new Unit("Kv", Dimension.Kv, 1));
        r.Register(new Unit("Па/м", Dimension.SpecificLoss, 1));
        r.Register(new Unit("руб.", Dimension.Money, 1));
        return r;
    }
}

/// <summary>Значение с единицей. Хранится в базовой единице своей размерности.</summary>
public readonly record struct Quantity(double BaseValue, Dimension Dimension)
{
    public static Quantity From(double value, Unit unit) => new(unit.ToBase(value), unit.Dimension);

    public double In(Unit unit)
    {
        if (unit.Dimension != Dimension)
            throw new InvalidOperationException(
                $"Нельзя выразить {Dimension} в единице «{unit.Symbol}» ({unit.Dimension}).");
        return unit.FromBase(BaseValue);
    }
}
