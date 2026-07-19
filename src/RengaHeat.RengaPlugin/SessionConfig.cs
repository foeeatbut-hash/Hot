using System.Text.Json;
using RengaHeat.Core.Model;

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

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RengaHeat", "config.json");

    public static SessionConfig Load()
    {
        try
        {
            if (File.Exists(DefaultPath))
                return JsonSerializer.Deserialize<SessionConfig>(File.ReadAllText(DefaultPath)) ?? new SessionConfig();
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
