using RengaHeat.Core.Calculation;
using RengaHeat.Core.Model;

namespace RengaHeat.Core.Adapter;

/// <summary>
/// Шлюз модели: единственная точка соприкосновения ядра с источником данных (Renga или иным).
/// Ядро зависит только от этого интерфейса, а не от COM API — поэтому оно тестируется без Renga.
/// Renga-реализация живёт в отдельной сборке плагина (RengaHeat.RengaPlugin).
/// </summary>
public interface IModelGateway
{
    /// <summary>Считать снимок сети отопления из источника в расчётную модель.</summary>
    HeatingModel ReadModel();

    /// <summary>
    /// Применить подтверждённые изменения. Реализация Renga обязана выполнять их в одной операции
    /// проекта с поддержкой Undo и не делать необратимых изменений. Возвращает журнал применения.
    /// </summary>
    ApplyReport ApplyChanges(IReadOnlyList<ModelChange> approvedChanges);

    /// <summary>Возможности источника: какие виды изменений безопасно поддерживаются API.</summary>
    GatewayCapabilities Capabilities { get; }
}

public sealed record GatewayCapabilities(
    bool CanWriteProperties,
    bool CanChangePipeStyle,
    bool CanWriteValvePreset,
    bool CanCreateMarks,
    bool CanFixDirection,
    bool SupportsUndo)
{
    public bool Supports(ChangeKind kind) => kind switch
    {
        ChangeKind.SetProperty => CanWriteProperties,
        ChangeKind.SetPipeStyle => CanChangePipeStyle,
        ChangeKind.SetValvePreset => CanWriteValvePreset,
        ChangeKind.SetMark => CanCreateMarks,
        ChangeKind.FixDirection => CanFixDirection,
        _ => false,
    };
}

public sealed record ApplyResultItem(ModelChange Change, bool Applied, string? Error);

public sealed class ApplyReport
{
    public List<ApplyResultItem> Items { get; } = new();
    public bool AllApplied => Items.All(i => i.Applied);
    public int AppliedCount => Items.Count(i => i.Applied);
    public int SkippedCount => Items.Count(i => !i.Applied);
}
