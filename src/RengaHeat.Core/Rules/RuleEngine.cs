using RengaHeat.Core.Model;

namespace RengaHeat.Core.Rules;

/// <summary>
/// Конструктор инженерных правил «Кому → При каком условии → Что сделать» без программирования.
/// Примеры: «всем стоякам с DN ≤ 50 разрешать только ВГП», «при более 10 приборов в кольце — ошибка».
/// </summary>
public sealed record EngineeringRule
{
    public required string Name { get; init; }
    /// <summary>«Кому»: роли, к которым применяется правило (пусто = ко всем).</summary>
    public IReadOnlyList<ObjectRole> TargetRoles { get; init; } = Array.Empty<ObjectRole>();
    /// <summary>«При каком условии»: предикат над объектом (null = всегда).</summary>
    public Func<NetworkObject, bool>? Condition { get; init; }
    /// <summary>«Что сделать».</summary>
    public required RuleAction Action { get; init; }
    public int Priority { get; init; }
    /// <summary>Режим «только проверять»: правило создаёт замечания, но не влияет на расчёт/подбор.</summary>
    public bool CheckOnly { get; init; }

    public bool AppliesTo(NetworkObject obj) =>
        (TargetRoles.Count == 0 || TargetRoles.Contains(obj.Role.Role)) &&
        (Condition is null || Condition(obj));
}

public enum RuleActionKind
{
    SetLimit,           // задать лимит (скорость, удельные потери, длина прибора…)
    RaiseError,
    RaiseWarning,
    FixDn,              // зафиксировать DN, автоподбор не меняет
    FixEquipment,       // зафиксировать арматуру/стиль
    ExcludeFromCalc,    // исключить объект из расчёта
    AssignTemporaryRole,
    RequirePresence,    // требовать наличие элемента (напр., регулятор перепада перед коллектором)
    SetMarkTemplate,    // шаблон маркировки
}

public sealed record RuleAction(RuleActionKind Kind, string? Key = null, double? NumberValue = null, string? TextValue = null);

/// <summary>Ручное исключение из правил с обязательной фиксацией причины (видно в отчёте).</summary>
public sealed record RuleException(string RuleName, string ObjectId, string Reason, string Author, DateTime CreatedUtc);

/// <summary>Результат применения правила к объекту.</summary>
public sealed record RuleApplication(EngineeringRule Rule, NetworkObject Object, bool Suppressed, string? SuppressReason);

/// <summary>
/// Движок правил: применяет правила по приоритетам, фиксирует конфликты одинакового приоритета
/// и ведёт журнал ручных исключений.
/// </summary>
public sealed class RuleEngine
{
    private readonly List<EngineeringRule> _rules = new();
    private readonly List<RuleException> _exceptions = new();
    public IReadOnlyList<EngineeringRule> Rules => _rules;
    public IReadOnlyList<RuleException> Exceptions => _exceptions;

    public void Add(EngineeringRule rule) => _rules.Add(rule);

    public void AddException(string ruleName, string objectId, string reason, string author) =>
        _exceptions.Add(new RuleException(ruleName, objectId, reason, author, DateTime.UtcNow));

    /// <summary>Все применимые к объекту правила с учётом исключений, по убыванию приоритета.</summary>
    public IReadOnlyList<RuleApplication> ApplicableTo(NetworkObject obj) =>
        _rules.Where(r => r.AppliesTo(obj))
              .OrderByDescending(r => r.Priority)
              .Select(r =>
              {
                  var ex = _exceptions.FirstOrDefault(e => e.RuleName == r.Name && e.ObjectId == obj.Id);
                  return new RuleApplication(r, obj, ex is not null, ex?.Reason);
              })
              .ToList();

    /// <summary>
    /// Эффективное значение лимита (SetLimit) для объекта по ключу.
    /// Побеждает правило с наибольшим приоритетом; конфликт одинакового приоритета
    /// с разными значениями возвращается через out для показа инженеру.
    /// </summary>
    public double? EffectiveLimit(NetworkObject obj, string limitKey, out bool conflict)
    {
        conflict = false;
        var candidates = ApplicableTo(obj)
            .Where(a => !a.Suppressed &&
                        a.Rule.Action.Kind == RuleActionKind.SetLimit &&
                        a.Rule.Action.Key == limitKey &&
                        a.Rule.Action.NumberValue is not null)
            .ToList();
        if (candidates.Count == 0) return null;

        var topPriority = candidates.Max(c => c.Rule.Priority);
        var top = candidates.Where(c => c.Rule.Priority == topPriority).ToList();
        var values = top.Select(c => c.Rule.Action.NumberValue!.Value).Distinct().ToList();
        conflict = values.Count > 1;
        // При конфликте берём консервативное (минимальное) значение, но помечаем конфликт
        return values.Min();
    }

    /// <summary>DN зафиксирован вручную или правилом — автоподбор не должен его менять.</summary>
    public bool IsDnFixed(NetworkObject obj, out int? fixedDn)
    {
        var app = ApplicableTo(obj).FirstOrDefault(a =>
            !a.Suppressed && a.Rule.Action.Kind == RuleActionKind.FixDn);
        fixedDn = app?.Rule.Action.NumberValue is { } v ? (int)v : null;
        return app is not null;
    }
}

/// <summary>Стандартные ключи лимитов, используемые ядром при подборе и проверках.</summary>
public static class LimitKeys
{
    public const string MaxVelocity = "limit.maxVelocity";           // м/с
    public const string MaxSpecificLoss = "limit.maxSpecificLoss";   // Па/м
    public const string MaxDeviceLength = "limit.maxDeviceLength";   // м
    public const string MaxDevicesPerLoop = "limit.maxDevicesPerLoop";
    public const string MaxApartmentsPerManifold = "limit.maxApartmentsPerManifold";
    public const string MaxManifoldsPerSection = "limit.maxManifoldsPerSection";
    public const string MaxFloors = "limit.maxFloors";
    public const string MaxPipeDn = "limit.maxPipeDn";
    public const string PowerMarginPercent = "limit.powerMarginPercent";
}
