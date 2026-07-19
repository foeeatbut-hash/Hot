using System.Globalization;

namespace RengaHeat.Core.Catalogs;

/// <summary>Метаданные каталога: версия и источник фиксируются в отчёте для воспроизводимости.</summary>
public sealed record CatalogInfo(string Name, string Version, string SourceDescription, DateTime ImportedUtc);

/// <summary>Типоразмер трубы в каталоге.</summary>
public sealed record PipeSeriesItem(
    string Series,              // серия/наименование: «ВГП ГОСТ 3262», «Электросварная ГОСТ 10704», «PE-Xa EVOH»
    string Material,            // сталь, PE-Xa …
    int Dn,
    double OuterDiameterM,
    double WallThicknessM,
    double RoughnessM,          // эквивалентная шероховатость
    double MaxPressurePn,       // PN, бар
    string JointType,           // муфтовое, сварное, пресс …
    double? CostPerMeter = null)
{
    public double InnerDiameterM => OuterDiameterM - 2 * WallThicknessM;
}

/// <summary>Каталог труб: подбор идёт по реальным типоразмерам, а не «идеальному диаметру по формуле».</summary>
public sealed class PipeCatalog
{
    private readonly List<PipeSeriesItem> _items = new();
    public required CatalogInfo Info { get; init; }
    public IReadOnlyList<PipeSeriesItem> Items => _items;
    public void Add(PipeSeriesItem item) => _items.Add(item);

    public IEnumerable<PipeSeriesItem> BySeries(string series) =>
        _items.Where(i => string.Equals(i.Series, series, StringComparison.OrdinalIgnoreCase))
              .OrderBy(i => i.InnerDiameterM);

    public PipeSeriesItem? Find(string series, int dn) =>
        BySeries(series).FirstOrDefault(i => i.Dn == dn);

    /// <summary>
    /// Каталог стальных труб по умолчанию: ВГП ГОСТ 3262-75 (обыкновенные) до Ду50,
    /// электросварные ГОСТ 10704-91 выше, PE-Xa 16–32. Шероховатость: сталь 0.2 мм (новая ВГП
    /// по СП 60/справочным данным), PE-X 0.007 мм.
    /// </summary>
    public static PipeCatalog CreateDefault()
    {
        var cat = new PipeCatalog
        {
            Info = new CatalogInfo("Трубы (встроенный)", "1.0",
                "ГОСТ 3262-75, ГОСТ 10704-91, типовые PE-Xa; заменить импортом каталога производителя",
                DateTime.UtcNow),
        };
        // ВГП ГОСТ 3262-75, обыкновенная: Ду, наружный диаметр, стенка
        (int dn, double od, double wall)[] vgp =
            { (15, 21.3, 2.8), (20, 26.8, 2.8), (25, 33.5, 3.2), (32, 42.3, 3.2), (40, 48.0, 3.5), (50, 60.0, 3.5) };
        foreach (var (dn, od, wall) in vgp)
            cat.Add(new PipeSeriesItem("ВГП ГОСТ 3262", "Сталь", dn, od / 1000, wall / 1000,
                0.0002, 16, "муфтовое/сварное"));
        // Электросварные ГОСТ 10704-91
        (int dn, double od, double wall)[] es =
            { (65, 76.0, 3.5), (80, 89.0, 3.5), (100, 108.0, 4.0), (125, 133.0, 4.0), (150, 159.0, 4.5), (200, 219.0, 6.0) };
        foreach (var (dn, od, wall) in es)
            cat.Add(new PipeSeriesItem("Электросварная ГОСТ 10704", "Сталь", dn, od / 1000, wall / 1000,
                0.0002, 16, "сварное"));
        // PE-Xa с EVOH (типовые размеры; ограничение диаметра — параметр профиля)
        (int dn, double od, double wall)[] pex = { (12, 16.0, 2.2), (16, 20.0, 2.8), (20, 25.0, 3.5), (25, 32.0, 4.4) };
        foreach (var (dn, od, wall) in pex)
            cat.Add(new PipeSeriesItem("PE-Xa EVOH", "PE-Xa", dn, od / 1000, wall / 1000,
                7e-6, 10, "пресс/аксиальное"));
        return cat;
    }
}

/// <summary>Балансировочный клапан: таблица «преднастройка n → Kv».</summary>
public sealed record BalancingValveModel(
    string Article,
    string Manufacturer,
    int Dn,
    IReadOnlyList<(double Preset, double Kv)> PresetKv)
{
    public double MaxKv => PresetKv.Max(p => p.Kv);

    /// <summary>Преднастройка, дающая требуемую Kv (линейная интерполяция между позициями каталога).</summary>
    public double? PresetForKv(double requiredKv)
    {
        var sorted = PresetKv.OrderBy(p => p.Kv).ToList();
        if (requiredKv <= sorted[0].Kv) return sorted[0].Preset;
        if (requiredKv > sorted[^1].Kv) return null; // клапан мал — не хватает пропускной способности
        for (var i = 1; i < sorted.Count; i++)
        {
            if (requiredKv > sorted[i].Kv) continue;
            var (p0, kv0) = sorted[i - 1];
            var (p1, kv1) = sorted[i];
            var t = (requiredKv - kv0) / (kv1 - kv0);
            return Math.Round(p0 + t * (p1 - p0), 1);
        }
        return sorted[^1].Preset;
    }
}

public sealed class ValveCatalog
{
    private readonly List<BalancingValveModel> _items = new();
    public required CatalogInfo Info { get; init; }
    public IReadOnlyList<BalancingValveModel> Items => _items;
    public void Add(BalancingValveModel item) => _items.Add(item);

    public IEnumerable<BalancingValveModel> ByDn(int dn) => _items.Where(v => v.Dn == dn);

    /// <summary>Типовые ручные балансировочные клапаны (характеристики уровня MSV-BD/USV-I; заменить каталогом производителя).</summary>
    public static ValveCatalog CreateDefault()
    {
        var cat = new ValveCatalog
        {
            Info = new CatalogInfo("Балансировочные клапаны (встроенный)", "1.0",
                "Типовые характеристики ручных балансировочных клапанов; требуется согласование производителя по ЧТУ",
                DateTime.UtcNow),
        };
        cat.Add(new BalancingValveModel("BV-15", "Типовой", 15, new (double, double)[]
            { (0.5, 0.15), (1, 0.35), (1.5, 0.62), (2, 1.0), (2.5, 1.45), (3, 1.9), (3.5, 2.4), (4, 3.0) }));
        cat.Add(new BalancingValveModel("BV-20", "Типовой", 20, new (double, double)[]
            { (0.5, 0.3), (1, 0.75), (1.5, 1.4), (2, 2.2), (2.5, 3.1), (3, 4.0), (3.5, 5.0), (4, 6.0) }));
        cat.Add(new BalancingValveModel("BV-25", "Типовой", 25, new (double, double)[]
            { (0.5, 0.5), (1, 1.3), (2, 3.5), (3, 6.0), (4, 8.5), (5, 9.5) }));
        cat.Add(new BalancingValveModel("BV-32", "Типовой", 32, new (double, double)[]
            { (0.5, 1.0), (1, 2.5), (2, 6.0), (3, 10.0), (4, 14.0), (5, 18.0) }));
        return cat;
    }
}

/// <summary>Насос из каталога: рабочая зона по расходу и напору.</summary>
public sealed record PumpModel(
    string Article, string Manufacturer,
    double MinFlowM3H, double MaxFlowM3H, double MaxHeadKPa);

public sealed class PumpCatalog
{
    private readonly List<PumpModel> _items = new();
    public required CatalogInfo Info { get; init; }
    public IReadOnlyList<PumpModel> Items => _items;
    public void Add(PumpModel item) => _items.Add(item);

    public static PumpCatalog CreateDefault()
    {
        var cat = new PumpCatalog
        {
            Info = new CatalogInfo("Насосы (встроенный)", "1.0",
                "Типовые циркуляционные насосы; заменить каталогом производителя", DateTime.UtcNow),
        };
        cat.Add(new PumpModel("ЦН 25-40", "Типовой", 0.2, 3.5, 40));
        cat.Add(new PumpModel("ЦН 25-60", "Типовой", 0.3, 4.5, 60));
        cat.Add(new PumpModel("ЦН 32-80", "Типовой", 0.5, 8.0, 80));
        cat.Add(new PumpModel("ЦН 40-120", "Типовой", 1.5, 25.0, 120));
        cat.Add(new PumpModel("ЦН 50-180", "Типовой", 4.0, 60.0, 180));
        return cat;
    }
}

/// <summary>
/// Импорт каталога труб из CSV: series;material;dn;outer_mm;wall_mm;roughness_mm;pn;joint;cost.
/// Разделитель — «;», десятичный знак — точка или запятая.
/// </summary>
public static class CsvCatalogLoader
{
    public static PipeCatalog LoadPipes(string csvContent, string name, string version, string source)
    {
        var catalog = new PipeCatalog { Info = new CatalogInfo(name, version, source, DateTime.UtcNow) };
        var lines = csvContent.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines.Skip(1)) // первая строка — заголовок
        {
            var f = line.Split(';');
            if (f.Length < 8)
                throw new FormatException($"Строка каталога труб неполна: «{line}»");
            catalog.Add(new PipeSeriesItem(
                f[0], f[1], int.Parse(f[2], CultureInfo.InvariantCulture),
                Num(f[3]) / 1000, Num(f[4]) / 1000, Num(f[5]) / 1000,
                Num(f[6]), f[7],
                f.Length > 8 && f[8].Length > 0 ? Num(f[8]) : null));
        }
        return catalog;
    }

    private static double Num(string s) =>
        double.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture);
}
