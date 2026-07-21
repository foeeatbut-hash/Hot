using System.Text.RegularExpressions;
using RengaHeat.Core.Model;

namespace RengaHeat.Core.Classification;

/// <summary>
/// Условие правила классификации. Все критерии необязательны и объединяются по «И»;
/// роль определяется правилами, а не только именем объекта.
/// </summary>
public sealed record RoleCriteria
{
    public string? RengaTypeId { get; init; }
    public string? Category { get; init; }
    public string? StyleNameContains { get; init; }
    public string? ArticleEquals { get; init; }
    public string? MaterialContains { get; init; }
    public int? MinDn { get; init; }
    public int? MaxDn { get; init; }
    public string? NameRegex { get; init; }
    public string? SystemNameContains { get; init; }
    public int? MinPortCount { get; init; }
    /// <summary>Имя свойства и требуемое значение (произвольное свойство как критерий).</summary>
    public (string Name, string Value)? PropertyEquals { get; init; }

    public bool Matches(NetworkObject o)
    {
        if (RengaTypeId is { } t && !string.Equals(o.RengaTypeId, t, StringComparison.OrdinalIgnoreCase)) return false;
        if (Category is { } c && !string.Equals(o.Category, c, StringComparison.OrdinalIgnoreCase)) return false;
        if (StyleNameContains is { } s &&
            (o.StyleName is null || !o.StyleName.Contains(s, StringComparison.OrdinalIgnoreCase))) return false;
        if (ArticleEquals is { } a && !string.Equals(o.Article, a, StringComparison.OrdinalIgnoreCase)) return false;
        if (MaterialContains is { } m &&
            (o.Material is null || !o.Material.Contains(m, StringComparison.OrdinalIgnoreCase))) return false;
        if (MinDn is { } minDn && o.Ports.All(p => p.Dn is null || p.Dn < minDn)) return false;
        if (MaxDn is { } maxDn && o.Ports.Any(p => p.Dn > maxDn)) return false;
        if (NameRegex is { } rx && !Regex.IsMatch(o.Name, rx, RegexOptions.IgnoreCase)) return false;
        if (SystemNameContains is { } sys &&
            (o.Context.SystemName is null ||
             !o.Context.SystemName.Contains(sys, StringComparison.OrdinalIgnoreCase))) return false;
        if (MinPortCount is { } pc && o.Ports.Count < pc) return false;
        if (PropertyEquals is { } pe)
        {
            var prop = o.FindPropertyByName(pe.Name);
            if (prop?.Value?.ToString() is not { } v ||
                !string.Equals(v, pe.Value, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}

/// <summary>Правило классификатора: условие → роль, с приоритетом.</summary>
public sealed record RoleRule(string Name, RoleCriteria Criteria, ObjectRole Role, int Priority = 0);

/// <summary>Итог классификации одного объекта, включая конфликты одинакового приоритета.</summary>
public sealed record ClassificationOutcome(
    NetworkObject Object,
    RoleAssignment Assigned,
    IReadOnlyList<RoleRule> MatchedRules,
    bool HasConflict);

/// <summary>
/// Классификатор ролей. Порядок приоритетов:
/// ручное назначение → свойство «ОВ_Роль» → правила (по приоритету) → роль не определена.
/// Конфликт правил одинакового приоритета с разными ролями фиксируется, роль не назначается молча.
/// </summary>
public sealed class Classifier
{
    /// <summary>Имя необязательного свойства для ручного уточнения роли сомнительного объекта.</summary>
    public const string OvRolePropertyName = "ОВ_Роль";

    private readonly List<RoleRule> _rules = new();
    public IReadOnlyList<RoleRule> Rules => _rules;
    public void AddRule(RoleRule rule) => _rules.Add(rule);

    public ClassificationOutcome Classify(NetworkObject obj)
    {
        // 1. Ручное назначение инженера имеет абсолютный приоритет
        if (obj.Role.Source == RoleSource.Manual)
            return new ClassificationOutcome(obj, obj.Role, Array.Empty<RoleRule>(), false);

        // 2. Свойство «ОВ_Роль»
        if (obj.FindPropertyByName(OvRolePropertyName)?.Value?.ToString() is { Length: > 0 } roleText &&
            TryParseRole(roleText, out var explicitRole))
        {
            var assignment = new RoleAssignment(explicitRole, RoleSource.OvRoleProperty);
            obj.Role = assignment;
            return new ClassificationOutcome(obj, assignment, Array.Empty<RoleRule>(), false);
        }

        // 3. Правила по приоритету
        var matched = _rules.Where(r => r.Criteria.Matches(obj)).ToList();
        if (matched.Count == 0)
            return new ClassificationOutcome(obj, RoleAssignment.None, matched, false);

        var topPriority = matched.Max(r => r.Priority);
        var top = matched.Where(r => r.Priority == topPriority).ToList();
        var distinctRoles = top.Select(r => r.Role).Distinct().ToList();

        if (distinctRoles.Count > 1)
            // Конфликт одинакового приоритета: роль не назначаем, требуется решение инженера
            return new ClassificationOutcome(obj, RoleAssignment.None, top, true);

        var winner = top[0];
        var result = new RoleAssignment(winner.Role, RoleSource.Rule, winner.Name);
        obj.Role = result;
        return new ClassificationOutcome(obj, result, top, false);
    }

    public IReadOnlyList<ClassificationOutcome> ClassifyAll(HeatingModel model) =>
        model.Objects.Values.Select(Classify).ToList();

    /// <summary>
    /// Подсказка роли для каждого типа Renga (ObjectTypeS): по каждому типу берётся наиболее частая
    /// роль, выведенная правилами. Используется UI для авто-заполнения таблицы «тип → роль», чтобы
    /// инженер лишь правил исключения, а не размечал всё вручную. Модель не мутируется.
    /// </summary>
    public IReadOnlyDictionary<string, ObjectRole> SuggestRolesByType(HeatingModel model)
    {
        var result = new Dictionary<string, ObjectRole>();
        foreach (var byType in model.Objects.Values.GroupBy(o => o.RengaTypeId ?? ""))
        {
            var votes = new Dictionary<ObjectRole, int>();
            foreach (var obj in byType)
            {
                var matched = _rules.Where(r => r.Criteria.Matches(obj)).ToList();
                if (matched.Count == 0) continue;
                var maxP = matched.Max(x => x.Priority);
                var top = matched.Where(r => r.Priority == maxP).Select(r => r.Role).Distinct().ToList();
                if (top.Count != 1) continue;          // конфликт — не голосуем
                votes[top[0]] = votes.GetValueOrDefault(top[0]) + 1;
            }
            if (votes.Count > 0)
                result[byType.Key] = votes.OrderByDescending(kv => kv.Value).First().Key;
        }
        return result;
    }

    private static readonly Dictionary<string, ObjectRole> RoleAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Источник тепла"] = ObjectRole.HeatSource, ["ИТП"] = ObjectRole.HeatSource,
        ["Насос"] = ObjectRole.Pump,
        ["Подающая магистраль"] = ObjectRole.SupplyMain, ["Подача"] = ObjectRole.SupplyMain,
        ["Обратная магистраль"] = ObjectRole.ReturnMain, ["Обратка"] = ObjectRole.ReturnMain,
        ["Стояк"] = ObjectRole.Riser,
        ["Подающий коллектор"] = ObjectRole.SupplyManifold,
        ["Обратный коллектор"] = ObjectRole.ReturnManifold,
        ["Труба"] = ObjectRole.Pipe,
        ["Поквартирное кольцо"] = ObjectRole.ApartmentLoop,
        ["Кольцо МОП"] = ObjectRole.CommonAreaLoop,
        ["Лестничный контур"] = ObjectRole.StaircaseCircuit,
        ["Вентиляционный контур"] = ObjectRole.VentilationCircuit,
        ["Радиатор"] = ObjectRole.Radiator,
        ["Конвектор"] = ObjectRole.Convector,
        ["Полотенцесушитель"] = ObjectRole.TowelRail,
        ["Воздухонагреватель"] = ObjectRole.AirHeater,
        ["Термостатический клапан"] = ObjectRole.ThermostaticValve,
        ["Балансировочный клапан"] = ObjectRole.BalancingValve,
        ["Регулятор перепада давления"] = ObjectRole.DifferentialPressureRegulator,
        ["Запорная арматура"] = ObjectRole.ShutoffValve, ["Кран"] = ObjectRole.ShutoffValve,
        ["Фильтр"] = ObjectRole.Strainer,
        ["Счётчик"] = ObjectRole.HeatMeter, ["Теплосчётчик"] = ObjectRole.HeatMeter,
        ["Отвод"] = ObjectRole.Elbow,
        ["Тройник"] = ObjectRole.Tee,
        ["Переход"] = ObjectRole.Reducer,
        ["Компенсатор"] = ObjectRole.Compensator,
        ["Неподвижная опора"] = ObjectRole.FixedSupport,
        ["Точка трассировки"] = ObjectRole.RoutePoint,
    };

    public static bool TryParseRole(string text, out ObjectRole role)
    {
        if (RoleAliases.TryGetValue(text.Trim(), out role)) return true;
        return Enum.TryParse(text, ignoreCase: true, out role) && role != ObjectRole.Unknown;
    }
}
