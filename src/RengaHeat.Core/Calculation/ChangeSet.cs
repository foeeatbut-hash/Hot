namespace RengaHeat.Core.Calculation;

/// <summary>Вид изменения модели Renga, предлагаемого плагином.</summary>
public enum ChangeKind
{
    SetProperty,        // записать значение свойства/результата
    SetPipeStyle,       // сменить стиль трубы (диаметр)
    SetValvePreset,     // записать преднастройку клапана
    SetMark,            // маркировка/выноска
    FixDirection,       // безопасное исправление направления, поддержанное API
}

/// <summary>
/// Одно предлагаемое изменение модели. Ничего не применяется без предпросмотра
/// и явного подтверждения инженера. Хранит старое и новое значение для отмены.
/// </summary>
public sealed record ModelChange(
    ChangeKind Kind,
    string ObjectId,
    string ObjectName,
    string Target,          // что меняется: имя свойства, «Стиль», «Преднастройка n» …
    object? OldValue,
    object? NewValue,
    string Reason,
    bool ApiSupported = true)
{
    public bool Approved { get; set; }
    public override string ToString() =>
        $"{ObjectName}: {Target}: {OldValue ?? "—"} → {NewValue ?? "—"} ({Reason})";
}

/// <summary>
/// Пакет предлагаемых изменений (таблица предпросмотра). По умолчанию плагин работает в режиме
/// «только анализ»: набор формируется, но применяется исключительно после подтверждения.
/// Применение выполняет Renga-адаптер в одной операции проекта с поддержкой Undo.
/// </summary>
public sealed class ChangeSet
{
    private readonly List<ModelChange> _changes = new();
    public IReadOnlyList<ModelChange> Changes => _changes;

    public void Add(ModelChange change) => _changes.Add(change);

    public IEnumerable<ModelChange> Approved => _changes.Where(c => c.Approved);
    public IEnumerable<ModelChange> Unsupported => _changes.Where(c => !c.ApiSupported);

    public void ApproveAll() { foreach (var c in _changes) c.Approved = c.ApiSupported; }

    public IEnumerable<IGrouping<ChangeKind, ModelChange>> ByKind() => _changes.GroupBy(c => c.Kind);
}
