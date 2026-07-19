namespace RengaHeat.Core.Model;

/// <summary>
/// Роли объектов сети отопления, независимые от библиотек и стилей Renga.
/// Роль назначается классификатором по правилам либо вручную (свойство «ОВ_Роль»).
/// </summary>
public enum ObjectRole
{
    Unknown = 0,

    // Источники и оборудование
    HeatSource,             // источник тепла / ИТП
    Pump,                   // насос

    // Трубопроводные роли
    SupplyMain,             // подающая магистраль
    ReturnMain,             // обратная магистраль
    Riser,                  // стояк
    SupplyManifold,         // подающий коллектор
    ReturnManifold,         // обратный коллектор
    Pipe,                   // труба (участок без уточнённой роли магистрали/стояка)

    // Контуры
    ApartmentLoop,          // поквартирное кольцо
    CommonAreaLoop,         // кольцо МОП
    StaircaseCircuit,       // лестничный контур
    VentilationCircuit,     // вентиляционный контур (теплоснабжение вентиляции)

    // Приборы
    Radiator,
    Convector,
    TowelRail,              // полотенцесушитель
    AirHeater,              // воздухонагреватель

    // Арматура
    ThermostaticValve,
    BalancingValve,
    DifferentialPressureRegulator,
    ShutoffValve,
    Strainer,               // фильтр
    HeatMeter,              // счётчик

    // Фитинги и элементы трассы
    Elbow,                  // отвод
    Tee,                    // тройник
    Reducer,                // переход
    Compensator,            // компенсатор
    FixedSupport,           // неподвижная опора
}

/// <summary>Откуда взялась роль объекта — для журнала и уровня доверия.</summary>
public enum RoleSource
{
    Unassigned,
    Manual,             // назначено инженером
    OvRoleProperty,     // из необязательного свойства «ОВ_Роль»
    Rule,               // выведено правилом классификатора
    Guessed,            // предположено (низкое доверие)
}

public sealed record RoleAssignment(ObjectRole Role, RoleSource Source, string? RuleName = null)
{
    public static readonly RoleAssignment None = new(ObjectRole.Unknown, RoleSource.Unassigned);
}

/// <summary>Отображаемые русские имена ролей для журналов, отчётов и команды «Почему это значение?».</summary>
public static class RoleNames
{
    private static readonly Dictionary<ObjectRole, string> Display = new()
    {
        [ObjectRole.Unknown] = "Не определена",
        [ObjectRole.HeatSource] = "Источник тепла",
        [ObjectRole.Pump] = "Насос",
        [ObjectRole.SupplyMain] = "Подающая магистраль",
        [ObjectRole.ReturnMain] = "Обратная магистраль",
        [ObjectRole.Riser] = "Стояк",
        [ObjectRole.SupplyManifold] = "Подающий коллектор",
        [ObjectRole.ReturnManifold] = "Обратный коллектор",
        [ObjectRole.Pipe] = "Труба",
        [ObjectRole.ApartmentLoop] = "Поквартирное кольцо",
        [ObjectRole.CommonAreaLoop] = "Кольцо МОП",
        [ObjectRole.StaircaseCircuit] = "Лестничный контур",
        [ObjectRole.VentilationCircuit] = "Вентиляционный контур",
        [ObjectRole.Radiator] = "Радиатор",
        [ObjectRole.Convector] = "Конвектор",
        [ObjectRole.TowelRail] = "Полотенцесушитель",
        [ObjectRole.AirHeater] = "Воздухонагреватель",
        [ObjectRole.ThermostaticValve] = "Термостатический клапан",
        [ObjectRole.BalancingValve] = "Балансировочный клапан",
        [ObjectRole.DifferentialPressureRegulator] = "Регулятор перепада давления",
        [ObjectRole.ShutoffValve] = "Запорная арматура",
        [ObjectRole.Strainer] = "Фильтр",
        [ObjectRole.HeatMeter] = "Счётчик",
        [ObjectRole.Elbow] = "Отвод",
        [ObjectRole.Tee] = "Тройник",
        [ObjectRole.Reducer] = "Переход",
        [ObjectRole.Compensator] = "Компенсатор",
        [ObjectRole.FixedSupport] = "Неподвижная опора",
    };

    public static string Of(ObjectRole role) => Display.GetValueOrDefault(role, role.ToString());
}
