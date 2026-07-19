using RengaHeat.Core.Model;

namespace RengaHeat.Core.Topology;

/// <summary>Ребро ненаправленного графа физической связности.</summary>
public sealed record GraphEdge(string Id, string NodeA, string NodeB, Connection Source);

/// <summary>
/// Ненаправленный граф физической связности модели — первый шаг алгоритма.
/// Узлы — объекты, рёбра — соединения портов. Стрелки трасс Renga здесь не участвуют:
/// они не считаются физическим направлением теплоносителя.
/// </summary>
public sealed class NetworkGraph
{
    private readonly Dictionary<string, List<GraphEdge>> _adjacency = new();
    public HeatingModel Model { get; }
    public IReadOnlyList<GraphEdge> Edges { get; }

    public NetworkGraph(HeatingModel model)
    {
        Model = model;
        var edges = new List<GraphEdge>();
        foreach (var obj in model.Objects.Values)
            _adjacency[obj.Id] = new List<GraphEdge>();

        var i = 0;
        foreach (var c in model.Connections)
        {
            if (!_adjacency.ContainsKey(c.ObjectAId) || !_adjacency.ContainsKey(c.ObjectBId))
                continue; // соединение с отсутствующим объектом — попадёт в диагностику
            var edge = new GraphEdge($"e{i++}", c.ObjectAId, c.ObjectBId, c);
            edges.Add(edge);
            _adjacency[c.ObjectAId].Add(edge);
            _adjacency[c.ObjectBId].Add(edge);
        }
        Edges = edges;
    }

    public IReadOnlyList<GraphEdge> EdgesOf(string nodeId) =>
        _adjacency.TryGetValue(nodeId, out var list) ? list : Array.Empty<GraphEdge>();

    public IEnumerable<string> NeighborsOf(string nodeId) =>
        EdgesOf(nodeId).Select(e => e.NodeA == nodeId ? e.NodeB : e.NodeA);

    /// <summary>Связные фрагменты сети. Больше одного — сеть имеет разрывы (или несколько систем).</summary>
    public IReadOnlyList<IReadOnlySet<string>> ConnectedComponents()
    {
        var visited = new HashSet<string>();
        var components = new List<IReadOnlySet<string>>();
        foreach (var start in _adjacency.Keys)
        {
            if (!visited.Add(start)) continue;
            var component = new HashSet<string> { start };
            var queue = new Queue<string>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                foreach (var n in NeighborsOf(queue.Dequeue()))
                    if (visited.Add(n))
                    {
                        component.Add(n);
                        queue.Enqueue(n);
                    }
            }
            components.Add(component);
        }
        return components;
    }

    /// <summary>
    /// Обход в ширину из набора стартовых узлов, не проходя сквозь «барьерные» узлы
    /// (барьер включается в результат, но обход через него не продолжается).
    /// Используется для разделения сети на подающую и обратную стороны: барьеры — приборы.
    /// </summary>
    public IReadOnlySet<string> ReachableFrom(IEnumerable<string> starts, Func<string, bool>? isBarrier = null)
    {
        var visited = new HashSet<string>();
        var queue = new Queue<string>();
        foreach (var s in starts)
            if (_adjacency.ContainsKey(s) && visited.Add(s))
                queue.Enqueue(s);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var n in NeighborsOf(current))
            {
                if (!visited.Add(n)) continue;
                if (isBarrier is null || !isBarrier(n))
                    queue.Enqueue(n);
            }
        }
        return visited;
    }
}
