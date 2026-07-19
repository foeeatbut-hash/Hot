using RengaHeat.Core.Calculation;
using RengaHeat.Core.Model;

namespace RengaHeat.Core.Adapter;

/// <summary>
/// Реализация шлюза в оперативной памяти для тестов, CLI-демонстраций и черновиков без Renga.
/// Изменения применяются к свойствам объектов модели и полностью обратимы (хранит снимок для отмены).
/// </summary>
public sealed class InMemoryModelGateway(HeatingModel model) : IModelGateway
{
    private readonly Dictionary<string, Dictionary<Guid, PropertyValue>> _undo = new();

    public GatewayCapabilities Capabilities { get; } = new(
        CanWriteProperties: true, CanChangePipeStyle: false, CanWriteValvePreset: true,
        CanCreateMarks: false, CanFixDirection: false, SupportsUndo: true);

    public HeatingModel ReadModel() => model;

    public ApplyReport ApplyChanges(IReadOnlyList<ModelChange> approvedChanges)
    {
        var report = new ApplyReport();
        foreach (var change in approvedChanges)
        {
            if (!Capabilities.Supports(change.Kind))
            {
                report.Items.Add(new ApplyResultItem(change, false,
                    $"Вид изменения «{change.Kind}» не поддерживается этим шлюзом."));
                continue;
            }
            if (!model.Objects.TryGetValue(change.ObjectId, out var obj))
            {
                report.Items.Add(new ApplyResultItem(change, false, "Объект не найден в модели."));
                continue;
            }
            // Снимок для отмены
            if (!_undo.ContainsKey(obj.Id))
                _undo[obj.Id] = new Dictionary<Guid, PropertyValue>(obj.Properties);

            var stableId = DeterministicGuid(change.Target);
            obj.Properties[stableId] = new PropertyValue(stableId, change.Target, change.NewValue);
            report.Items.Add(new ApplyResultItem(change, true, null));
        }
        return report;
    }

    /// <summary>Отмена всех применённых изменений (аналог Undo).</summary>
    public void Undo()
    {
        foreach (var (objectId, snapshot) in _undo)
        {
            var obj = model.Objects[objectId];
            obj.Properties.Clear();
            foreach (var (k, v) in snapshot) obj.Properties[k] = v;
        }
        _undo.Clear();
    }

    private static Guid DeterministicGuid(string name)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(name));
        return new Guid(bytes);
    }
}
