using RengaHeat.Core.Model;

namespace RengaHeat.Core.Topology;

/// <summary>Несовпадение: ориентация трассы в модели противоположна правильному направлению потока.</summary>
public sealed record DirectionIssue(
    string ObjectAId, string ObjectAName,
    string ObjectBId, string ObjectBName,
    string CorrectFromId,          // откуда должен идти поток по расчёту
    string Basis)                  // на чём основан вывод (для журнала «почему»)
{
    public override string ToString() =>
        $"«{ObjectAName}» ↔ «{ObjectBName}»: поток должен идти от " +
        $"«{(CorrectFromId == ObjectAId ? ObjectAName : ObjectBName)}» ({Basis})";
}

/// <summary>Итог аудита направлений по всем ориентированным соединениям модели.</summary>
public sealed record DirectionAuditResult(
    IReadOnlyList<DirectionIssue> Issues,
    int Checked,        // соединений с ориентацией, попавших под проверку
    int Confirmed,      // ориентация совпала с расчётным направлением
    int Undecidable)    // направление не определяется однозначно (кольца, неизвестные стороны)
{
    public static readonly DirectionAuditResult Empty =
        new(Array.Empty<DirectionIssue>(), 0, 0, 0);
}

/// <summary>
/// Аудит направлений потока: сравнивает ориентацию трасс, заданную в модели (стрелки/порядок
/// точек Renga), с физически правильным направлением, выведенным из топологии.
///
/// Расчёт направлениям модели НЕ доверяет никогда — гидравлика всегда считается по собственным
/// направлениям (от источника по подаче к приборам, от приборов по обратке к источнику).
/// Аудит нужен инженеру: он показывает, где стрелки в модели «нарисованы» против потока,
/// чтобы модель можно было привести в порядок.
///
/// Правила вывода правильного направления (в порядке надёжности):
///  1) ребро у источника: по подающей стороне поток выходит из источника, по обратной — входит;
///  2) ребро у прибора: с подающей стороны поток входит в прибор, с обратной — выходит;
///  3) ребро внутри одной стороны: по подаче поток течёт от источника (от меньшей топологической
///     дистанции к большей), по обратке — к источнику (от большей к меньшей);
///  4) стороны разные/неизвестны/конфликт, дистанции равны (кольцо) или недостижимы —
///     направление честно помечается как неопределимое, а не угадывается.
/// </summary>
public static class DirectionAudit
{
    public static DirectionAuditResult Audit(HeatingModel model, TopologyAnalysis topology)
    {
        var oriented = model.Connections.Where(c => c.ModeledAtoB).ToList();
        if (oriented.Count == 0 || topology.Sources.Count == 0) return DirectionAuditResult.Empty;

        // Топологическая дистанция от источников (BFS по всей сети, без барьеров).
        var dist = Distances(topology);

        var issues = new List<DirectionIssue>();
        int confirmed = 0, undecidable = 0, checkedCount = 0;

        foreach (var c in oriented)
        {
            if (!model.Objects.TryGetValue(c.ObjectAId, out var a) ||
                !model.Objects.TryGetValue(c.ObjectBId, out var b)) continue;
            checkedCount++;

            var (correctFrom, basis) = CorrectFlowFrom(c, topology, dist);
            if (correctFrom is null) { undecidable++; continue; }

            // Ориентация модели — A→B. Совпадает, если правильный исток — A.
            if (correctFrom == c.ObjectAId) confirmed++;
            else issues.Add(new DirectionIssue(a.Id, a.Name, b.Id, b.Name, correctFrom, basis!));
        }
        return new DirectionAuditResult(issues, checkedCount, confirmed, undecidable);
    }

    /// <summary>Откуда должен идти поток на ребре (null — неопределимо), и обоснование.</summary>
    private static (string? FromId, string? Basis) CorrectFlowFrom(
        Connection c, TopologyAnalysis topology, IReadOnlyDictionary<string, int> dist)
    {
        var sideA = topology.SideOf(c.ObjectAId);
        var sideB = topology.SideOf(c.ObjectBId);

        // 1. Ребро у источника: подача выходит из него, обратка входит.
        if (sideA.Side == NetworkSide.Source || sideB.Side == NetworkSide.Source)
        {
            var (src, other, otherSide) = sideA.Side == NetworkSide.Source
                ? (c.ObjectAId, c.ObjectBId, sideB.Side)
                : (c.ObjectBId, c.ObjectAId, sideA.Side);
            return otherSide switch
            {
                NetworkSide.Supply => (src, "подача выходит из источника"),
                NetworkSide.Return => (other, "обратка входит в источник"),
                _ => (null, null),
            };
        }

        // 2. Ребро у прибора: с подачи поток входит в прибор, с обратки — выходит.
        if (sideA.Side == NetworkSide.Device ^ sideB.Side == NetworkSide.Device)
        {
            var (device, other, otherSide) = sideA.Side == NetworkSide.Device
                ? (c.ObjectAId, c.ObjectBId, sideB.Side)
                : (c.ObjectBId, c.ObjectAId, sideA.Side);
            return otherSide switch
            {
                NetworkSide.Supply => (other, "подводка подачи входит в прибор"),
                NetworkSide.Return => (device, "обратная подводка выходит из прибора"),
                _ => (null, null),
            };
        }

        // 3. Ребро внутри одной стороны: направление — по топологической дистанции от источника.
        if (sideA.Side == sideB.Side &&
            sideA.Side is NetworkSide.Supply or NetworkSide.Return &&
            dist.TryGetValue(c.ObjectAId, out var da) && dist.TryGetValue(c.ObjectBId, out var db) &&
            da != db)
        {
            var nearId = da < db ? c.ObjectAId : c.ObjectBId;
            var farId = da < db ? c.ObjectBId : c.ObjectAId;
            return sideA.Side == NetworkSide.Supply
                ? (nearId, "по подаче поток течёт от источника")
                : (farId, "по обратке поток течёт к источнику");
        }

        // 4. Стороны разные/неизвестны, кольцо с равными дистанциями — не угадываем.
        return (null, null);
    }

    private static IReadOnlyDictionary<string, int> Distances(TopologyAnalysis topology)
    {
        var dist = new Dictionary<string, int>();
        var queue = new Queue<string>();
        foreach (var s in topology.Sources)
            if (dist.TryAdd(s.Id, 0)) queue.Enqueue(s.Id);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            foreach (var n in topology.Graph.NeighborsOf(cur))
                if (dist.TryAdd(n, dist[cur] + 1)) queue.Enqueue(n);
        }
        return dist;
    }
}
