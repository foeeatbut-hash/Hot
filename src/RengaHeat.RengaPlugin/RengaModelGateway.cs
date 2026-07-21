// Renga-адаптер: реализация IModelGateway поверх официального Renga API (Interop.Renga).
//
// Код проверен сборкой под реальный Renga SDK (Interop.Renga + Renga.NET8.PluginUtility, API 2.48)
// и использует фактические интерфейсы: IModelObjectCollection.GetByIndex, UniqueIdS, ObjectTypeS,
// IPropertyContainer/IParameterContainer/IQuantityContainer, IEntityWithPorts/IPortPipeParams,
// IRouteParams для связности трасс.
//
// Компилируется только при наличии Renga SDK (константа RENGA_SDK_ABSENT НЕ задана).
// Без SDK вместо адаптера компилируется заглушка в конце файла.

using System.Globalization;
using RengaHeat.Core.Adapter;
using RengaHeat.Core.Calculation;
using RengaHeat.Core.Model;

namespace RengaHeat.RengaPlugin;

#if !RENGA_SDK_ABSENT

/// <summary>
/// Шлюз к модели Renga. Читает объекты, свойства, параметры, количества и порты в HeatingModel,
/// восстанавливает связность сети по параметрам трасс.
///
/// По умолчанию — режим «только анализ»: запись отключена (Capabilities = false). Чтобы включить
/// запись результатов, настройте явное сопоставление свойств-приёмников (GUID) и подтверждайте
/// изменения через предпросмотр; тогда ApplyChanges выполняется в операции проекта с Undo.
/// </summary>
public sealed class RengaModelGateway : IModelGateway
{
    private readonly Renga.IApplication _application;

    // Кэш рефлексии свойства уровня: находим один раз, а не на каждый из десятков тысяч объектов.
    // ВАЖНО: у COM-объектов GetType() возвращает __ComObject без свойств, поэтому LevelId ищем
    // по типам interop-сборки (ILevelObject / IModelObject), а не по типу экземпляра.
    private static System.Reflection.PropertyInfo? _levelIdProp;
    private static bool _levelIdOnModelObject;   // LevelId объявлен прямо на IModelObject
    private static bool _levelIdProbed;

    // Кэш рефлексии геометрии порта (размещение → начало координат). Свойства ищутся по интерфейсу
    // Renga.IPort один раз; если в этой версии API их нет — координаты остаются пустыми, и
    // автосоединение по близости честно отключается (без падений).
    private static System.Reflection.PropertyInfo? _portPlacementProp;
    private static System.Reflection.PropertyInfo? _placementOriginProp;
    private static bool _portGeomProbed;

    public RengaModelGateway(Renga.IApplication application) => _application = application;

    /// <summary>
    /// Инженерные типы объектов Renga (ОВ/ВК/ЭОМ): трубы, фитинги, арматура, воздуховоды,
    /// оборудование, приборы. Архитектура (стены, полы, двери, материалы, помещения) не читается.
    ///
    /// Это подсказка, а НЕ единственный критерий: GUID-типы отличаются между версиями/сборками
    /// Renga, поэтому основной признак инженерного объекта — наличие портов трубопровода/воздуховода
    /// или параметров трассы (см. IsEngineering). Так фильтр не «слепнет» при несовпадении GUID.
    /// </summary>
    private static readonly HashSet<Guid> EngineeringTypes = new()
    {
        Renga.EntityTypes.Pipe, Renga.EntityTypes.PipeFitting, Renga.EntityTypes.PipeAccessory,
        Renga.EntityTypes.Duct, Renga.EntityTypes.DuctFitting, Renga.EntityTypes.DuctAccessory,
        Renga.EntityTypes.MechanicalEquipment, Renga.EntityTypes.Equipment,
        Renga.EntityTypes.PlumbingFixture, Renga.EntityTypes.LightingFixture,
        Renga.EntityTypes.WiringAccessory, Renga.EntityTypes.ElectricDistributionBoard,
        Renga.EntityTypes.ElectricalCircuitLine,
    };

    /// <summary>
    /// Инженерный объект — по сути, а не по жёсткому списку GUID: либо известный инженерный тип,
    /// либо у объекта есть порты трубопровода/воздуховода (IEntityWithPorts), либо параметры трассы
    /// (IRouteParams). Архитектура (стены, двери, помещения) портов трубопровода не имеет и отсеивается.
    /// </summary>
    private static bool IsEngineering(Renga.IModelObject mo)
    {
        try { if (EngineeringTypes.Contains(mo.ObjectType)) return true; } catch { /* тип нечитаем */ }
        try { if (mo is Renga.IEntityWithPorts { Count: > 0 }) return true; } catch { /* нет портов */ }
        try { if (mo.GetInterfaceByName("IRouteParams") is Renga.IRouteParams) return true; } catch { /* нет трассы */ }
        return false;
    }

    public GatewayCapabilities Capabilities { get; } = new(
        CanWriteProperties: false, CanChangePipeStyle: false, CanWriteValvePreset: false,
        CanCreateMarks: false, CanFixDirection: false, SupportsUndo: false);

    /// <summary>Прочитать всю инженерную модель проекта.</summary>
    public HeatingModel ReadModel() => ReadModelCore(null);

    /// <summary>
    /// Прочитать только объекты, выделенные в Renga (сценарий изоляции уровней: изолируем уровни →
    /// выделяем объекты (Ctrl+A выделяет видимые) → грузим). Читаются лишь актуальные объекты —
    /// это на порядок быстрее полного чтения крупной модели.
    /// </summary>
    public HeatingModel ReadSelected()
    {
        var ids = GetSelectedObjectIds();
        return ReadModelCore(ids);   // пустой набор → пустая модель (ничего не выделено)
    }

    /// <summary>Текущее выделение в Renga: числовые Id объектов (пусто, если ничего не выделено).</summary>
    public IReadOnlySet<int> GetSelectedObjectIds()
    {
        var set = new HashSet<int>();
        try
        {
            // GetSelectedObjects возвращает нетипизированный System.Array (COM SAFEARRAY) —
            // элементы перебираем и приводим поштучно.
            var sel = _application.Selection?.GetSelectedObjects();
            if (sel is null) return set;
            foreach (var item in sel)
                if (item is int id) set.Add(id);
                else if (item is not null)
                    try { set.Add(Convert.ToInt32(item, CultureInfo.InvariantCulture)); } catch { }
        }
        catch { /* выделение недоступно — вернём пустой набор */ }
        return set;
    }

    /// <summary>
    /// Единый проход чтения. При onlyIds != null дорогое чтение (свойства/параметры/порты) делается
    /// только для объектов из набора. Связи восстанавливаются по карте «Id Renga → UniqueId» без
    /// вызовов GetById (иначе на десятках тысяч объектов получается O(N²) и минуты ожидания).
    /// </summary>
    private HeatingModel ReadModelCore(IReadOnlySet<int>? onlyIds)
    {
        var project = _application.Project
            ?? throw new InvalidOperationException("В Renga не открыт проект.");
        var model = new HeatingModel
        {
            Name = string.IsNullOrWhiteSpace(project.FilePath)
                ? "Проект Renga"
                : Path.GetFileNameWithoutExtension(project.FilePath),
        };

        var objects = project.Model.GetObjects();
        var tally = new Dictionary<Guid, (int Total, int Kept, string Sample)>();
        var levelNames = new Dictionary<int, string>();
        var idToUid = new Dictionary<int, string>();   // Renga Id → UniqueId, для связей без GetById
        var routes = new List<(int Src, int Tgt, int SrcPort, int TgtPort)>();

        var count = objects.Count;
        for (var i = 0; i < count; i++)
        {
            try
            {
                var mo = objects.GetByIndex(i);
                if (mo is null) continue;

                // Имена уровней собираем всегда (объект-уровень определяем по интерфейсу ILevel).
                try
                {
                    if (mo.GetInterfaceByName("ILevel") != null)
                        levelNames[mo.Id] = string.IsNullOrWhiteSpace(mo.Name) ? $"Уровень {mo.Id}" : mo.Name;
                }
                catch { /* не уровень */ }

                // Фильтр выделения: пропускаем ненужные объекты ДО дорогого чтения свойств.
                if (onlyIds is not null && !onlyIds.Contains(mo.Id)) continue;

                var eng = IsEngineering(mo);
                Guid type; try { type = mo.ObjectType; } catch { type = Guid.Empty; }
                tally.TryGetValue(type, out var t);
                tally[type] = (t.Total + 1, t.Kept + (eng ? 1 : 0),
                    string.IsNullOrEmpty(t.Sample) ? SafeName(mo) : t.Sample);
                if (!eng) continue;

                var uid = mo.UniqueIdS;
                idToUid[mo.Id] = uid;
                var obj = new NetworkObject { Id = uid, Name = mo.Name ?? string.Empty, RengaTypeId = mo.ObjectTypeS };

                // Уровень объекта (LevelId через кэшированную рефлексию — имя резолвим после прохода).
                if (!_levelIdProbed)
                {
                    _levelIdProbed = true;
                    try
                    {
                        var direct = typeof(Renga.IModelObject).GetProperty("LevelId");
                        if (direct is not null) { _levelIdProp = direct; _levelIdOnModelObject = true; }
                        else
                            _levelIdProp = typeof(Renga.IModelObject).Assembly
                                .GetType("Renga.ILevelObject")?.GetProperty("LevelId");
                    }
                    catch { /* уровни недоступны в этой версии API */ }
                }
                if (_levelIdProp is not null)
                    try
                    {
                        object? owner = _levelIdOnModelObject ? mo : mo.GetInterfaceByName("ILevelObject");
                        if (owner is not null && _levelIdProp.GetValue(owner) is int lid) obj.LevelId = lid;
                    }
                    catch { /* объект вне уровня */ }

                ReadProperties(mo, obj);
                ReadParameters(mo, obj);
                ReadQuantities(mo, obj);
                ReadPorts(mo, obj);

                // Сташим концы трассы (числовые Id) — резолвим в UniqueId после прохода, без GetById.
                try
                {
                    if (mo.GetInterfaceByName("IRouteParams") is Renga.IRouteParams route)
                        routes.Add((route.SourceModelObjectId, route.TargetModelObjectId,
                                    route.SourcePortIndex, route.TargetPortIndex));
                }
                catch { /* нет трассы */ }

                if (!model.Objects.ContainsKey(uid)) model.Add(obj);
            }
            catch { /* нечитаемый объект пропускаем */ }
        }

        // Имена уровней (уровень мог встретиться в коллекции позже своих объектов).
        foreach (var obj in model.Objects.Values)
            if (obj.LevelId is { } lid && levelNames.TryGetValue(lid, out var ln))
                obj.LevelName = ln;

        // Связи по сташированным трассам через карту Id→UniqueId (O(1) на связь).
        // Порядок Src→Tgt трассы сохраняем как смоделированную ориентацию — по ней работает
        // аудит направлений (расчёт этой ориентации не доверяет).
        foreach (var (src, tgt, sp, tp) in routes)
        {
            if (!idToUid.TryGetValue(src, out var srcUid) || !idToUid.TryGetValue(tgt, out var tgtUid)) continue;
            if (!model.Objects.TryGetValue(srcUid, out var na) || !model.Objects.TryGetValue(tgtUid, out var nb)) continue;
            var sPort = sp.ToString(CultureInfo.InvariantCulture);
            var tPort = tp.ToString(CultureInfo.InvariantCulture);
            if (na.Ports.Any(p => p.Id == sPort) && nb.Ports.Any(p => p.Id == tPort))
                model.Connect(na, sPort, nb, tPort, modeledAtoB: true);
        }

        var withLevel = model.Objects.Values.Count(o => o.LevelId is not null);
        DumpTypeDiagnostics(tally, model.Objects.Count, levelNames.Count, withLevel);
        return model;
    }

    private static string SafeName(Renga.IModelObject mo)
    {
        try { return mo.Name ?? ""; } catch { return ""; }
    }

    /// <summary>
    /// Пишет распределение GUID-типов модели в %TEMP%\RengaHeat_types.log (перезаписью).
    /// Формат строки: «всего / инженерных : GUID : пример имени». По этому файлу видно, какие
    /// типы есть в проекте и почему объект попал/не попал в инженерную выборку.
    /// </summary>
    private static void DumpTypeDiagnostics(
        Dictionary<Guid, (int Total, int Kept, string Sample)> tally, int keptTotal,
        int levelCount, int objectsWithLevel)
    {
        try
        {
            var lines = new List<string>
            {
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] RengaHeat: распределение типов объектов модели",
                $"Всего типов: {tally.Count};  инженерных объектов прочитано: {keptTotal}",
                $"Уровней в модели: {levelCount};  объектов с привязкой к уровню: {objectsWithLevel}" +
                $"  (LevelId {( _levelIdProp is null ? "НЕ найден в API" : "читается: " + _levelIdProp.DeclaringType?.Name )})",
                "  всего / инж. : GUID типа : пример имени",
                "  ------------------------------------------",
            };
            foreach (var kv in tally.OrderByDescending(kv => kv.Value.Total))
                lines.Add($"  {kv.Value.Total,7} / {kv.Value.Kept,-7} : {kv.Key} : {kv.Value.Sample}");
            var path = Path.Combine(Path.GetTempPath(), "RengaHeat_types.log");
            File.WriteAllText(path, string.Join(Environment.NewLine, lines));
        }
        catch { /* диагностика не должна ронять чтение модели */ }
    }

    private static void ReadProperties(Renga.IModelObject mo, NetworkObject item)
    {
        var props = mo.GetProperties();
        if (props is null) return;
        var ids = props.GetIds();
        if (ids is null) return;
        for (var i = 0; i < ids.Count; i++)
        {
            var id = ids.Get(i);
            var p = props.Get(id);
            if (p is null || !p.HasValue()) continue;
            object? value = p.Type switch
            {
                Renga.PropertyType.PropertyType_Angle => p.GetAngleValue(Renga.AngleUnit.AngleUnit_Degrees),
                Renga.PropertyType.PropertyType_Area => p.GetAreaValue(Renga.AreaUnit.AreaUnit_Meters2),
                Renga.PropertyType.PropertyType_Boolean => p.GetBooleanValue(),
                Renga.PropertyType.PropertyType_Double => p.GetDoubleValue(),
                Renga.PropertyType.PropertyType_Enumeration => p.GetEnumerationValue(),
                Renga.PropertyType.PropertyType_Integer => p.GetIntegerValue(),
                Renga.PropertyType.PropertyType_Length => p.GetLengthValue(Renga.LengthUnit.LengthUnit_Meters),
                Renga.PropertyType.PropertyType_Logical => p.GetLogicalValue(),
                Renga.PropertyType.PropertyType_Mass => p.GetMassValue(Renga.MassUnit.MassUnit_Kilograms),
                Renga.PropertyType.PropertyType_String => p.GetStringValue(),
                Renga.PropertyType.PropertyType_Volume => p.GetVolumeValue(Renga.VolumeUnit.VolumeUnit_Meters3),
                _ => null,
            };
            item.Properties[id] = new PropertyValue(id, p.Name ?? id.ToString(), value);
        }
    }

    private static void ReadParameters(Renga.IModelObject mo, NetworkObject item)
    {
        var pars = mo.GetParameters();
        if (pars is null) return;
        var ids = pars.GetIds();
        if (ids is null) return;
        for (var i = 0; i < ids.Count; i++)
        {
            var par = pars.Get(ids.Get(i));
            if (par is null || !par.HasValue) continue;
            object? value = par.ValueType switch
            {
                Renga.ParameterValueType.ParameterValueType_Bool => par.GetBoolValue(),
                Renga.ParameterValueType.ParameterValueType_Double => par.GetDoubleValue(),
                Renga.ParameterValueType.ParameterValueType_Int => par.GetIntValue(),
                Renga.ParameterValueType.ParameterValueType_String => par.GetStringValue(),
                _ => null,
            };
            var name = par.Definition?.Name;      // Definition может быть null у нетипизированных параметров
            if (!string.IsNullOrEmpty(name))
                item.Parameters[name] = value;
        }
    }

    private static void ReadQuantities(Renga.IModelObject mo, NetworkObject item)
    {
        var quantities = mo.GetQuantities();
        if (quantities is null) return;
        var q = quantities.Get(Renga.Quantities.NominalLength);
        if (q is not null && q.HasValue() && q.Type == Renga.QuantityType.QuantityType_Length)
            item.Quantities[q.Name ?? "Длина"] = q.AsLength(Renga.LengthUnit.LengthUnit_Meters);
    }

    private static void ReadPorts(Renga.IModelObject mo, NetworkObject item)
    {
        if (mo is not Renga.IEntityWithPorts withPorts) return;
        for (var i = 0; i < withPorts.Count; i++)
        {
            var port = withPorts.GetByIndex(i);
            var pipeParams = port?.PortConnectionParams as Renga.IPortPipeParams;
            var (x, y, z) = port is null ? ((double?)null, (double?)null, (double?)null) : PortOrigin(port);
            item.Ports.Add(new Port(
                Id: i.ToString(CultureInfo.InvariantCulture),
                Dn: pipeParams is null ? null : (int)Math.Round(pipeParams.NominalDiameter),
                ConnectionType: pipeParams?.ConnectionType.ToString(),
                Xmm: x, Ymm: y, Zmm: z));
        }
    }

    /// <summary>
    /// Глобальные координаты порта, мм (для автосоединения близких свободных точек трассировки).
    /// API размещения порта отличается между версиями Renga, поэтому свойства ищутся рефлексией
    /// по интерфейсу IPort (Placement → Origin → X/Y/Z) и кэшируются. Нет свойств — нет координат.
    /// </summary>
    private static (double?, double?, double?) PortOrigin(Renga.IPort port)
    {
        try
        {
            if (!_portGeomProbed)
            {
                _portGeomProbed = true;
                _portPlacementProp = typeof(Renga.IPort).GetProperty("Placement")
                                     ?? typeof(Renga.IPort).GetProperty("Placement3D");
            }
            var placement = _portPlacementProp?.GetValue(port);
            if (placement is null) return (null, null, null);
            // Для COM-объектов GetType() даёт __ComObject без свойств — ищем по типу интерфейса,
            // объявленному в interop-сборке (PropertyType), а не по типу экземпляра.
            _placementOriginProp ??= _portPlacementProp!.PropertyType.GetProperty("Origin")
                                     ?? _portPlacementProp.PropertyType.GetProperty("OriginPoint");
            var origin = _placementOriginProp?.GetValue(placement);
            if (origin is null) return (null, null, null);
            var ot = _placementOriginProp!.PropertyType;
            double? Coord(string name) => ot.GetProperty(name)?.GetValue(origin) switch
            {
                double d => d,
                float f => f,
                _ => null,
            };
            return (Coord("X"), Coord("Y"), Coord("Z"));
        }
        catch { return (null, null, null); }
    }

    /// <summary>
    /// Выделить объект в Renga по устойчивому идентификатору (UniqueIdS) — для перехода из таблиц UI.
    /// Selection API принимает числовые Id объектов; получаем объект по Guid и берём его Id.
    /// </summary>
    public void SelectByUniqueId(string uniqueIdS)
    {
        try
        {
            if (!Guid.TryParse(uniqueIdS, out var guid)) return;
            var project = _application.Project;
            if (project is null) return;
            var mo = project.Model.GetObjects().GetByUniqueId(guid);
            if (mo is null) return;
            _application.Selection.SetSelectedObjects(new[] { mo.Id });
        }
        catch { /* переход к объекту не должен ронять UI */ }
    }

    /// <summary>
    /// UniqueId объектов, выделенных сейчас в Renga (для переназначения ролей из «Карты»).
    /// Малое выделение резолвится точечно через GetById; большое — одним проходом коллекции,
    /// чтобы не получить O(N²) на крупных моделях.
    /// </summary>
    public IReadOnlyList<string> GetSelectedUniqueIds()
    {
        try
        {
            var selected = GetSelectedObjectIds();
            if (selected.Count == 0) return Array.Empty<string>();
            var objects = _application.Project?.Model.GetObjects();
            if (objects is null) return Array.Empty<string>();

            var result = new List<string>(selected.Count);
            if (selected.Count <= 64)
            {
                foreach (var id in selected)
                    try
                    {
                        var mo = objects.GetById(id);
                        if (mo is not null) result.Add(mo.UniqueIdS);
                    }
                    catch { /* объект мог быть удалён */ }
            }
            else
            {
                var count = objects.Count;
                for (var i = 0; i < count && result.Count < selected.Count; i++)
                    try
                    {
                        var mo = objects.GetByIndex(i);
                        if (mo is not null && selected.Contains(mo.Id)) result.Add(mo.UniqueIdS);
                    }
                    catch { /* нечитаемый объект пропускаем */ }
            }
            return result;
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Выделить в Renga сразу набор объектов по устойчивым идентификаторам (подсветка группы).</summary>
    public void SelectManyByUniqueId(IReadOnlyList<string> uniqueIds)
    {
        try
        {
            var project = _application.Project;
            if (project is null) return;
            var objects = project.Model.GetObjects();
            var ids = new List<int>();
            foreach (var u in uniqueIds)
            {
                if (!Guid.TryParse(u, out var guid)) continue;
                var mo = objects.GetByUniqueId(guid);
                if (mo is not null) ids.Add(mo.Id);
            }
            if (ids.Count > 0) _application.Selection.SetSelectedObjects(ids.ToArray());
        }
        catch { /* подсветка не должна ронять UI */ }
    }

    public ApplyReport ApplyChanges(IReadOnlyList<ModelChange> approvedChanges)
    {
        // Режим «только анализ»: запись отключена. Чтобы включить запись результатов в Renga,
        // задайте свойства-приёмники (GUID), поднимите соответствующие флаги Capabilities и
        // реализуйте запись здесь через операцию проекта (Model.CreateOperation → Start/Apply)
        // для корректной поддержки Undo.
        var report = new ApplyReport();
        foreach (var change in approvedChanges)
            report.Items.Add(new ApplyResultItem(change, false,
                "Режим только анализа: настройте сопоставление свойств-приёмников и подтвердите " +
                "предпросмотр, чтобы включить запись."));
        return report;
    }
}

#else

/// <summary>Заглушка адаптера для сред без Renga SDK. Реальную реализацию см. выше.</summary>
public sealed class RengaModelGateway : IModelGateway
{
    private const string Msg =
        "Renga SDK не подключён. Соберите проект RengaHeat.RengaPlugin на машине с Renga SDK.";

    public GatewayCapabilities Capabilities { get; } = new(false, false, false, false, false, false);
    public HeatingModel ReadModel() => throw new PlatformNotSupportedException(Msg);
    public ApplyReport ApplyChanges(IReadOnlyList<ModelChange> approvedChanges) =>
        throw new PlatformNotSupportedException(Msg);
}

#endif
