using RengaHeat.Core.Model;
using RengaHeat.Core.Profiles;
using RengaHeat.Core.Topology;

namespace RengaHeat.Core.Validation;

/// <summary>Статусы, отображаемые инженеру в таблицах плагина.</summary>
public enum FindingStatus
{
    Error,          // ошибка
    Warning,        // предупреждение
    Assumption,     // допущение
    NeedsDecision,  // требует решения инженера
    Done,           // выполнено
    Excluded,       // исключено
    ManuallyFixed,  // зафиксировано вручную
}

/// <summary>Замечание проверки модели с привязкой к объекту (переход к объекту в Renga — по ObjectId).</summary>
public sealed record Finding(
    FindingStatus Status,
    string Code,
    string Message,
    string? ObjectId = null,
    string? Grouping = null)     // корпус/секция/этаж/система/кольцо для группировки таблиц
{
    public override string ToString() => $"[{Status}] {Code}: {Message}";
}

/// <summary>
/// Проверка модели на ошибки и на требования профиля ЧТУ.
/// Все лимиты берутся из профиля/правил — жёстко зашитой логики объекта здесь нет.
/// </summary>
public sealed class ModelValidator(RequirementsProfile profile)
{
    public List<Finding> Validate(HeatingModel model, TopologyAnalysis topology)
    {
        var findings = new List<Finding>();
        CheckConnectivity(model, topology, findings);
        CheckSources(topology, findings);
        CheckRoles(model, findings);
        CheckManifoldLimits(model, findings);
        CheckLoopDeviceLimits(model, findings);
        CheckFloorZone(model, findings);
        CheckRadiatorLengths(model, findings);
        CheckDprBeforeManifolds(model, topology, findings);
        CheckHeatMeterPlacement(model, topology, findings);
        CheckDirectionConflicts(model, topology, findings);
        return findings;
    }

    private static void CheckConnectivity(HeatingModel model, TopologyAnalysis topology, List<Finding> findings)
    {
        if (topology.Fragments.Count > 1)
            findings.Add(new Finding(FindingStatus.Warning, "NET-001",
                $"Сеть распадается на {topology.Fragments.Count} фрагментов. Возможен разрыв: " +
                "расчёт выполняется по связным фрагментам, недостающие связи нужно достроить или подтвердить."));

        foreach (var obj in model.Objects.Values.Where(o => !o.ExcludedFromCalculation))
        {
            var unconnected = obj.Ports.Where(p => !p.IsConnected).ToList();
            if (unconnected.Count > 0 && obj.Ports.Count > 1)
                findings.Add(new Finding(FindingStatus.Warning, "NET-002",
                    $"У объекта «{obj.Name}» не подключены порты: {string.Join(", ", unconnected.Select(p => p.Id))}.",
                    obj.Id, obj.Context.SystemName));
        }
    }

    private static void CheckSources(TopologyAnalysis topology, List<Finding> findings)
    {
        if (topology.Sources.Count == 0)
            findings.Add(new Finding(FindingStatus.NeedsDecision, "SRC-001",
                "Источник тепла (ИТП) не найден, и нет открытого конца сети для присоединения. " +
                "Назначьте роль «Источник тепла» или выберите временный источник."));
        else if (topology.ItpBoundaryAssumed)
            findings.Add(new Finding(FindingStatus.Assumption, "SRC-003",
                "ИТП не смоделирован: принят открытый конец магистрали как граница расчёта (допущение). " +
                "Проверьте точку присоединения в разделе «Карта»."));
        else if (topology.Sources.Count > 1)
            findings.Add(new Finding(FindingStatus.Assumption, "SRC-002",
                $"Несколько источников ({topology.Sources.Count}). Каждая зона рассчитывается от своего источника; " +
                "проверьте границы зон."));
    }

    private static void CheckRoles(HeatingModel model, List<Finding> findings)
    {
        foreach (var obj in model.Objects.Values.Where(o =>
                     !o.ExcludedFromCalculation && o.Role.Role == ObjectRole.Unknown))
            findings.Add(new Finding(FindingStatus.NeedsDecision, "CLS-001",
                $"Роль объекта «{obj.Name}» не определена. Уточните правила классификатора " +
                $"или заполните свойство «ОВ_Роль».", obj.Id, obj.Context.SystemName));
    }

    private void CheckManifoldLimits(HeatingModel model, List<Finding> findings)
    {
        foreach (var manifold in model.WithRole(ObjectRole.SupplyManifold))
        {
            var apartments = ApartmentsServedBy(model, manifold);
            if (apartments.Count > profile.MaxApartmentsPerManifold)
                findings.Add(new Finding(FindingStatus.Error, "CTU-001",
                    $"Коллектор «{manifold.Name}» обслуживает {apartments.Count} квартир — " +
                    $"больше допустимых {profile.MaxApartmentsPerManifold} по {profile.Name}.",
                    manifold.Id, manifold.Context.Section));
        }

        foreach (var group in model.WithRole(ObjectRole.SupplyManifold)
                     .Where(m => m.Context.Section is not null)
                     .GroupBy(m => (m.Context.Building, m.Context.Section)))
        {
            if (group.Count() > profile.MaxManifoldsPerSection)
                findings.Add(new Finding(FindingStatus.Warning, "CTU-002",
                    $"В секции «{group.Key.Section}» {group.Count()} коллекторов — больше " +
                    $"{profile.MaxManifoldsPerSection}; по {profile.Name} требуется отдельное согласование.",
                    group.First().Id, group.Key.Section));
        }
    }

    private void CheckLoopDeviceLimits(HeatingModel model, List<Finding> findings)
    {
        // Кольцо = группа приборов одной квартиры (горизонтальная поэтажная разводка)
        foreach (var loop in HeatingDevices(model)
                     .Where(d => d.Context.Apartment is not null)
                     .GroupBy(d => (d.Context.Building, d.Context.Section, d.Context.Apartment)))
        {
            if (loop.Count() > profile.MaxDevicesPerHorizontalLoop)
                findings.Add(new Finding(FindingStatus.Error, "CTU-003",
                    $"В кольце квартиры «{loop.Key.Apartment}» {loop.Count()} отопительных приборов — " +
                    $"больше допустимых {profile.MaxDevicesPerHorizontalLoop} по {profile.Name}.",
                    loop.First().Id, loop.Key.Apartment));
        }
    }

    private void CheckFloorZone(HeatingModel model, List<Finding> findings)
    {
        var maxFloor = model.Objects.Values
            .Where(o => !o.ExcludedFromCalculation)
            .Select(o => o.Context.Floor)
            .Where(f => f is not null)
            .DefaultIfEmpty(null)
            .Max();
        if (maxFloor > profile.MaxFloorsLowerZone)
            findings.Add(new Finding(FindingStatus.Warning, "CTU-004",
                $"В модели есть объекты выше {profile.MaxFloorsLowerZone}-го этажа (до {maxFloor}); " +
                $"нижняя высотная зона по {profile.Name} — не более {profile.MaxFloorsLowerZone} этажей. " +
                "Проверьте зонирование системы."));
    }

    private void CheckRadiatorLengths(HeatingModel model, List<Finding> findings)
    {
        foreach (var radiator in model.WithRole(ObjectRole.Radiator)
                     .Where(r => r.Context.Apartment is not null))
        {
            // Длина берётся из количеств/свойств через сопоставление; здесь — из количества, если оно есть
            if (radiator.Quantities.TryGetValue("Длина", out var lengthM) &&
                lengthM > profile.MaxApartmentRadiatorLengthM)
                findings.Add(new Finding(FindingStatus.Error, "CTU-005",
                    $"Радиатор «{radiator.Name}» длиной {lengthM * 1000:0} мм превышает " +
                    $"{profile.MaxApartmentRadiatorLengthM * 1000:0} мм, допустимые по {profile.Name}.",
                    radiator.Id, radiator.Context.Apartment));
        }
    }

    private void CheckDprBeforeManifolds(HeatingModel model, TopologyAnalysis topology, List<Finding> findings)
    {
        if (!profile.RequireDprBeforeManifold) return;
        foreach (var manifold in model.WithRole(ObjectRole.SupplyManifold))
        {
            var hasDpr = topology.Graph.NeighborsOf(manifold.Id)
                .Concat(topology.Graph.NeighborsOf(manifold.Id)
                    .SelectMany(n => topology.Graph.NeighborsOf(n)))
                .Select(model.Get)
                .Any(o => o.Role.Role == ObjectRole.DifferentialPressureRegulator);
            if (!hasDpr)
                findings.Add(new Finding(FindingStatus.Error, "CTU-006",
                    $"Перед коллектором «{manifold.Name}» не найден регулятор перепада давления, " +
                    $"обязательный по {profile.Name}.", manifold.Id, manifold.Context.Section));
        }
    }

    private void CheckHeatMeterPlacement(HeatingModel model, TopologyAnalysis topology, List<Finding> findings)
    {
        if (!profile.HeatMeterOnReturn) return;
        foreach (var meter in model.WithRole(ObjectRole.HeatMeter))
        {
            var side = topology.SideOf(meter.Id).Side;
            if (side == NetworkSide.Supply)
                findings.Add(new Finding(FindingStatus.Error, "CTU-007",
                    $"Теплосчётчик «{meter.Name}» стоит на подающем трубопроводе; по {profile.Name} " +
                    "теплосчётчики располагаются на обратном.", meter.Id, meter.Context.SystemName));
        }
    }

    private static void CheckDirectionConflicts(HeatingModel model, TopologyAnalysis topology, List<Finding> findings)
    {
        foreach (var (objectId, side) in topology.Sides)
        {
            if (side.Confidence != DirectionConfidence.Conflict) continue;
            var obj = model.Get(objectId);
            findings.Add(new Finding(FindingStatus.NeedsDecision, "DIR-001",
                $"Сторона сети объекта «{obj.Name}» противоречива: {side.Basis}. " +
                "Назначьте подачу/обратку вручную или исправьте модель.", objectId, obj.Context.SystemName));
        }
    }

    private static IEnumerable<NetworkObject> HeatingDevices(HeatingModel model) =>
        model.Objects.Values.Where(o => !o.ExcludedFromCalculation && o.Role.Role is
            ObjectRole.Radiator or ObjectRole.Convector or ObjectRole.TowelRail or ObjectRole.AirHeater);

    /// <summary>Квартиры, обслуживаемые коллектором: по связности до приборов с признаком квартиры.</summary>
    private static IReadOnlyList<string> ApartmentsServedBy(HeatingModel model, NetworkObject manifold)
    {
        var graph = new NetworkGraph(model);
        bool IsOtherManifold(string id) =>
            id != manifold.Id && model.Get(id).Role.Role is ObjectRole.SupplyManifold or ObjectRole.ReturnManifold
                or ObjectRole.HeatSource;
        var reachable = graph.ReachableFrom(new[] { manifold.Id }, IsOtherManifold);
        return reachable
            .Select(model.Get)
            .Select(o => o.Context.Apartment)
            .Where(a => a is not null)
            .Distinct()
            .ToList()!;
    }
}
