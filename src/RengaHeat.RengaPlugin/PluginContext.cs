using RengaHeat.Core.Calculation;
using RengaHeat.Core.Model;
using RengaHeat.Core.Profiles;

namespace RengaHeat.RengaPlugin;

/// <summary>
/// Контекст для UI-окна плагина. Отделяет форму от Renga: форма зависит только от ядра
/// и от этих делегатов, а Renga-адаптер подставляет реальные реализации (чтение модели,
/// переход к объекту, применение изменений). Благодаря этому UI тестируется и без Renga.
/// </summary>
public sealed class PluginContext
{
    public RequirementsProfile Profile { get; set; } = RequirementsProfile.Default();
    public CalculationScenario Scenario { get; set; } = CalculationScenario.Base;

    /// <summary>Прочитать модель из источника (Renga или демо) — вся инженерная модель.</summary>
    public required Func<HeatingModel> ReadModel { get; init; }

    /// <summary>Прочитать только выделенные в Renga объекты (сценарий изоляции уровней; необязательно).</summary>
    public Func<HeatingModel>? ReadSelectedModel { get; init; }

    /// <summary>Выделить/показать объект в Renga по его идентификатору (необязательно).</summary>
    public Action<string>? SelectInRenga { get; init; }

    /// <summary>Выделить/подсветить группу объектов в Renga по идентификаторам (необязательно).</summary>
    public Action<IReadOnlyList<string>>? SelectManyInRenga { get; init; }

    /// <summary>UniqueId объектов, выделенных сейчас в Renga (для переназначения ролей в «Карте»).</summary>
    public Func<IReadOnlyList<string>>? GetSelectedUniqueIds { get; init; }

    /// <summary>Применить подтверждённые изменения (необязательно; null — применение недоступно).</summary>
    public Func<IReadOnlyList<ModelChange>, string>? ApplyChanges { get; init; }

    /// <summary>Запустить полный прогон сессии по текущему профилю и сценарию.</summary>
    public SessionOutcome RunSession(HeatingModel model) =>
        SessionFactory.CreateSession(Profile, Scenario).Run(model);
}
