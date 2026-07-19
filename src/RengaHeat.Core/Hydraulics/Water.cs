namespace RengaHeat.Core.Hydraulics;

/// <summary>
/// Свойства воды как теплоносителя. Табличные данные при атмосферном давлении
/// (справочные значения по ГОСТ/справочнику Ривкина–Александрова), линейная интерполяция.
/// </summary>
public static class Water
{
    private static readonly double[] TemperatureC = { 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };

    private static readonly double[] DensityKgM3 =
        { 999.8, 999.7, 998.2, 995.7, 992.2, 988.0, 983.2, 977.8, 971.8, 965.3, 958.4 };

    // Кинематическая вязкость, 1e-6 м²/с
    private static readonly double[] KinematicViscosity1e6 =
        { 1.792, 1.306, 1.006, 0.805, 0.659, 0.556, 0.478, 0.415, 0.365, 0.326, 0.295 };

    // Удельная теплоёмкость, Дж/(кг·К)
    private static readonly double[] SpecificHeatJKgK =
        { 4218, 4192, 4182, 4178, 4178, 4181, 4184, 4190, 4196, 4205, 4216 };

    public static double Density(double tC) => Interpolate(TemperatureC, DensityKgM3, tC);

    public static double KinematicViscosity(double tC) =>
        Interpolate(TemperatureC, KinematicViscosity1e6, tC) * 1e-6;

    public static double SpecificHeat(double tC) => Interpolate(TemperatureC, SpecificHeatJKgK, tC);

    /// <summary>
    /// Массовый расход из тепловой нагрузки и температурного графика:
    /// G = Q / (c · (Tпод − Tобр)), кг/с. Свойства берутся при средней температуре.
    /// </summary>
    public static double MassFlowFromLoad(double loadW, double supplyC, double returnC)
    {
        var dt = supplyC - returnC;
        if (dt <= 0)
            throw new ArgumentException(
                $"Температурный график некорректен: Tпод={supplyC} °C, Tобр={returnC} °C.");
        var tMean = (supplyC + returnC) / 2;
        return loadW / (SpecificHeat(tMean) * dt);
    }

    private static double Interpolate(double[] xs, double[] ys, double x)
    {
        if (x <= xs[0]) return ys[0];
        if (x >= xs[^1]) return ys[^1];
        for (var i = 1; i < xs.Length; i++)
        {
            if (x > xs[i]) continue;
            var t = (x - xs[i - 1]) / (xs[i] - xs[i - 1]);
            return ys[i - 1] + t * (ys[i] - ys[i - 1]);
        }
        return ys[^1];
    }
}
