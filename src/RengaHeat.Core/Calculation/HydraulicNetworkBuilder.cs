using RengaHeat.Core.Hydraulics;
using RengaHeat.Core.Model;
using RengaHeat.Core.Topology;
using RengaHeat.Core.Validation;

namespace RengaHeat.Core.Calculation;

/// <summary>Гидравлическое описание участка-объекта для решателя.</summary>
public sealed record BranchDescriptor(
    NetworkObject Object,
    string FromNode, string ToNode,
    double LengthM, double InnerDiameterM, double RoughnessM, double ZetaSum, double? Kv);

/// <summary>Прибор как ветвь с фиксированным расходом между подающим и обратным узлами.</summary>
public sealed record DeviceDescriptor(
    NetworkObject Object,
    string SupplyNode, string ReturnNode,
    double MassFlowKgS, double OwnKv, bool InletAssumed);

/// <summary>Расчётная гидравлическая схема, готовая для решателя.</summary>
public sealed class HydraulicNetwork
{
    public required string SourceSupplyNode { get; init; }
    public required string SourceReturnNode { get; init; }
    public List<BranchDescriptor> Branches { get; } = new();
    public List<DeviceDescriptor> Devices { get; } = new();
    public List<Finding> BuildFindings { get; } = new();
    public double TotalFlowKgS => Devices.Sum(d => d.MassFlowKgS);
}

/// <summary>
/// Преобразует граф модели в расчётную схему: порты объединяются соединениями в гидравлические
/// узлы (union-find), объекты становятся ветвями. Коллекторы и тройники — узловые объекты:
/// их порты сливаются в один узел. Стрелки трасс Renga не используются — направление даст
/// знак расхода решателя.
/// </summary>
public sealed class HydraulicNetworkBuilder(
    Func<NetworkObject, double> lengthOf,
    Func<NetworkObject, double> innerDiameterOf,
    Func<NetworkObject, double> roughnessOf,
    Func<NetworkObject, double> zetaOf,
    Func<NetworkObject, double?> kvOf,
    Func<NetworkObject, double> deviceFlowOf,
    Func<NetworkObject, double> deviceKvOf)
{
    private static bool IsJunction(NetworkObject o) => o.Role.Role is
        ObjectRole.Tee or ObjectRole.SupplyManifold or ObjectRole.ReturnManifold;

    private static bool IsDevice(NetworkObject o) => o.Role.Role is
        ObjectRole.Radiator or ObjectRole.Convector or ObjectRole.TowelRail or ObjectRole.AirHeater;

    public HydraulicNetwork? Build(HeatingModel model, TopologyAnalysis topology, NetworkObject source,
        IReadOnlySet<string> fragment)
    {
        var uf = new UnionFind();

        foreach (var c in model.Connections)
        {
            if (!fragment.Contains(c.ObjectAId) || !fragment.Contains(c.ObjectBId)) continue;
            uf.Union(PortKey(c.ObjectAId, c.PortAId), PortKey(c.ObjectBId, c.PortBId));
        }

        // Узловые объекты: все порты — один гидравлический узел
        foreach (var id in fragment)
        {
            var obj = model.Get(id);
            if (!IsJunction(obj) || obj.Ports.Count < 2) continue;
            for (var i = 1; i < obj.Ports.Count; i++)
                uf.Union(PortKey(id, obj.Ports[0].Id), PortKey(id, obj.Ports[i].Id));
        }

        if (source.Ports.Count < 2)
            return null;

        var supplyPort = PickSourcePort(source, topology, model, wantSupply: true);
        var returnPort = source.Ports.First(p => p.Id != supplyPort.Id);

        var network = new HydraulicNetwork
        {
            SourceSupplyNode = uf.Find(PortKey(source.Id, supplyPort.Id)),
            SourceReturnNode = uf.Find(PortKey(source.Id, returnPort.Id)),
        };

        foreach (var id in fragment)
        {
            var obj = model.Get(id);
            if (obj.ExcludedFromCalculation || obj.Id == source.Id || IsJunction(obj)) continue;

            if (IsDevice(obj))
            {
                BuildDevice(model, topology, obj, uf, network);
                continue;
            }

            if (obj.Ports.Count < 2)
                continue; // датчики, опоры и прочие однопортовые элементы не являются ветвями

            var from = uf.Find(PortKey(obj.Id, obj.Ports[0].Id));
            var to = uf.Find(PortKey(obj.Id, obj.Ports[1].Id));
            if (from == to)
            {
                network.BuildFindings.Add(new Finding(FindingStatus.Warning, "HYD-003",
                    $"Объект «{obj.Name}» замкнут сам на себя (оба порта в одном узле) и пропущен.", obj.Id));
                continue;
            }
            network.Branches.Add(new BranchDescriptor(obj, from, to,
                lengthOf(obj), innerDiameterOf(obj), roughnessOf(obj), zetaOf(obj), kvOf(obj)));
        }

        if (network.Devices.Count == 0)
        {
            network.BuildFindings.Add(new Finding(FindingStatus.Error, "HYD-001",
                "Во фрагменте с источником нет отопительных приборов — рассчитывать нечего.", source.Id));
            return network;
        }
        return network;
    }

    private void BuildDevice(HeatingModel model, TopologyAnalysis topology, NetworkObject device,
        UnionFind uf, HydraulicNetwork network)
    {
        if (device.Ports.Count < 2)
        {
            network.BuildFindings.Add(new Finding(FindingStatus.Error, "HYD-002",
                $"У прибора «{device.Name}» меньше двух портов — он не может быть включён в кольцо.", device.Id));
            return;
        }

        // Вход прибора — порт, чей сосед лежит на подающей стороне. Если порты перепутаны,
        // прибор подключается правильно внутренне, без изменения модели Renga.
        Port? inlet = null;
        var assumed = false;
        foreach (var port in device.Ports.Take(2))
        {
            if (port.CounterpartObjectId is null) continue;
            var side = topology.SideOf(port.CounterpartObjectId).Side;
            if (side == NetworkSide.Supply) { inlet = port; break; }
        }
        if (inlet is null)
        {
            inlet = device.Ports[0];
            assumed = true; // стороны соседей неизвестны: гипотеза «порт 1 — вход», уточнит знак расхода
        }
        var outlet = device.Ports.First(p => p.Id != inlet.Id);

        network.Devices.Add(new DeviceDescriptor(device,
            uf.Find(PortKey(device.Id, inlet.Id)),
            uf.Find(PortKey(device.Id, outlet.Id)),
            deviceFlowOf(device), deviceKvOf(device), assumed));
    }

    private static Port PickSourcePort(NetworkObject source, TopologyAnalysis topology, HeatingModel model,
        bool wantSupply)
    {
        foreach (var port in source.Ports)
        {
            if (port.CounterpartObjectId is null) continue;
            var side = topology.SideOf(port.CounterpartObjectId).Side;
            if (wantSupply && side == NetworkSide.Supply) return port;
            if (!wantSupply && side == NetworkSide.Return) return port;
        }
        return source.Ports[0];
    }

    private static string PortKey(string objectId, string portId) => $"{objectId}#{portId}";

    private sealed class UnionFind
    {
        private readonly Dictionary<string, string> _parent = new();

        public string Find(string x)
        {
            if (!_parent.TryGetValue(x, out var p)) { _parent[x] = x; return x; }
            if (p == x) return x;
            var root = Find(p);
            _parent[x] = root;
            return root;
        }

        public void Union(string a, string b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb) _parent[ra] = rb;
        }
    }
}
