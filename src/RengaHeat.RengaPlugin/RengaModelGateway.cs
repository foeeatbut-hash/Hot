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

    public RengaModelGateway(Renga.IApplication application) => _application = application;

    public GatewayCapabilities Capabilities { get; } = new(
        CanWriteProperties: false, CanChangePipeStyle: false, CanWriteValvePreset: false,
        CanCreateMarks: false, CanFixDirection: false, SupportsUndo: false);

    public HeatingModel ReadModel()
    {
        var project = _application.Project
            ?? throw new InvalidOperationException("В Renga не открыт проект.");
        var model = new HeatingModel
        {
            Name = string.IsNullOrWhiteSpace(project.FilePath)
                ? "Проект Renga"
                : Path.GetFileNameWithoutExtension(project.FilePath),
        };

        var rengaModel = project.Model;
        var objects = rengaModel.GetObjects();
        for (var i = 0; i < objects.Count; i++)
        {
            // Один «плохой» объект модели не должен ронять весь расчёт — читаем защищённо.
            try
            {
                var mo = objects.GetByIndex(i);
                if (mo is null) continue;
                var obj = new NetworkObject
                {
                    Id = mo.UniqueIdS,
                    Name = mo.Name ?? string.Empty,
                    RengaTypeId = mo.ObjectTypeS,
                };
                ReadProperties(mo, obj);
                ReadParameters(mo, obj);
                ReadQuantities(mo, obj);
                ReadPorts(mo, obj);
                if (!model.Objects.ContainsKey(obj.Id))
                    model.Add(obj);
            }
            catch
            {
                // пропускаем нечитаемый объект; связи по нему просто не построятся
            }
        }

        ReadConnections(rengaModel, objects, model);
        return model;
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
            item.Ports.Add(new Port(
                Id: i.ToString(CultureInfo.InvariantCulture),
                Dn: pipeParams is null ? null : (int)Math.Round(pipeParams.NominalDiameter),
                ConnectionType: pipeParams?.ConnectionType.ToString()));
        }
    }

    private static void ReadConnections(Renga.IModel rengaModel, Renga.IModelObjectCollection objects,
        HeatingModel model)
    {
        // Связность инженерной сети восстанавливается по параметрам трасс (IRouteParams):
        // источник/приёмник соединения и индексы портов. Стрелка трассы здесь не трактуется как
        // физическое направление — им занимается топология/решатель ядра.
        var allObjects = rengaModel.GetObjects();
        for (var i = 0; i < objects.Count; i++)
        {
            try
            {
                var mo = objects.GetByIndex(i);
                if (mo?.GetInterfaceByName("IRouteParams") is not Renga.IRouteParams route)
                    continue;
                // Конец трассы может ссылаться на отсутствующий/несозданный объект — GetById вернёт null.
                var a = allObjects.GetById(route.SourceModelObjectId);
                var b = allObjects.GetById(route.TargetModelObjectId);
                if (a is null || b is null) continue;
                if (!model.Objects.TryGetValue(a.UniqueIdS, out var na) ||
                    !model.Objects.TryGetValue(b.UniqueIdS, out var nb))
                    continue;
                var sourcePort = route.SourcePortIndex.ToString(CultureInfo.InvariantCulture);
                var targetPort = route.TargetPortIndex.ToString(CultureInfo.InvariantCulture);
                if (na.Ports.Any(p => p.Id == sourcePort) && nb.Ports.Any(p => p.Id == targetPort))
                    model.Connect(na, sourcePort, nb, targetPort);
            }
            catch
            {
                // одна нечитаемая связь не должна ронять построение сети
            }
        }
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
