namespace RengaHeat.Core.Model;

/// <summary>
/// Свойство объекта. Ключ — устойчивый идентификатор (GUID свойства Renga или иной стабильный ключ),
/// имя используется только для поиска и отображения: переименование свойства не разрушает настройку.
/// </summary>
public sealed record PropertyValue(Guid StableId, string Name, object? Value, string? UnitSymbol = null);

/// <summary>Тип соединения порта (данные порта Renga: DN, тип, число подключений).</summary>
public sealed record Port(
    string Id,
    int? Dn = null,
    string? ConnectionType = null,
    string? CounterpartObjectId = null,
    string? CounterpartPortId = null)
{
    public bool IsConnected => CounterpartObjectId is not null;
}

/// <summary>Геометрический и организационный контекст объекта в здании.</summary>
public sealed record BuildingContext(
    string? Building = null,
    string? Section = null,
    int? Floor = null,
    string? Room = null,
    string? SystemName = null,
    string? Apartment = null,
    double? ElevationM = null);

/// <summary>
/// Объект расчётной модели: абстракция над объектом Renga (или созданный вручную).
/// Все данные, которые ядро берёт из Renga, лежат в свойствах/параметрах/количествах —
/// доступ к ним идёт только через сопоставление (Mapping), без жёстко зашитых имён.
/// </summary>
public sealed class NetworkObject
{
    public required string Id { get; init; }                 // устойчивый ID (GUID объекта Renga)
    public string Name { get; set; } = "";
    public string? RengaTypeId { get; init; }                // тип объекта Renga
    public string? Category { get; init; }
    public string? StyleName { get; init; }
    public string? StyleId { get; init; }
    public string? Article { get; init; }                    // артикул
    public string? Material { get; init; }

    public RoleAssignment Role { get; set; } = RoleAssignment.None;
    public BuildingContext Context { get; set; } = new();
    public List<Port> Ports { get; } = new();

    /// <summary>Свойства экземпляра по устойчивому идентификатору.</summary>
    public Dictionary<Guid, PropertyValue> Properties { get; } = new();

    /// <summary>Свойства стиля по устойчивому идентификатору.</summary>
    public Dictionary<Guid, PropertyValue> StyleProperties { get; } = new();

    /// <summary>Параметры объекта/стиля (ключ — имя параметра API).</summary>
    public Dictionary<string, object?> Parameters { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Количества Renga: длина, площадь, номинальный размер и т. п.</summary>
    public Dictionary<string, double> Quantities { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Исключён из расчёта вручную (фиксируется в журнале исключений).</summary>
    public bool ExcludedFromCalculation { get; set; }

    public PropertyValue? FindPropertyByName(string name) =>
        Properties.Values.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public PropertyValue? FindStylePropertyByName(string name) =>
        StyleProperties.Values.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => $"{Name} [{Role.Role}] ({Id})";
}

/// <summary>Физическое соединение двух портов (ребро ненаправленного графа связности).</summary>
public sealed record Connection(string ObjectAId, string PortAId, string ObjectBId, string PortBId);

/// <summary>Расчётная модель: снимок сети отопления, считанный из Renga через адаптер.</summary>
public sealed class HeatingModel
{
    public string Name { get; set; } = "";
    public Dictionary<string, NetworkObject> Objects { get; } = new();
    public List<Connection> Connections { get; } = new();

    public void Add(NetworkObject obj) => Objects.Add(obj.Id, obj);

    public NetworkObject Get(string id) =>
        Objects.TryGetValue(id, out var o)
            ? o
            : throw new KeyNotFoundException($"Объект «{id}» отсутствует в модели.");

    public IEnumerable<NetworkObject> WithRole(ObjectRole role) =>
        Objects.Values.Where(o => o.Role.Role == role && !o.ExcludedFromCalculation);

    /// <summary>Соединить два объекта по указанным портам, обновив данные портов с обеих сторон.</summary>
    public void Connect(NetworkObject a, string portA, NetworkObject b, string portB)
    {
        var pa = a.Ports.FindIndex(p => p.Id == portA);
        var pb = b.Ports.FindIndex(p => p.Id == portB);
        if (pa < 0 || pb < 0)
            throw new ArgumentException($"Порт не найден: {a.Id}:{portA} или {b.Id}:{portB}");
        a.Ports[pa] = a.Ports[pa] with { CounterpartObjectId = b.Id, CounterpartPortId = portB };
        b.Ports[pb] = b.Ports[pb] with { CounterpartObjectId = a.Id, CounterpartPortId = portA };
        Connections.Add(new Connection(a.Id, portA, b.Id, portB));
    }
}
