using RengaHeat.Core.Model;

namespace RengaHeat.Core.Topology;

/// <summary>
/// Уровень уверенности в направлении/стороне участка сети.
/// Хранится для каждого решения; критическая неоднозначность блокирует статус «готово».
/// </summary>
public enum DirectionConfidence
{
    EngineerSet,    // задано инженером
    Determined,     // однозначно определено (топологией/решателем)
    RuleInferred,   // выведено по правилам
    Assumed,        // предположено (расчёт возможен только в режиме «с допущениями»)
    Conflict,       // противоречивые данные — требует решения инженера
}

/// <summary>Сторона сети, к которой отнесён объект.</summary>
public enum NetworkSide { Unknown, Supply, Return, Device, Source }

public sealed record SideAssignment(NetworkSide Side, DirectionConfidence Confidence, string Basis)
{
    public static readonly SideAssignment Unknown =
        new(NetworkSide.Unknown, DirectionConfidence.Assumed, "не определено");
}

/// <summary>
/// Результат топологического анализа: стороны сети, источники, фрагменты, разрывы.
/// Фактическое направление потока на участках определяется позже знаком расхода в решателе;
/// здесь строится исходная гипотеза и её уверенность.
/// </summary>
public sealed class TopologyAnalysis
{
    public required NetworkGraph Graph { get; init; }
    public Dictionary<string, SideAssignment> Sides { get; } = new();
    public List<NetworkObject> Sources { get; } = new();
    public List<IReadOnlySet<string>> Fragments { get; } = new();
    public List<string> Notes { get; } = new();

    /// <summary>Открытые концы сети: несущие объекты со свободным (несоединённым) портом.</summary>
    public List<NetworkObject> OpenEnds { get; } = new();

    /// <summary>
    /// Точки присоединения к ИТП: открытые концы наибольшего диаметра, где магистраль «выходит»
    /// из модели. ИТП может быть не смоделирован — тогда это граница расчёта.
    /// </summary>
    public List<NetworkObject> ItpConnections { get; } = new();

    /// <summary>ИТП не смоделирован и вместо источника принят открытый конец (граница) — допущение.</summary>
    public bool ItpBoundaryAssumed { get; set; }

    public SideAssignment SideOf(string objectId) => Sides.GetValueOrDefault(objectId, SideAssignment.Unknown);

    public bool HasCriticalAmbiguity =>
        Sides.Values.Any(s => s.Confidence == DirectionConfidence.Conflict);
}

/// <summary>
/// Определение сторон (подача/обратка) и исходных направлений.
/// Приоритеты источников информации (по ТЗ):
/// 1) ручное назначение инженера; 2) свойства/параметры; 3) тип и порты оборудования;
/// 4) температурные данные; 5) связь с источником и приборами; 6) стрелка Renga — слабая подсказка.
/// </summary>
public sealed class DirectionInference
{
    /// <summary>Ручные назначения стороны инженером: objectId → сторона.</summary>
    public Dictionary<string, NetworkSide> ManualSides { get; } = new();

    /// <summary>Временный источник для расчёта фрагмента без ИТП (режим «выбор временного источника»).</summary>
    public string? TemporarySourceObjectId { get; set; }

    /// <summary>Делегат чтения признака стороны из свойств (через сопоставление): подача=true, обратка=false.</summary>
    public Func<NetworkObject, bool?>? SideFromProperties { get; set; }

    /// <summary>Делегат чтения температуры теплоносителя объекта, °C (если размечено в модели).</summary>
    public Func<NetworkObject, double?>? TemperatureOf { get; set; }

    /// <summary>Порог: температура выше — подача, ниже — обратка (для графика 80/60 — середина, 70).</summary>
    public double SupplyReturnTemperatureThreshold { get; set; } = 70;

    private static bool IsDevice(NetworkObject o) => o.Role.Role is
        ObjectRole.Radiator or ObjectRole.Convector or ObjectRole.TowelRail or ObjectRole.AirHeater;

    public TopologyAnalysis Analyze(HeatingModel model)
    {
        var graph = new NetworkGraph(model);
        var analysis = new TopologyAnalysis { Graph = graph };

        // Фрагменты: разрывы сети не останавливают анализ — каждый фрагмент рассматривается отдельно
        foreach (var component in graph.ConnectedComponents())
            analysis.Fragments.Add(component);
        if (analysis.Fragments.Count > 1)
            analysis.Notes.Add($"Сеть распадается на {analysis.Fragments.Count} связных фрагментов (возможны разрывы).");

        // Источники: назначенные роли + временный источник инженера
        analysis.Sources.AddRange(model.WithRole(ObjectRole.HeatSource));
        if (TemporarySourceObjectId is { } tempId && model.Objects.TryGetValue(tempId, out var temp) &&
            analysis.Sources.All(s => s.Id != tempId))
        {
            analysis.Sources.Add(temp);
            analysis.Notes.Add($"Использован временный источник «{temp.Name}» (допущение инженера).");
        }

        // Открытые концы и присоединения к ИТП (где магистраль обрывается — граница с ИТП)
        DetectOpenEndsAndItp(model, analysis);

        // ИТП не смоделирован: принимаем присоединение (открытый конец наибольшего DN) как границу,
        // чтобы фрагмент считался. Это допущение, а не физический источник в модели.
        if (analysis.Sources.Count == 0 && analysis.ItpConnections.Count > 0)
        {
            var boundary = analysis.ItpConnections[0];
            analysis.Sources.Add(boundary);
            analysis.ItpBoundaryAssumed = true;
            analysis.Notes.Add($"ИТП не смоделирован: узел присоединения «{boundary.Name}» " +
                               $"(открытый конец Ду{boundary.MaxDn}) принят как граница расчёта.");
        }
        if (analysis.Sources.Count == 0)
            analysis.Notes.Add("Источник тепла и присоединение к ИТП не найдены: расчёт возможен только с временным источником.");
        if (analysis.Sources.Count > 1)
            analysis.Notes.Add($"Найдено несколько источников ({analysis.Sources.Count}): каждый фрагмент/зона рассчитывается от своего источника.");

        foreach (var obj in model.Objects.Values)
        {
            if (obj.ExcludedFromCalculation) continue;
            analysis.Sides[obj.Id] = InferSide(obj, analysis);
        }

        // Топологическая волна: от подающих коллекторов/магистралей через сеть до приборов
        PropagateSidesTopologically(model, graph, analysis);

        return analysis;
    }

    private static bool IsCarrier(NetworkObject o) =>
        !IsDevice(o) && o.Role.Role is not ObjectRole.HeatSource;

    /// <summary>
    /// Открытые концы: несущие объекты со свободным портом (сеть там обрывается). Присоединение к
    /// ИТП — в первую очередь открытая ТОЧКА ТРАССИРОВКИ (узел, которым сеть «выходит» из модели);
    /// если таких нет — открытые концы наибольшего DN; если DN нигде не задан — все открытые концы.
    /// Логика топологическая — координаты не нужны.
    /// </summary>
    private static void DetectOpenEndsAndItp(HeatingModel model, TopologyAnalysis analysis)
    {
        var openEnds = model.Objects.Values
            .Where(o => !o.ExcludedFromCalculation && IsCarrier(o) &&
                        (o.HasFreePort ||
                         // точка трассировки без портов: узел, не соединённый ни с чем, — открытый
                         (o.Role.Role == ObjectRole.RoutePoint && o.Ports.Count == 0)))
            .ToList();
        analysis.OpenEnds.AddRange(openEnds);
        if (openEnds.Count == 0) return;

        var routePoints = openEnds.Where(o => o.Role.Role == ObjectRole.RoutePoint).ToList();
        List<NetworkObject> itp;
        string basis;
        if (routePoints.Count > 0)
        {
            // ИТП — это точка трассировки; при нескольких первым идёт узел наибольшего DN (ввод).
            itp = routePoints.OrderByDescending(o => o.MaxDn).ToList();
            basis = "открытые точки трассировки";
        }
        else
        {
            var maxDn = openEnds.Max(o => o.MaxDn);
            itp = maxDn > 0
                ? openEnds.Where(o => o.MaxDn == maxDn).ToList()   // магистральные концы наибольшего DN
                : openEnds;                                        // DN неизвестен — все концы кандидаты
            basis = maxDn > 0 ? $"открытые концы Ду{maxDn}" : "все открытые концы";
        }
        analysis.ItpConnections.AddRange(itp);
        analysis.Notes.Add($"Открытых концов сети: {openEnds.Count}; присоединений к ИТП ({basis}): {itp.Count}.");
    }

    private SideAssignment InferSide(NetworkObject obj, TopologyAnalysis analysis)
    {
        // 1. Ручное назначение
        if (ManualSides.TryGetValue(obj.Id, out var manual))
            return new SideAssignment(manual, DirectionConfidence.EngineerSet, "назначено инженером");

        if (analysis.Sources.Any(s => s.Id == obj.Id))
            return new SideAssignment(NetworkSide.Source, DirectionConfidence.Determined, "источник тепла");

        if (IsDevice(obj))
            return new SideAssignment(NetworkSide.Device, DirectionConfidence.Determined, "отопительный прибор");

        // 2. Свойства/параметры (через сопоставление — без жёстких имён)
        if (SideFromProperties?.Invoke(obj) is { } fromProps)
            return new SideAssignment(fromProps ? NetworkSide.Supply : NetworkSide.Return,
                DirectionConfidence.RuleInferred, "признак стороны из свойств");

        // 3. Тип и роль оборудования
        switch (obj.Role.Role)
        {
            case ObjectRole.SupplyMain or ObjectRole.SupplyManifold:
                return new SideAssignment(NetworkSide.Supply, DirectionConfidence.RuleInferred, $"роль {obj.Role.Role}");
            case ObjectRole.ReturnMain or ObjectRole.ReturnManifold:
                return new SideAssignment(NetworkSide.Return, DirectionConfidence.RuleInferred, $"роль {obj.Role.Role}");
        }

        // 4. Температурные данные
        if (TemperatureOf?.Invoke(obj) is { } t)
            return new SideAssignment(
                t >= SupplyReturnTemperatureThreshold ? NetworkSide.Supply : NetworkSide.Return,
                DirectionConfidence.RuleInferred, $"температура {t:0.#} °C");

        // 5–6. Пока неизвестно: решит топологическая волна или решатель по знаку расхода
        return SideAssignment.Unknown;
    }

    /// <summary>
    /// Распространение сторон по связности: волна от известных подающих узлов помечает
    /// неизвестные трубы/арматуру как подачу, от обратных — как обратку. Приборы — барьеры.
    /// Узел, достижимый с обеих сторон без прохода через прибор, помечается конфликтом.
    /// </summary>
    private static void PropagateSidesTopologically(HeatingModel model, NetworkGraph graph, TopologyAnalysis analysis)
    {
        bool IsBarrier(string id) =>
            analysis.SideOf(id).Side is NetworkSide.Device or NetworkSide.Source;

        var supplySeeds = analysis.Sides.Where(kv => kv.Value.Side == NetworkSide.Supply).Select(kv => kv.Key).ToList();
        var returnSeeds = analysis.Sides.Where(kv => kv.Value.Side == NetworkSide.Return).Select(kv => kv.Key).ToList();

        var fromSupply = graph.ReachableFrom(supplySeeds, IsBarrier);
        var fromReturn = graph.ReachableFrom(returnSeeds, IsBarrier);

        foreach (var obj in model.Objects.Values)
        {
            var current = analysis.SideOf(obj.Id);
            if (current.Side != NetworkSide.Unknown) continue;

            var inSupply = fromSupply.Contains(obj.Id);
            var inReturn = fromReturn.Contains(obj.Id);
            analysis.Sides[obj.Id] = (inSupply, inReturn) switch
            {
                (true, false) => new SideAssignment(NetworkSide.Supply, DirectionConfidence.Determined,
                    "связность с подающей стороной"),
                (false, true) => new SideAssignment(NetworkSide.Return, DirectionConfidence.Determined,
                    "связность с обратной стороной"),
                (true, true) => new SideAssignment(NetworkSide.Unknown, DirectionConfidence.Conflict,
                    "достижим и с подачи, и с обратки без прохода через прибор"),
                _ => new SideAssignment(NetworkSide.Unknown, DirectionConfidence.Assumed,
                    "не связан с размеченными сторонами"),
            };
        }
    }
}
