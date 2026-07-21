namespace RengaHeat.Core.Model;

/// <summary>
/// Свойство объекта. Ключ — устойчивый идентификатор (GUID свойства Renga или иной стабильный ключ),
/// имя используется только для поиска и отображения: переименование свойства не разрушает настройку.
/// </summary>
public sealed record PropertyValue(Guid StableId, string Name, object? Value, string? UnitSymbol = null);

/// <summary>
/// Тип соединения порта (данные порта Renga: DN, тип, число подключений).
/// Координаты (мм, глобальные) заполняются адаптером, если API их отдаёт; по ним работает
/// автосоединение близких свободных точек трассировки (ProximityStitcher).
/// </summary>
public sealed record Port(
    string Id,
    int? Dn = null,
    string? ConnectionType = null,
    string? CounterpartObjectId = null,
    string? CounterpartPortId = null,
    double? Xmm = null,
    double? Ymm = null,
    double? Zmm = null)
{
    public bool IsConnected => CounterpartObjectId is not null;
    public bool HasLocation => Xmm is not null && Ymm is not null && Zmm is not null;
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

    /// <summary>Имя уровня (этажа) Renga, на котором размещён объект (для фильтра по уровням).</summary>
    public string? LevelName { get; set; }
    /// <summary>Идентификатор уровня Renga (числовой Id объекта-уровня).</summary>
    public int? LevelId { get; set; }

    /// <summary>Признак открытого конца: у объекта есть свободный (несоединённый) порт.</summary>
    public bool HasFreePort => Ports.Any(p => !p.IsConnected);
    /// <summary>Наибольший DN среди портов объекта (для ранжирования магистралей/присоединений).</summary>
    public int MaxDn => Ports.Select(p => p.Dn ?? 0).DefaultIfEmpty(0).Max();

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

/// <summary>
/// Физическое соединение двух портов (ребро ненаправленного графа связности).
/// ModeledAtoB — в модели Renga трасса ориентирована A→B (ориентация/стрелка автора модели).
/// Это лишь подсказка для аудита направлений: расчёт направлений ей не доверяет.
/// </summary>
public sealed record Connection(string ObjectAId, string PortAId, string ObjectBId, string PortBId,
    bool ModeledAtoB = false);

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

    /// <summary>Сводка по уровням: имя уровня → число объектов (для раздела «Уровни»).</summary>
    public IReadOnlyList<(string Level, int Count)> LevelSummary() =>
        Objects.Values
            .GroupBy(o => o.LevelName ?? NoLevel)
            .Select(g => (g.Key, g.Count()))
            .OrderBy(x => x.Key, StringComparer.CurrentCulture)
            .ToList();

    /// <summary>Метка объектов без уровня (общие/сквозные — всегда включаются в расчёт).</summary>
    public const string NoLevel = "(без уровня)";

    /// <summary>
    /// Подмодель по выбранным уровням: сохраняет объекты выбранных уровней и объекты без уровня
    /// (магистрали/стояки, сквозные для этажей — иначе рвётся связность), плюс соединения между ними.
    /// Пустой набор — вернуть исходную модель (фильтр выключен). Объекты переиспользуются по ссылке.
    /// </summary>
    public HeatingModel FilterByLevels(IReadOnlySet<string> selectedLevels)
    {
        if (selectedLevels.Count == 0) return this;
        var m = new HeatingModel { Name = Name };
        foreach (var o in Objects.Values)
            if (o.LevelName is null || selectedLevels.Contains(o.LevelName))
                m.Objects[o.Id] = o;
        foreach (var c in Connections)
            if (m.Objects.ContainsKey(c.ObjectAId) && m.Objects.ContainsKey(c.ObjectBId))
                m.Connections.Add(c);
        return m;
    }

    /// <summary>Соединить два объекта по указанным портам, обновив данные портов с обеих сторон.
    /// modeledAtoB — соединение пришло из трассы Renga, ориентированной A→B (для аудита направлений).</summary>
    public void Connect(NetworkObject a, string portA, NetworkObject b, string portB, bool modeledAtoB = false)
    {
        var pa = a.Ports.FindIndex(p => p.Id == portA);
        var pb = b.Ports.FindIndex(p => p.Id == portB);
        if (pa < 0 || pb < 0)
            throw new ArgumentException($"Порт не найден: {a.Id}:{portA} или {b.Id}:{portB}");
        a.Ports[pa] = a.Ports[pa] with { CounterpartObjectId = b.Id, CounterpartPortId = portB };
        b.Ports[pb] = b.Ports[pb] with { CounterpartObjectId = a.Id, CounterpartPortId = portA };
        Connections.Add(new Connection(a.Id, portA, b.Id, portB, modeledAtoB));
    }
}
