using RengaHeat.Core.Model;

namespace RengaHeat.Core.Mapping;

/// <summary>Результат обращения к источнику: сырое значение + описание, откуда оно взято.</summary>
public sealed record SourceReadResult(bool Found, object? RawValue, string SourceDescription)
{
    public static SourceReadResult Missing(string description) => new(false, null, description);
    public static SourceReadResult Ok(object? value, string description) => new(true, value, description);
}

/// <summary>Контекст разрешения значения: модель, каталоги, результаты предыдущего расчёта и т. д.</summary>
public sealed class ResolutionContext
{
    public required HeatingModel Model { get; init; }
    public Func<string, string, object?>? CatalogLookup { get; init; }          // (каталог, ключ) → значение
    public IReadOnlyDictionary<string, double>? ProfileConstants { get; init; }
    public Func<string, string, object?>? PreviousResults { get; init; }        // (objectId, ключ) → значение
    public IReadOnlyDictionary<string, Dictionary<string, string>>? Tables { get; init; } // CSV: имя → (ключ → значение)
    public Dictionary<(string ObjectId, string FieldKey), object?> ManualInputs { get; } = new();
    public Func<NetworkObject, string, object?>? FormulaVariableProvider { get; init; }
}

/// <summary>
/// Источник значения расчётного поля. Реализации покрывают все 12 обязательных типов из ТЗ.
/// </summary>
public abstract record ValueSource
{
    public abstract string Kind { get; }
    public abstract SourceReadResult Read(NetworkObject obj, FieldDefinition field, ResolutionContext ctx);
}

/// <summary>1. Пользовательское свойство экземпляра. Ищется по устойчивому GUID, имя — только резерв/отображение.</summary>
public sealed record InstancePropertySource(Guid PropertyStableId, string PropertyName) : ValueSource
{
    public override string Kind => "Свойство экземпляра";
    public override SourceReadResult Read(NetworkObject obj, FieldDefinition field, ResolutionContext ctx)
    {
        var prop = obj.Properties.GetValueOrDefault(PropertyStableId) ?? obj.FindPropertyByName(PropertyName);
        return prop is null
            ? SourceReadResult.Missing($"свойство «{PropertyName}» отсутствует")
            : SourceReadResult.Ok(prop.Value, $"свойство «{prop.Name}» = {prop.Value}");
    }
}

/// <summary>2. Свойство стиля.</summary>
public sealed record StylePropertySource(Guid PropertyStableId, string PropertyName) : ValueSource
{
    public override string Kind => "Свойство стиля";
    public override SourceReadResult Read(NetworkObject obj, FieldDefinition field, ResolutionContext ctx)
    {
        var prop = obj.StyleProperties.GetValueOrDefault(PropertyStableId) ?? obj.FindStylePropertyByName(PropertyName);
        return prop is null
            ? SourceReadResult.Missing($"свойство стиля «{PropertyName}» отсутствует")
            : SourceReadResult.Ok(prop.Value, $"свойство стиля «{prop.Name}» = {prop.Value}");
    }
}

/// <summary>3. Параметр объекта или стиля.</summary>
public sealed record ParameterSource(string ParameterName) : ValueSource
{
    public override string Kind => "Параметр";
    public override SourceReadResult Read(NetworkObject obj, FieldDefinition field, ResolutionContext ctx) =>
        obj.Parameters.TryGetValue(ParameterName, out var v) && v is not null
            ? SourceReadResult.Ok(v, $"параметр «{ParameterName}» = {v}")
            : SourceReadResult.Missing($"параметр «{ParameterName}» отсутствует");
}

/// <summary>4. Количество Renga (длина, площадь, номинальный размер…).</summary>
public sealed record QuantitySource(string QuantityName) : ValueSource
{
    public override string Kind => "Количество Renga";
    public override SourceReadResult Read(NetworkObject obj, FieldDefinition field, ResolutionContext ctx) =>
        obj.Quantities.TryGetValue(QuantityName, out var v)
            ? SourceReadResult.Ok(v, $"количество «{QuantityName}» = {v}")
            : SourceReadResult.Missing($"количество «{QuantityName}» отсутствует");
}

/// <summary>5. Данные порта: DN, тип соединения, число подключений.</summary>
public sealed record PortDataSource(PortDataKind Data, int PortIndex = 0) : ValueSource
{
    public override string Kind => "Данные порта";
    public override SourceReadResult Read(NetworkObject obj, FieldDefinition field, ResolutionContext ctx)
    {
        if (Data == PortDataKind.ConnectionCount)
            return SourceReadResult.Ok(obj.Ports.Count(p => p.IsConnected), "число подключённых портов");
        if (PortIndex >= obj.Ports.Count)
            return SourceReadResult.Missing($"порт №{PortIndex + 1} отсутствует");
        var port = obj.Ports[PortIndex];
        return Data switch
        {
            PortDataKind.Dn => port.Dn is int dn
                ? SourceReadResult.Ok(dn, $"DN порта «{port.Id}» = {dn}")
                : SourceReadResult.Missing($"у порта «{port.Id}» не задан DN"),
            PortDataKind.ConnectionType => port.ConnectionType is { } t
                ? SourceReadResult.Ok(t, $"тип соединения порта «{port.Id}» = {t}")
                : SourceReadResult.Missing($"у порта «{port.Id}» не задан тип соединения"),
            _ => SourceReadResult.Missing("неизвестный вид данных порта"),
        };
    }
}

public enum PortDataKind { Dn, ConnectionType, ConnectionCount }

/// <summary>6. Геометрия и контекст: этаж, помещение, секция, система, отметка, длина трассы.</summary>
public sealed record ContextSource(ContextDataKind Data) : ValueSource
{
    public override string Kind => "Контекст";
    public override SourceReadResult Read(NetworkObject obj, FieldDefinition field, ResolutionContext ctx)
    {
        object? v = Data switch
        {
            ContextDataKind.Floor => obj.Context.Floor,
            ContextDataKind.Room => obj.Context.Room,
            ContextDataKind.Section => obj.Context.Section,
            ContextDataKind.System => obj.Context.SystemName,
            ContextDataKind.Elevation => obj.Context.ElevationM,
            ContextDataKind.Building => obj.Context.Building,
            ContextDataKind.Apartment => obj.Context.Apartment,
            _ => null,
        };
        return v is null
            ? SourceReadResult.Missing($"контекст «{Data}» не задан")
            : SourceReadResult.Ok(v, $"контекст «{Data}» = {v}");
    }
}

public enum ContextDataKind { Floor, Room, Section, System, Elevation, Building, Apartment }

/// <summary>7. Каталог по стилю, артикулу, производителю или коду.</summary>
public sealed record CatalogSource(string CatalogName, CatalogKeyKind KeyKind) : ValueSource
{
    public override string Kind => "Каталог";
    public override SourceReadResult Read(NetworkObject obj, FieldDefinition field, ResolutionContext ctx)
    {
        if (ctx.CatalogLookup is null)
            return SourceReadResult.Missing($"каталог «{CatalogName}» не подключён");
        var key = KeyKind switch
        {
            CatalogKeyKind.Article => obj.Article,
            CatalogKeyKind.Style => obj.StyleName,
            CatalogKeyKind.Name => obj.Name,
            _ => null,
        };
        if (string.IsNullOrEmpty(key))
            return SourceReadResult.Missing($"у объекта нет ключа «{KeyKind}» для каталога «{CatalogName}»");
        var v = ctx.CatalogLookup(CatalogName, key);
        return v is null
            ? SourceReadResult.Missing($"в каталоге «{CatalogName}» нет записи «{key}»")
            : SourceReadResult.Ok(v, $"каталог «{CatalogName}», ключ «{key}» = {v}");
    }
}

public enum CatalogKeyKind { Article, Style, Name }

/// <summary>8. Константа профиля требований (ЧТУ/организации/проекта).</summary>
public sealed record ProfileConstantSource(string ConstantKey) : ValueSource
{
    public override string Kind => "Константа профиля";
    public override SourceReadResult Read(NetworkObject obj, FieldDefinition field, ResolutionContext ctx) =>
        ctx.ProfileConstants is not null && ctx.ProfileConstants.TryGetValue(ConstantKey, out var v)
            ? SourceReadResult.Ok(v, $"константа профиля «{ConstantKey}» = {v}")
            : SourceReadResult.Missing($"константа профиля «{ConstantKey}» не задана");
}

/// <summary>9. Формула над другими значениями объекта (см. FormulaEvaluator).</summary>
public sealed record FormulaSource(string Expression) : ValueSource
{
    public override string Kind => "Формула";
    public override SourceReadResult Read(NetworkObject obj, FieldDefinition field, ResolutionContext ctx)
    {
        try
        {
            var result = FormulaEvaluator.Evaluate(Expression,
                name => ctx.FormulaVariableProvider?.Invoke(obj, name));
            return SourceReadResult.Ok(result, $"формула «{Expression}» = {result}");
        }
        catch (Exception ex)
        {
            return SourceReadResult.Missing($"формула «{Expression}» не вычислена: {ex.Message}");
        }
    }
}

/// <summary>10. Таблица Excel/CSV по ключу (таблицы загружаются в ResolutionContext.Tables).</summary>
public sealed record TableLookupSource(string TableName, CatalogKeyKind KeyKind, string ColumnName) : ValueSource
{
    public override string Kind => "Таблица Excel/CSV";
    public override SourceReadResult Read(NetworkObject obj, FieldDefinition field, ResolutionContext ctx)
    {
        if (ctx.Tables is null || !ctx.Tables.TryGetValue(TableName, out var table))
            return SourceReadResult.Missing($"таблица «{TableName}» не загружена");
        var key = KeyKind switch
        {
            CatalogKeyKind.Article => obj.Article,
            CatalogKeyKind.Style => obj.StyleName,
            _ => obj.Name,
        };
        if (string.IsNullOrEmpty(key) || !table.TryGetValue($"{key}|{ColumnName}", out var v))
            return SourceReadResult.Missing($"в таблице «{TableName}» нет «{key}» / «{ColumnName}»");
        return SourceReadResult.Ok(v, $"таблица «{TableName}»[{key}, {ColumnName}] = {v}");
    }
}

/// <summary>11. Ручной ввод инженера (хранится в контексте по объекту и полю).</summary>
public sealed record ManualInputSource : ValueSource
{
    public override string Kind => "Ручной ввод";
    public override SourceReadResult Read(NetworkObject obj, FieldDefinition field, ResolutionContext ctx) =>
        ctx.ManualInputs.TryGetValue((obj.Id, field.Key), out var v) && v is not null
            ? SourceReadResult.Ok(v, $"ручной ввод = {v}")
            : SourceReadResult.Missing("ручной ввод отсутствует");
}

/// <summary>12. Результат предыдущего расчёта.</summary>
public sealed record PreviousResultSource(string ResultKey) : ValueSource
{
    public override string Kind => "Результат предыдущего расчёта";
    public override SourceReadResult Read(NetworkObject obj, FieldDefinition field, ResolutionContext ctx)
    {
        var v = ctx.PreviousResults?.Invoke(obj.Id, ResultKey);
        return v is null
            ? SourceReadResult.Missing($"результат «{ResultKey}» предыдущего расчёта отсутствует")
            : SourceReadResult.Ok(v, $"предыдущий расчёт, «{ResultKey}» = {v}");
    }
}
