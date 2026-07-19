using System.Globalization;
using RengaHeat.Core.Model;
using RengaHeat.Core.Units;

namespace RengaHeat.Core.Mapping;

/// <summary>
/// Правило сопоставления: какое расчётное поле, для каких ролей, из какой цепочки источников.
/// Цепочка — это резерв: если первый источник пуст, берётся следующий
/// (например: Q_расч экземпляра → мощность стиля → каталог по артикулу → ручной ввод → ошибка).
/// </summary>
public sealed record MappingRule
{
    public required FieldDefinition Field { get; init; }
    public required IReadOnlyList<ObjectRole> AppliesToRoles { get; init; }
    public required IReadOnlyList<ValueSource> SourceChain { get; init; }
    /// <summary>Единица, в которой источник хранит число (для преобразования к базовой единице поля).</summary>
    public string? SourceUnitSymbol { get; init; }
    public string Name { get; init; } = "";
    public int Priority { get; init; }

    public bool AppliesTo(NetworkObject obj) =>
        AppliesToRoles.Count == 0 || AppliesToRoles.Contains(obj.Role.Role);
}

/// <summary>Набор правил сопоставления проекта (раздел «Исходные данные → Сопоставление»).</summary>
public sealed class MappingSet
{
    private readonly List<MappingRule> _rules = new();
    public IReadOnlyList<MappingRule> Rules => _rules;
    public void Add(MappingRule rule) => _rules.Add(rule);

    public MappingRule? FindFor(NetworkObject obj, string fieldKey) =>
        _rules.Where(r => r.Field.Key == fieldKey && r.AppliesTo(obj))
              .OrderByDescending(r => r.Priority)
              .FirstOrDefault();
}

/// <summary>Одна запись журнала происхождения: полный путь значения для команды «Почему это значение?».</summary>
public sealed record ProvenanceRecord(
    string ObjectId,
    string ObjectName,
    ObjectRole Role,
    string FieldKey,
    string RuleName,
    string SourceKind,
    string SourceDescription,
    object? RawValue,
    string? Conversion,
    object? FinalValue,
    ResolveStatus Status,
    string? Message = null)
{
    /// <summary>«объект → роль → правило → источник → исходное значение → преобразование → результат».</summary>
    public string Explain() =>
        $"{ObjectName} → роль {RoleNames.Of(Role)} → правило «{RuleName}» → {SourceKind}: {SourceDescription}" +
        (Conversion is null ? "" : $" → {Conversion}") +
        $" → {FinalValue ?? "нет значения"}" +
        (Message is null ? "" : $" [{Message}]");
}

public enum ResolveStatus { Ok, Missing, Error, Assumption, NeedsDecision, Excluded }

public sealed record ResolvedValue(double? Number, object? Raw, ResolveStatus Status, ProvenanceRecord Provenance)
{
    public bool HasValue => Status is ResolveStatus.Ok or ResolveStatus.Assumption && Number is not null;
}

/// <summary>
/// Разрешает значения расчётных полей по настроенным правилам сопоставления,
/// с преобразованием единиц, проверкой диапазона и полным журналом происхождения.
/// </summary>
public sealed class ValueResolver(MappingSet mappings, ResolutionContext context, UnitRegistry? units = null)
{
    private readonly UnitRegistry _units = units ?? UnitRegistry.Default;
    private readonly List<ProvenanceRecord> _journal = new();

    /// <summary>Журнал происхождения всех разрешённых значений (для отчёта и «Почему это значение?»).</summary>
    public IReadOnlyList<ProvenanceRecord> Journal => _journal;

    public ResolvedValue Resolve(NetworkObject obj, FieldDefinition field)
    {
        var rule = mappings.FindFor(obj, field.Key);
        if (rule is null)
            return Record(obj, field, "(нет правила)", "—", "поле не сопоставлено", null, null, null,
                ResolveStatus.Missing, "Для роли объекта не настроено сопоставление поля.");

        foreach (var source in rule.SourceChain)
        {
            var read = source.Read(obj, field, context);
            if (!read.Found)
                continue;

            if (field.DataType is FieldDataType.Text or FieldDataType.Boolean)
                return Record(obj, field, rule.Name, source.Kind, read.SourceDescription,
                    read.RawValue, null, read.RawValue, ResolveStatus.Ok);

            if (!TryToDouble(read.RawValue, out var raw))
            {
                if (field.EmptyPolicy == InvalidValuePolicy.TreatAsMissing) continue;
                return Record(obj, field, rule.Name, source.Kind, read.SourceDescription,
                    read.RawValue, null, null, ResolveStatus.Error,
                    $"Значение «{read.RawValue}» не является числом.");
            }

            // Правила пустого/нулевого/отрицательного значения
            var policyResult = ApplyValuePolicies(field, raw);
            if (policyResult == PolicyOutcome.SkipToNextSource) continue;
            if (policyResult == PolicyOutcome.Error)
                return Record(obj, field, rule.Name, source.Kind, read.SourceDescription,
                    raw, null, null, ResolveStatus.Error,
                    $"Значение {raw} нарушает правило поля (ноль/отрицательное/пустое).");

            // Преобразование единиц: единица источника → базовая единица поля
            double converted = raw;
            string? conversion = null;
            if (rule.SourceUnitSymbol is { } srcUnitSymbol &&
                !string.Equals(srcUnitSymbol, field.BaseUnitSymbol, StringComparison.OrdinalIgnoreCase))
            {
                var srcUnit = _units.Get(srcUnitSymbol);
                var dstUnit = _units.Get(field.BaseUnitSymbol);
                converted = Quantity.From(raw, srcUnit).In(dstUnit);
                conversion = $"{raw.ToString(CultureInfo.InvariantCulture)} {srcUnitSymbol} → " +
                             $"{converted.ToString("G6", CultureInfo.InvariantCulture)} {field.BaseUnitSymbol}";
            }

            // Диапазон
            if (field.Min is { } min && converted < min || field.Max is { } max2 && converted > max2)
            {
                if (field.NegativePolicy == InvalidValuePolicy.ClampToRange)
                {
                    var clamped = Math.Clamp(converted, field.Min ?? double.MinValue, field.Max ?? double.MaxValue);
                    return Record(obj, field, rule.Name, source.Kind, read.SourceDescription,
                        raw, conversion, Round(field, clamped), ResolveStatus.Assumption,
                        $"Значение {converted:G6} вне диапазона [{field.Min}; {field.Max}], приведено к границе.");
                }
                return Record(obj, field, rule.Name, source.Kind, read.SourceDescription,
                    raw, conversion, null, ResolveStatus.Error,
                    $"Значение {converted:G6} {field.BaseUnitSymbol} вне допустимого диапазона [{field.Min}; {field.Max}].");
            }

            return Record(obj, field, rule.Name, source.Kind, read.SourceDescription,
                raw, conversion, Round(field, converted), ResolveStatus.Ok);
        }

        // Ни один источник не дал значение — применяем правило при отсутствии данных
        return field.MissingAction switch
        {
            MissingValueAction.UseDefault when field.DefaultValue is { } def =>
                Record(obj, field, rule.Name, "По умолчанию", "значение по умолчанию поля",
                    def, null, Round(field, def), ResolveStatus.Assumption,
                    "Источники пусты, принято значение по умолчанию (допущение)."),
            MissingValueAction.Warning =>
                Record(obj, field, rule.Name, "—", "все источники пусты", null, null, null,
                    ResolveStatus.Missing, "Значение не найдено (предупреждение)."),
            MissingValueAction.ExcludeObject =>
                Record(obj, field, rule.Name, "—", "все источники пусты", null, null, null,
                    ResolveStatus.Excluded, "Объект исключён из расчёта: нет значения."),
            MissingValueAction.AskEngineer =>
                Record(obj, field, rule.Name, "—", "все источники пусты", null, null, null,
                    ResolveStatus.NeedsDecision, "Требует решения инженера."),
            _ =>
                Record(obj, field, rule.Name, "—", "все источники пусты", null, null, null,
                    ResolveStatus.Error, $"Значение поля «{field.DisplayName}» не найдено ни в одном источнике."),
        };
    }

    /// <summary>Команда «Почему это значение?» — последняя запись журнала по объекту и полю.</summary>
    public ProvenanceRecord? WhyThisValue(string objectId, string fieldKey) =>
        _journal.LastOrDefault(r => r.ObjectId == objectId && r.FieldKey == fieldKey);

    private enum PolicyOutcome { Accept, SkipToNextSource, Error }

    private static PolicyOutcome ApplyValuePolicies(FieldDefinition field, double value)
    {
        if (value == 0)
            return field.ZeroPolicy switch
            {
                InvalidValuePolicy.Error => PolicyOutcome.Error,
                InvalidValuePolicy.TreatAsMissing => PolicyOutcome.SkipToNextSource,
                _ => PolicyOutcome.Accept,
            };
        if (value < 0)
            return field.NegativePolicy switch
            {
                InvalidValuePolicy.Error => PolicyOutcome.Error,
                InvalidValuePolicy.TreatAsMissing => PolicyOutcome.SkipToNextSource,
                _ => PolicyOutcome.Accept,
            };
        return PolicyOutcome.Accept;
    }

    private static bool TryToDouble(object? raw, out double value)
    {
        switch (raw)
        {
            case null: value = 0; return false;
            case double d: value = d; return true;
            case int i: value = i; return true;
            case string s when double.TryParse(s.Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var p): value = p; return true;
            case IConvertible c:
                try { value = c.ToDouble(CultureInfo.InvariantCulture); return true; }
                catch { value = 0; return false; }
            default: value = 0; return false;
        }
    }

    private static double Round(FieldDefinition field, double value) =>
        Math.Round(value, Math.Clamp(field.RoundDigits, 0, 12));

    private ResolvedValue Record(NetworkObject obj, FieldDefinition field, string ruleName,
        string sourceKind, string sourceDescription, object? raw, string? conversion,
        object? final, ResolveStatus status, string? message = null)
    {
        var record = new ProvenanceRecord(obj.Id, obj.Name, obj.Role.Role, field.Key, ruleName,
            sourceKind, sourceDescription, raw, conversion, final, status, message);
        _journal.Add(record);
        double? number = final is double dd ? dd : null;
        return new ResolvedValue(number, final, status, record);
    }
}
