// Renga-адаптер: реализация IModelGateway поверх официального Renga API/COM.
//
// Компилируется только при наличии Renga SDK (константа RENGA_SDK_ABSENT НЕ задана).
// Без SDK вместо адаптера компилируется заглушка в конце файла, чтобы проект не падал.
//
// Соответствие требованиям ТЗ:
//  - все изменения выполняются в операции проекта (IOperation) с поддержкой Undo;
//  - никаких необратимых изменений: применяются только свойства/результаты и преднастройки;
//  - устойчивые идентификаторы объектов и свойств (GUID) сохраняются в модель ядра;
//  - роли, направления и расчёт остаются в ядре — адаптер только читает и записывает.

using RengaHeat.Core.Adapter;
using RengaHeat.Core.Calculation;
using RengaHeat.Core.Model;

namespace RengaHeat.RengaPlugin;

#if !RENGA_SDK_ABSENT

/// <summary>
/// Шлюз к модели Renga. Читает объекты, свойства, параметры, количества и порты в HeatingModel;
/// применяет подтверждённые изменения в одной операции проекта с поддержкой Undo.
/// </summary>
public sealed class RengaModelGateway : IModelGateway
{
    private readonly Renga.IApplication _application;

    public RengaModelGateway(Renga.IApplication application) => _application = application;

    public GatewayCapabilities Capabilities { get; } = new(
        CanWriteProperties: true,
        CanChangePipeStyle: false,   // смена стиля трубы — только после отдельного подтверждения и проверки API
        CanWriteValvePreset: true,
        CanCreateMarks: false,
        CanFixDirection: false,      // безопасное исправление направления зависит от поддержки API вашей версии
        SupportsUndo: true);

    public HeatingModel ReadModel()
    {
        var project = _application.Project
            ?? throw new InvalidOperationException("В Renga не открыт проект.");
        var model = new HeatingModel { Name = project.Name ?? "Проект Renga" };

        var rengaModel = project.Model;
        var objects = rengaModel.GetObjects();
        for (var i = 0; i < objects.Count; i++)
        {
            var mo = objects.Get(i);
            var obj = new NetworkObject
            {
                Id = mo.Id.ToString(),
                Name = mo.Name ?? "",
                RengaTypeId = mo.ObjectType.ToString(),
            };

            ReadProperties(mo, obj);
            ReadParameters(mo, obj);
            ReadQuantities(mo, obj);
            ReadPorts(mo, obj);

            model.Add(obj);
        }

        ReadConnections(rengaModel, model);
        return model;
    }

    private static void ReadProperties(Renga.IModelObject mo, NetworkObject obj)
    {
        var props = mo.GetProperties();
        if (props is null) return;
        var ids = props.GetIds();
        for (var i = 0; i < ids.Count; i++)
        {
            var id = ids.Get(i);
            var p = props.Get(id);
            if (p is null || !p.HasValue()) continue;
            object? value = p.Type switch
            {
                Renga.PropertyType.PropertyType_Double => p.GetDoubleValue(),
                Renga.PropertyType.PropertyType_Integer => p.GetIntegerValue(),
                Renga.PropertyType.PropertyType_String => p.GetStringValue(),
                Renga.PropertyType.PropertyType_Logical => p.GetLogicalValue(),
                Renga.PropertyType.PropertyType_Enumeration => p.GetEnumerationValue(),
                _ => p.GetStringValue(),
            };
            obj.Properties[id] = new PropertyValue(id, p.Name ?? id.ToString(), value);
        }
    }

    private static void ReadParameters(Renga.IModelObject mo, NetworkObject obj)
    {
        var pars = mo.GetParameters();
        if (pars is null) return;
        var ids = pars.GetIds();
        for (var i = 0; i < ids.Count; i++)
        {
            var name = ids.Get(i);
            var par = pars.Get(name);
            if (par is null) continue;
            obj.Parameters[name] = par.GetDoubleValue();
        }
    }

    private static void ReadQuantities(Renga.IModelObject mo, NetworkObject obj)
    {
        var q = mo.GetQuantities();
        if (q is null) return;
        // Читаем распространённые количества; имена/типы уточните по вашей версии Renga API.
        TryQuantity(q, Renga.QuantityIds.Length, "Длина", obj);
        TryQuantity(q, Renga.QuantityIds.Area, "Площадь", obj);
        TryQuantity(q, Renga.QuantityIds.NominalDiameter, "Ду", obj);
    }

    private static void TryQuantity(Renga.IQuantityContainer q, Guid id, string name, NetworkObject obj)
    {
        var quantity = q.Get(id);
        if (quantity is null) return;
        // Значения в СИ; при необходимости переводим единицы через ядро (UnitRegistry).
        obj.Quantities[name] = quantity.AsLength(Renga.LengthUnit.LengthUnit_Meters);
    }

    private static void ReadPorts(Renga.IModelObject mo, NetworkObject obj)
    {
        // Renga предоставляет соединительные точки инженерного оборудования/труб.
        // Точный интерфейс портов зависит от версии SDK; здесь — типовой обход.
        var ports = mo.GetConnectionPoints?.Invoke();
        if (ports is null) return;
        for (var i = 0; i < ports.Count; i++)
        {
            var cp = ports.Get(i);
            obj.Ports.Add(new Port(
                Id: cp.Id.ToString(),
                Dn: cp.NominalDiameter > 0 ? cp.NominalDiameter : null,
                ConnectionType: cp.ConnectionType));
        }
    }

    private static void ReadConnections(Renga.IModel rengaModel, HeatingModel model)
    {
        // Связи труб/оборудования: реализация зависит от версии API.
        // Общая схема — по совпадению координат/идентификаторов соединительных точек.
        // Здесь оставлен явный расширяемый хук; в вашей версии Renga используйте
        // соответствующий интерфейс связности сети инженерного оборудования.
    }

    public ApplyReport ApplyChanges(IReadOnlyList<ModelChange> approvedChanges)
    {
        var report = new ApplyReport();
        if (approvedChanges.Count == 0) return report;

        var project = _application.Project
            ?? throw new InvalidOperationException("В Renga не открыт проект.");
        var rengaModel = project.Model;

        // Одна операция на весь пакет — обеспечивает атомарность и корректный Undo.
        var operation = rengaModel.CreateOperation();
        operation.Start();
        try
        {
            foreach (var change in approvedChanges)
            {
                if (!Capabilities.Supports(change.Kind))
                {
                    report.Items.Add(new ApplyResultItem(change, false,
                        $"Вид изменения «{change.Kind}» не поддерживается адаптером Renga."));
                    continue;
                }
                if (!Guid.TryParse(change.ObjectId, out var objectId))
                {
                    report.Items.Add(new ApplyResultItem(change, false, "Некорректный идентификатор объекта."));
                    continue;
                }
                var mo = FindObject(rengaModel, objectId);
                if (mo is null)
                {
                    report.Items.Add(new ApplyResultItem(change, false, "Объект не найден в модели Renga."));
                    continue;
                }

                var ok = ApplyOne(mo, change, out var error);
                report.Items.Add(new ApplyResultItem(change, ok, error));
            }

            if (report.AppliedCount > 0)
                operation.Apply();   // фиксируем как одно undo-действие
            else
                operation.Rollback();
        }
        catch
        {
            operation.Rollback();
            throw;
        }
        return report;
    }

    private static bool ApplyOne(Renga.IModelObject mo, ModelChange change, out string? error)
    {
        error = null;
        try
        {
            var props = mo.GetProperties();
            var stableId = DeterministicGuid(change.Target);
            var existing = props.Get(stableId);
            if (existing is null)
            {
                // Создаём пользовательское свойство результата, если его ещё нет.
                props.Add(stableId, change.Target, Renga.PropertyType.PropertyType_Double);
                existing = props.Get(stableId);
            }
            switch (change.NewValue)
            {
                case double d: existing!.SetDoubleValue(d); break;
                case int n: existing!.SetIntegerValue(n); break;
                case bool b: existing!.SetLogicalValue(b); break;
                default: existing!.SetStringValue(change.NewValue?.ToString() ?? ""); break;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static Renga.IModelObject? FindObject(Renga.IModel rengaModel, Guid id)
    {
        var objects = rengaModel.GetObjects();
        for (var i = 0; i < objects.Count; i++)
        {
            var mo = objects.Get(i);
            if (mo.Id == id) return mo;
        }
        return null;
    }

    private static Guid DeterministicGuid(string name)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(name));
        return new Guid(bytes);
    }
}

#else

/// <summary>
/// Заглушка адаптера для сред без Renga SDK. Реальную реализацию см. выше под RENGA_SDK_ABSENT.
/// </summary>
public sealed class RengaModelGateway : IModelGateway
{
    private const string Msg =
        "Renga SDK не подключён. Соберите проект RengaHeat.RengaPlugin на машине с Renga SDK " +
        "(задайте RengaSdkDir), чтобы получить рабочий адаптер.";

    public GatewayCapabilities Capabilities { get; } =
        new(false, false, false, false, false, false);

    public HeatingModel ReadModel() => throw new PlatformNotSupportedException(Msg);
    public ApplyReport ApplyChanges(IReadOnlyList<ModelChange> approvedChanges) =>
        throw new PlatformNotSupportedException(Msg);
}

#endif
