using System.Text.Json;
using RengaHeat.Core.Model;
using RengaHeat.Core.Profiles;

namespace RengaHeat.RengaPlugin;

/// <summary>
/// Настройки инженера, задаваемые в UI: сопоставление типов Renga ролям и выбор свойства
/// тепловой нагрузки. Сохраняются в файл (%APPDATA%\RengaHeat\config.json) — настройка
/// переносима на другой корпус и воспроизводима (требование ЧТУ/ТЗ).
/// Класс не зависит от Renga.
/// </summary>
public sealed class SessionConfig
{
    /// <summary>Тип объекта Renga (ObjectTypeS) → имя роли (значение enum ObjectRole).</summary>
    public Dictionary<string, string> TypeRoles { get; set; } = new();

    /// <summary>Имя свойства, из которого берётся тепловая нагрузка прибора (пусто — авто-поиск).</summary>
    public string? LoadPropertyName { get; set; }

    /// <summary>
    /// Ключ расчётного поля (FieldDefinition.Key) → имя свойства-источника, выбранное инженером.
    /// Ставится в начало цепочки сопоставления поля. Параметризует каждый вход расчёта
    /// (длина, диаметр, шероховатость, Kv, ζ, …), а не только нагрузку.
    /// </summary>
    public Dictionary<string, string> FieldProperties { get; set; } = new();

    /// <summary>Пользовательские исходные данные (переопределения профиля из раздела «Исходные»).</summary>
    public ProfileOverride Overrides { get; set; } = new();

    /// <summary>Имя расчётного сценария (Базовый/Экономичный/Тихий). Пусто — базовый.</summary>
    public string? ScenarioName { get; set; }

    /// <summary>Выбранные уровни (этажи) для расчёта. Пусто — учитываются все уровни.</summary>
    public List<string> SelectedLevels { get; set; } = new();

    /// <summary>
    /// Ручные назначения ролей конкретным объектам (раздел «Карта»): UniqueId объекта → имя роли.
    /// Абсолютный приоритет над авто-классификатором; переживают перезагрузку модели (Id устойчив).
    /// </summary>
    public Dictionary<string, string> ObjectRoles { get; set; } = new();

    /// <summary>Ручные назначения стороны сети (раздел «Карта»): UniqueId → Supply/Return/Source.</summary>
    public Dictionary<string, string> ObjectSides { get; set; } = new();

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RengaHeat", "config.json");

    public static SessionConfig Load()
    {
        try
        {
            if (File.Exists(DefaultPath))
            {
                var cfg = JsonSerializer.Deserialize<SessionConfig>(File.ReadAllText(DefaultPath)) ?? new SessionConfig();
                // Миграция: прежнее одиночное свойство нагрузки → общая таблица полей.
                if (!string.IsNullOrWhiteSpace(cfg.LoadPropertyName) &&
                    !cfg.FieldProperties.ContainsKey("device.load"))
                    cfg.FieldProperties["device.load"] = cfg.LoadPropertyName!;
                return cfg;
            }
        }
        catch { /* повреждённый конфиг не должен ронять плагин */ }
        return new SessionConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DefaultPath)!);
            File.WriteAllText(DefaultPath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* не удалось сохранить — работаем с настройками в памяти */ }
    }

    /// <summary>Разобранные пары «тип Renga → роль» (пропускает нераспознанные и Unknown).</summary>
    public IEnumerable<(string TypeS, ObjectRole Role)> ResolvedTypeRoles()
    {
        foreach (var (typeS, roleName) in TypeRoles)
            if (Enum.TryParse<ObjectRole>(roleName, out var role) && role != ObjectRole.Unknown)
                yield return (typeS, role);
    }
}
