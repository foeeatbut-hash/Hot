namespace RengaHeat.Core.Hydraulics;

/// <summary>Методика определения коэффициента гидравлического трения λ (выбор — параметр сценария).</summary>
public enum FrictionMethod
{
    Churchill,      // формула Черчилля: непрерывна во всех режимах, метод по умолчанию
    ColebrookWhite, // Кольбрук–Уайт (итерационно), турбулентный режим
    Altshul,        // Альтшуль (упрощённая отечественная практика)
}

/// <summary>Гидравлическое трение: Рейнольдс, λ, потери по Дарси–Вейсбаху.</summary>
public static class Friction
{
    public const double LaminarReynoldsLimit = 2300;

    public static double Reynolds(double velocityMS, double innerDiameterM, double kinematicViscosity) =>
        Math.Abs(velocityMS) * innerDiameterM / kinematicViscosity;

    public static double FrictionFactor(FrictionMethod method, double reynolds, double relativeRoughness)
    {
        if (reynolds < 1e-9) return 0;
        return method switch
        {
            FrictionMethod.Churchill => Churchill(reynolds, relativeRoughness),
            FrictionMethod.ColebrookWhite => reynolds < LaminarReynoldsLimit
                ? 64 / reynolds
                : ColebrookWhite(reynolds, relativeRoughness),
            FrictionMethod.Altshul => reynolds < LaminarReynoldsLimit
                ? 64 / reynolds
                : 0.11 * Math.Pow(relativeRoughness + 68 / reynolds, 0.25),
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        };
    }

    /// <summary>Формула Черчилля (1977): справедлива для ламинарного, переходного и турбулентного режимов.</summary>
    private static double Churchill(double re, double relRough)
    {
        var a = Math.Pow(2.457 * Math.Log(1.0 / (Math.Pow(7.0 / re, 0.9) + 0.27 * relRough)), 16);
        var b = Math.Pow(37530.0 / re, 16);
        return 8 * Math.Pow(Math.Pow(8.0 / re, 12) + 1.0 / Math.Pow(a + b, 1.5), 1.0 / 12);
    }

    /// <summary>Кольбрук–Уайт, решается итерациями простой подстановки по 1/√λ.</summary>
    private static double ColebrookWhite(double re, double relRough)
    {
        var x = 1.0 / Math.Sqrt(0.02); // старт от λ = 0.02
        for (var i = 0; i < 50; i++)
        {
            var next = -2 * Math.Log10(relRough / 3.7 + 2.51 * x / re);
            if (Math.Abs(next - x) < 1e-10) { x = next; break; }
            x = next;
        }
        return 1.0 / (x * x);
    }

    /// <summary>
    /// Линейные потери давления по Дарси–Вейсбаху: ΔP = λ·(L/d)·(ρ·v²/2), Па.
    /// </summary>
    public static double DarcyWeisbach(double frictionFactor, double lengthM, double innerDiameterM,
        double density, double velocityMS) =>
        frictionFactor * lengthM / innerDiameterM * density * velocityMS * velocityMS / 2;

    /// <summary>Потери на местных сопротивлениях: ΔP = Σζ·(ρ·v²/2), Па.</summary>
    public static double MinorLosses(double zetaSum, double density, double velocityMS) =>
        zetaSum * density * velocityMS * velocityMS / 2;

    /// <summary>
    /// Квадратичное сопротивление ветви R для модели решателя ΔP = R·G·|G| (G — массовый расход, кг/с):
    /// линейное трение (Дарси–Вейсбах) + местные (Σζ) + арматура (Kv), приведённые к G².
    /// λ вычисляется по ФАКТИЧЕСКОМУ расходу (внешняя итерация согласования): при G≈0 берётся
    /// характерная скорость 0.5 м/с, чтобы λ и R не вырождались. В ламинарном режиме λ=64/Re ∝ 1/v,
    /// поэтому R ∝ 1/G и ΔP=R·G² становится линейным по расходу — как и требует физика.
    /// </summary>
    public static double BranchQuadraticResistance(
        double lengthM, double innerDiameterM, double roughnessM, double zetaSum, double? kv,
        double density, double kinematicViscosity, double massFlowKgS, FrictionMethod method)
    {
        var area = Math.PI * innerDiameterM * innerDiameterM / 4;
        var v = massFlowKgS > 1e-9 ? massFlowKgS / (density * area) : 0.5;
        var re = Reynolds(v, innerDiameterM, kinematicViscosity);
        var lambda = FrictionFactor(method, re, roughnessM / innerDiameterM);
        var linearR = lambda * lengthM / innerDiameterM / (2 * density * area * area);
        var minorR = zetaSum / (2 * density * area * area);
        // ΔP[Па]=(Q[м³/ч]/Kv)²·1e5, Q=G/ρ·3600 ⇒ R_Kv=(3600/(ρ·Kv))²·1e5.
        var valveR = kv is { } k && k > 0 ? Math.Pow(3600.0 / (density * k), 2) * 1e5 : 0;
        return Math.Max(linearR + minorR + valveR, 1e-6);
    }

    /// <summary>
    /// Потери на арматуре по пропускной способности: ΔP[бар] = (Q[м³/ч] / Kv)², результат в Па.
    /// </summary>
    public static double KvPressureDrop(double volumeFlowM3H, double kv)
    {
        if (kv <= 0) throw new ArgumentException("Kv должно быть положительным.");
        var dpBar = Math.Pow(volumeFlowM3H / kv, 2);
        return dpBar * 1e5;
    }

    /// <summary>Требуемая Kv для заданного расхода и располагаемого перепада: Kv = Q/√ΔP[бар].</summary>
    public static double RequiredKv(double volumeFlowM3H, double pressureDropPa)
    {
        if (pressureDropPa <= 0)
            throw new ArgumentException("Перепад давления должен быть положительным.");
        return volumeFlowM3H / Math.Sqrt(pressureDropPa / 1e5);
    }

    public static double Velocity(double massFlowKgS, double density, double innerDiameterM)
    {
        var area = Math.PI * innerDiameterM * innerDiameterM / 4;
        return massFlowKgS / (density * area);
    }

    /// <summary>Гидростатическое давление столба: ΔP = ρ·g·Δh, Па (разные отметки).</summary>
    public static double Hydrostatic(double density, double elevationDeltaM) =>
        density * 9.80665 * elevationDeltaM;
}
