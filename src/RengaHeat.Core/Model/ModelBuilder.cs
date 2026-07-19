namespace RengaHeat.Core.Model;

/// <summary>
/// Удобный строитель расчётных моделей для тестов и демонстраций.
/// Создаёт объекты, порты и соединения, задавая роли, свойства и контекст.
/// </summary>
public sealed class ModelBuilder
{
    private readonly HeatingModel _model = new();
    private int _autoId;

    public ModelBuilder(string name = "Модель") => _model.Name = name;

    public HeatingModel Model => _model;

    public NetworkObject Add(string name, ObjectRole role, RoleSource roleSource = RoleSource.Manual,
        int portCount = 2, BuildingContext? context = null, string? styleName = null)
    {
        var obj = new NetworkObject
        {
            Id = $"o{_autoId++}",
            Name = name,
            StyleName = styleName,
            Role = new RoleAssignment(role, roleSource),
            Context = context ?? new BuildingContext(),
        };
        for (var i = 0; i < portCount; i++)
            obj.Ports.Add(new Port($"p{i}", Dn: 15));
        _model.Add(obj);
        return obj;
    }

    public NetworkObject AddPipe(string name, ObjectRole role = ObjectRole.Pipe, double lengthM = 3,
        double innerDiameterM = 0.0125, int dn = 15, BuildingContext? context = null)
    {
        var pipe = Add(name, role, portCount: 2, context: context);
        pipe.Quantities["Длина"] = lengthM;
        pipe.Properties[PropId("d_вн")] = new PropertyValue(PropId("d_вн"), "d_вн", innerDiameterM);
        for (var i = 0; i < pipe.Ports.Count; i++)
            pipe.Ports[i] = pipe.Ports[i] with { Dn = dn };
        return pipe;
    }

    public NetworkObject AddRadiator(string name, double loadW, BuildingContext context,
        string loadPropertyName = "Q_расч")
    {
        var rad = Add(name, ObjectRole.Radiator, portCount: 2, context: context);
        rad.Properties[PropId(loadPropertyName)] =
            new PropertyValue(PropId(loadPropertyName), loadPropertyName, loadW);
        return rad;
    }

    public void SetProperty(NetworkObject obj, string name, object? value, string? unit = null) =>
        obj.Properties[PropId(name)] = new PropertyValue(PropId(name), name, value, unit);

    /// <summary>Соединить два объекта: portA объекта a с portB объекта b.</summary>
    public void Connect(NetworkObject a, int portA, NetworkObject b, int portB) =>
        _model.Connect(a, a.Ports[portA].Id, b, b.Ports[portB].Id);

    /// <summary>Соединить цепочку объектов последовательно (порт 1 → порт 0 следующего).</summary>
    public void Chain(params NetworkObject[] chain)
    {
        for (var i = 0; i < chain.Length - 1; i++)
            _model.Connect(chain[i], chain[i].Ports[1].Id, chain[i + 1], chain[i + 1].Ports[0].Id);
    }

    public static Guid PropId(string name)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(name));
        return new Guid(bytes);
    }
}
