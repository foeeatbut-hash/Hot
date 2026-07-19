using RengaHeat.Core.Hydraulics;
using RengaHeat.Core.Mapping;
using RengaHeat.Core.Model;
using RengaHeat.Core.Profiles;
using RengaHeat.Core.Topology;
using RengaHeat.Core.Validation;

namespace RengaHeat.Core.Calculation;

/// <summary>Расчётный сценарий: лимиты и методики (базовый, экономичный, тихий, ручной, проверка производителя).</summary>
public sealed record CalculationScenario(
    string Name,
    FrictionMethod FrictionMethod = FrictionMethod.Churchill,
    double? VelocityLimitOverrideMS = null,
    double HeadMarginFraction = 0.1)
{
    public static CalculationScenario Base => new("Базовый");
    public static CalculationScenario Economy => new("Экономичный", VelocityLimitOverrideMS: 1.2);
    public static CalculationScenario Quiet => new("Тихий", VelocityLimitOverrideMS: 0.6);
}

/// <summary>Результат гидравлического расчёта одного прибора/кольца.</summary>
public sealed record DeviceResult(
    string DeviceId, string DeviceName, string? Grouping,
    double LoadW, double MassFlowKgS, double VolumeFlowM3H,
    double AvailablePressurePa, double CircuitLossPa,
    bool FlowDirectionReversed, DirectionConfidence Confidence);

/// <summary>Результат по участку трубы.</summary>
public sealed record SegmentResult(
    string ObjectId, string ObjectName, string? Grouping,
    double MassFlowKgS, double VelocityMS, double SpecificLossPaM,
    double LengthM, double LinearLossPa, double MinorLossPa, bool FlowReversed);

/// <summary>Полный результат расчёта фрагмента сети от одного источника.</summary>
public sealed class CalculationResult
{
    public required string SourceId { get; init; }
    public required string SourceName { get; init; }
    public bool Converged { get; init; }
    public int Iterations { get; init; }
    public double TotalFlowKgS { get; init; }
    public double RequiredHeadPa { get; init; }
    public string? CriticalRingDeviceId { get; init; }
    public List<DeviceResult> Devices { get; } = new();
    public List<SegmentResult> Segments { get; } = new();
    public List<BalancingResult> Balancing { get; } = new();
    public PumpSelectionResult? Pump { get; init; }
    public List<Finding> Findings { get; } = new();
}

/// <summary>
/// Расчётное ядро: связывает граф, решатель, балансировку и подбор.
/// Полностью независимо от Renga и COM — работает с HeatingModel и делегатами доступа к значениям.
/// </summary>
public sealed class CalculationEngine(RequirementsProfile profile, CalculationScenario scenario)
{
    private readonly Catalogs.ValveCatalog _valves = Catalogs.ValveCatalog.CreateDefault();
    private readonly Catalogs.PumpCatalog _pumps = Catalogs.PumpCatalog.CreateDefault();

    /// <summary>
    /// Рассчитать один фрагмент сети от заданного источника.
    /// Расходы приборов берутся из <paramref name="deviceFlow"/> (обычно из нагрузки и графика),
    /// геометрия участков — из делегатов доступа (значения разрешены через сопоставление).
    /// </summary>
    public CalculationResult Calculate(
        HeatingModel model, TopologyAnalysis topology, NetworkObject source, IReadOnlySet<string> fragment,
        Func<NetworkObject, double> deviceLoad,
        Func<NetworkObject, double> deviceFlow,
        Func<NetworkObject, double> lengthOf,
        Func<NetworkObject, double> innerDiameterOf,
        Func<NetworkObject, double> roughnessOf,
        Func<NetworkObject, double> zetaOf,
        Func<NetworkObject, double?> kvOf,
        Func<NetworkObject, double> deviceKvOf,
        Func<NetworkObject, int> valveDnOf)
    {
        var builder = new HydraulicNetworkBuilder(
            lengthOf, innerDiameterOf, roughnessOf, zetaOf, kvOf, deviceFlow, deviceKvOf);
        var network = builder.Build(model, topology, source, fragment);

        if (network is null)
            return Failed(source, new Finding(FindingStatus.Error, "CALC-001",
                $"Источник «{source.Name}» имеет менее двух портов — схему построить нельзя.", source.Id));
        if (network.Devices.Count == 0)
            return Failed(source, network.BuildFindings.ToArray());

        var findings = new List<Finding>(network.BuildFindings);
        var tMean = profile.HeatingSchedule.MeanC;
        var rho = Water.Density(tMean);
        var nu = Water.KinematicViscosity(tMean);

        // Сопротивления ветвей R в ΔP = R·G·|G| (линейное трение + местные + арматура по Kv)
        var resistiveBranches = network.Branches
            .Select(b => new ResistiveBranch(b.Object.Id, b.FromNode, b.ToNode, BranchResistance(b, rho, nu)))
            .ToList();

        // Приборы — ветви с фиксированным расходом (инжекция: из подающего узла в обратный).
        // Источник моделируется заземлением обоих его узлов (подача и обратка = 0):
        // подающая и обратная стороны — раздельные резистивные компоненты, у каждого свой опорный узел.
        var fixedFlows = network.Devices
            .Select(d => new FixedFlowBranch(d.Object.Id, d.SupplyNode, d.ReturnNode, d.MassFlowKgS))
            .ToList();

        var references = new[] { network.SourceSupplyNode, network.SourceReturnNode };
        var solve = new HydraulicSolver().Solve(resistiveBranches, fixedFlows, references);
        if (!solve.Converged)
            findings.Add(new Finding(FindingStatus.Warning, "CALC-002",
                $"Гидравлический решатель не сошёлся за {solve.Iterations} итераций у источника «{source.Name}». " +
                "Результат ориентировочный: проверьте связность и сопротивления.", source.Id));

        var segments = new List<SegmentResult>();
        foreach (var b in network.Branches)
        {
            var g = solve.BranchFlows[b.Object.Id];
            var absG = Math.Abs(g);
            var v = Friction.Velocity(absG, rho, b.InnerDiameterM);
            var re = Friction.Reynolds(v, b.InnerDiameterM, nu);
            var lambda = Friction.FrictionFactor(scenario.FrictionMethod, re, b.RoughnessM / b.InnerDiameterM);
            var rPaM = Friction.DarcyWeisbach(lambda, 1.0, b.InnerDiameterM, rho, v);
            var minor = Friction.MinorLosses(b.ZetaSum, rho, v) +
                        (b.Kv is { } kv && kv > 0 ? Friction.KvPressureDrop(absG / rho * 3600, kv) : 0);
            segments.Add(new SegmentResult(b.Object.Id, b.Object.Name, b.Object.Context.SystemName,
                g, v, rPaM, b.LengthM, rPaM * b.LengthM, minor, g < 0));

            if (g < 0)
                findings.Add(new Finding(FindingStatus.Assumption, "DIR-002",
                    $"На участке «{b.Object.Name}» расчётный расход отрицателен: фактическое направление " +
                    "теплоносителя противоположно исходной гипотезе (не ошибка, направление уточнено).",
                    b.Object.Id, b.Object.Context.SystemName));
        }

        // Потери трассы до прибора и собственное сопротивление прибора.
        // Оба узла источника заземлены в 0 ⇒ pathLoss = P[обратка] − P[подача] ≥ 0 — суммарные
        // потери подающей и обратной трассы этого кольца.
        var deviceInfo = network.Devices.Select(d =>
        {
            var pathLoss = solve.NodePressures[d.ReturnNode] - solve.NodePressures[d.SupplyNode];
            var ownDrop = d.OwnKv > 0 ? Friction.KvPressureDrop(d.MassFlowKgS / rho * 3600, d.OwnKv) : 0;
            return (d, pathLoss, ownDrop);
        }).ToList();

        // Требуемый напор насоса = максимум по кольцам (потери трассы + собственное сопротивление прибора).
        var requiredHead = deviceInfo.Max(x => x.pathLoss + x.ownDrop);

        var devices = new List<DeviceResult>();
        var ringInputs = new List<(string, double, double, double, int)>();
        foreach (var (d, pathLoss, ownDrop) in deviceInfo)
        {
            var available = requiredHead - pathLoss; // располагаемое давление на терминалах прибора
            devices.Add(new DeviceResult(d.Object.Id, d.Object.Name,
                d.Object.Context.Apartment ?? d.Object.Context.SystemName,
                deviceLoad(d.Object), d.MassFlowKgS, d.MassFlowKgS / rho * 3600,
                available, ownDrop, pathLoss < 0,
                d.InletAssumed ? DirectionConfidence.Assumed : topology.SideOf(d.Object.Id).Confidence));
            ringInputs.Add((d.Object.Id, d.MassFlowKgS, available, ownDrop, valveDnOf(d.Object)));
        }

        var critical = devices.MinBy(x => x.AvailablePressurePa - x.CircuitLossPa);

        var balancing = Balancing.BalanceRings(ringInputs, _valves, tMean);
        foreach (var bal in balancing.Where(b => b.Problem is not null))
            findings.Add(new Finding(FindingStatus.Warning, "BAL-001", bal.Problem!, bal.DeviceObjectId));

        var pump = PumpSelection.Select(_pumps, network.TotalFlowKgS, requiredHead, tMean, scenario.HeadMarginFraction);
        if (pump.Problem is not null)
            findings.Add(new Finding(FindingStatus.Warning, "PUMP-001", pump.Problem, source.Id));

        var result = new CalculationResult
        {
            SourceId = source.Id,
            SourceName = source.Name,
            Converged = solve.Converged,
            Iterations = solve.Iterations,
            TotalFlowKgS = network.TotalFlowKgS,
            RequiredHeadPa = requiredHead,
            CriticalRingDeviceId = critical?.DeviceId,
            Pump = pump,
        };
        result.Segments.AddRange(segments);
        result.Devices.AddRange(devices);
        result.Balancing.AddRange(balancing);
        result.Findings.AddRange(findings);
        return result;
    }

    private static double BranchResistance(BranchDescriptor b, double rho, double nu)
    {
        // ΔP = R·G² (G в кг/с). R собираем из геометрии: линейная + местная + арматура (Kv).
        var area = Math.PI * b.InnerDiameterM * b.InnerDiameterM / 4;
        var re = Friction.Reynolds(0.5, b.InnerDiameterM, nu); // λ при характерной скорости; уточняется по факту
        var lambda = Friction.FrictionFactor(FrictionMethod.Churchill, re, b.RoughnessM / b.InnerDiameterM);
        var linearR = lambda * b.LengthM / b.InnerDiameterM / (2 * rho * area * area);
        var minorR = b.ZetaSum / (2 * rho * area * area);
        // ΔP[Па] = (Q[м³/ч]/Kv)²·1e5; Q = G/ρ·3600 ⇒ R = (3600/(ρ·Kv))²·1e5
        var valveR = b.Kv is { } kv && kv > 0 ? Math.Pow(3600.0 / (rho * kv), 2) * 1e5 : 0;
        return Math.Max(linearR + minorR + valveR, 1e-6);
    }

    private static CalculationResult Failed(NetworkObject source, params Finding[] findings)
    {
        var r = new CalculationResult { SourceId = source.Id, SourceName = source.Name };
        r.Findings.AddRange(findings);
        r.Findings.Add(new Finding(FindingStatus.Error, "CALC-000",
            "Расчёт фрагмента невозможен — см. сопутствующие замечания.", source.Id));
        return r;
    }
}
