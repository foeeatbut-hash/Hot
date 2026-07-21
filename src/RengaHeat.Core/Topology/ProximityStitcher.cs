using RengaHeat.Core.Model;

namespace RengaHeat.Core.Topology;

/// <summary>Одно автоматически созданное соединение: какие порты сшиты и на каком расстоянии.</summary>
public sealed record StitchedPair(
    string ObjectAId, string ObjectAName, string PortAId,
    string ObjectBId, string ObjectBName, string PortBId,
    double DistanceMm);

/// <summary>
/// Автосоединение свободных точек трассировки, стоящих рядом, но физически не соединённых
/// в модели (частая ситуация: трассы сведены «встык» без общей точки подключения).
///
/// Логика — только по геометрии портов, имена объектов не учитываются:
///  1) кандидаты — свободные (несоединённые) порты с координатами;
///  2) пары ищутся пространственной сеткой (соседние ячейки) — на больших моделях это O(N);
///  3) пара допустима, если: разные объекты, расстояние ≤ допуска,
///     DN совместимы (равны либо хотя бы у одного не задан);
///  4) пары сшиваются жадно от самой близкой; каждый порт участвует один раз —
///     из трёх концов в одной точке соединятся два ближайших, третий останется открытым
///     (и попадёт в «открытые концы» на проверку инженеру);
///  5) каждое соединение фиксируется как допущение (список возвращается для журнала и подсветки).
/// </summary>
public static class ProximityStitcher
{
    public static IReadOnlyList<StitchedPair> Stitch(HeatingModel model, double toleranceMm)
    {
        if (toleranceMm <= 0) return Array.Empty<StitchedPair>();

        var free = new List<(NetworkObject O, Port P)>();
        foreach (var o in model.Objects.Values)
        {
            if (o.ExcludedFromCalculation) continue;
            foreach (var p in o.Ports)
                if (!p.IsConnected && p.HasLocation)
                    free.Add((o, p));
        }
        if (free.Count < 2) return Array.Empty<StitchedPair>();

        // Пространственная сетка с ячейкой в допуск: соседи ищутся в 27 смежных ячейках.
        var grid = new Dictionary<(long X, long Y, long Z), List<int>>();
        (long, long, long) CellOf(int i)
        {
            var p = free[i].P;
            return ((long)Math.Floor(p.Xmm!.Value / toleranceMm),
                    (long)Math.Floor(p.Ymm!.Value / toleranceMm),
                    (long)Math.Floor(p.Zmm!.Value / toleranceMm));
        }
        for (var i = 0; i < free.Count; i++)
        {
            var c = CellOf(i);
            if (!grid.TryGetValue(c, out var list)) grid[c] = list = new List<int>();
            list.Add(i);
        }

        static bool DnCompatible(Port a, Port b) => a.Dn is null || b.Dn is null || a.Dn == b.Dn;

        var candidates = new List<(double D, int I, int J)>();
        for (var i = 0; i < free.Count; i++)
        {
            var (cx, cy, cz) = CellOf(i);
            var (oi, pi) = free[i];
            for (var dx = -1L; dx <= 1; dx++)
            for (var dy = -1L; dy <= 1; dy++)
            for (var dz = -1L; dz <= 1; dz++)
            {
                if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var bucket)) continue;
                foreach (var j in bucket)
                {
                    if (j <= i) continue;                       // каждая пара — один раз
                    var (oj, pj) = free[j];
                    if (ReferenceEquals(oi, oj)) continue;      // порты одного объекта не сшиваются
                    if (!DnCompatible(pi, pj)) continue;
                    var ddx = pi.Xmm!.Value - pj.Xmm!.Value;
                    var ddy = pi.Ymm!.Value - pj.Ymm!.Value;
                    var ddz = pi.Zmm!.Value - pj.Zmm!.Value;
                    var dist = Math.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz);
                    if (dist <= toleranceMm) candidates.Add((dist, i, j));
                }
            }
        }
        if (candidates.Count == 0) return Array.Empty<StitchedPair>();

        candidates.Sort((a, b) => a.D.CompareTo(b.D));
        var usedPorts = new HashSet<(string ObjId, string PortId)>();
        var result = new List<StitchedPair>();
        foreach (var (d, i, j) in candidates)
        {
            var (oa, pa) = free[i];
            var (ob, pb) = free[j];
            if (!usedPorts.Add((oa.Id, pa.Id))) continue;
            if (!usedPorts.Add((ob.Id, pb.Id))) { usedPorts.Remove((oa.Id, pa.Id)); continue; }
            model.Connect(oa, pa.Id, ob, pb.Id);   // направление не моделировалось — только связность
            result.Add(new StitchedPair(oa.Id, oa.Name, pa.Id, ob.Id, ob.Name, pb.Id, Math.Round(d, 1)));
        }
        return result;
    }
}
