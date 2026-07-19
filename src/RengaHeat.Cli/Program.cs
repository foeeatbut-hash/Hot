using RengaHeat.Core.Adapter;
using RengaHeat.Core.Calculation;
using RengaHeat.Core.Model;
using RengaHeat.Core.Profiles;
using RengaHeat.Core.Reporting;

// Демонстрационный прогон расчётного ядра без Renga: строит типовую двухтрубную схему,
// прогоняет сессию под профилем ЧТУ Новосаратовки и печатает отчёты.
// Показывает, что ядро тестируется и работает независимо от Renga SDK.

Console.OutputEncoding = System.Text.Encoding.UTF8;

var model = BuildDemoModel();
var session = SessionFactory.CreateSession(RequirementsProfile.Novosaratovka(), CalculationScenario.Base);
var outcome = session.Run(model);

Console.WriteLine(Reports.SessionReport(outcome));
Console.WriteLine();
Console.WriteLine(Reports.FindingsReport(outcome.AllFindings));

foreach (var result in outcome.Results)
{
    Console.WriteLine($"\n=== Ведомость приборов (источник {result.SourceName}) ===");
    Console.WriteLine(Reports.DevicesCsv(result));
    Console.WriteLine($"=== Ведомость участков ===");
    Console.WriteLine(Reports.SegmentsCsv(result));
}

Console.WriteLine("=== Предпросмотр изменений (режим только анализ — не применяются) ===");
foreach (var change in outcome.PreviewChanges.Changes)
    Console.WriteLine($"  {change}");

// Демонстрация применения через шлюз: одобряем и применяем в память, затем откатываем.
Console.WriteLine("\n=== Применение подтверждённых изменений (in-memory шлюз, обратимо) ===");
var gateway = new InMemoryModelGateway(model);
outcome.PreviewChanges.ApproveAll();
var report = gateway.ApplyChanges(outcome.PreviewChanges.Approved.ToList());
Console.WriteLine($"  Применено: {report.AppliedCount}, пропущено: {report.SkippedCount}");
gateway.Undo();
Console.WriteLine("  Отменено (Undo) — модель возвращена в исходное состояние.");

Console.WriteLine($"\nСтатус готовности расчёта: {(outcome.IsReady ? "ГОТОВО" : "НЕ ГОТОВО")}");

static HeatingModel BuildDemoModel()
{
    var b = new ModelBuilder("Демо: секция, один коллектор, три квартиры");
    var src = b.Add("ИТП", ObjectRole.HeatSource);
    var dpr = b.Add("Регулятор перепада", ObjectRole.DifferentialPressureRegulator, portCount: 2,
        context: new BuildingContext(Building: "1", Section: "A"));
    var supMain = b.AddPipe("Подающая магистраль", ObjectRole.SupplyMain, lengthM: 8, dn: 32,
        innerDiameterM: 0.0359);
    var retMain = b.AddPipe("Обратная магистраль", ObjectRole.ReturnMain, lengthM: 8, dn: 32,
        innerDiameterM: 0.0359);
    b.Connect(src, 0, dpr, 0);
    b.Connect(dpr, 1, supMain, 0);
    b.Connect(retMain, 0, src, 1);

    NetworkObject prevSup = supMain, prevRet = retMain;
    var loads = new[] { 1200.0, 1500.0, 900.0 };
    for (var i = 0; i < loads.Length; i++)
    {
        var ctx = new BuildingContext(Building: "1", Section: "A", Floor: i + 1, Apartment: $"кв.{i + 1}");
        var supTee = b.Add($"Тройник П{i + 1}", ObjectRole.Tee, portCount: 3, context: ctx);
        var retTee = b.Add($"Тройник О{i + 1}", ObjectRole.Tee, portCount: 3, context: ctx);
        var supSeg = b.AddPipe($"Стояк П{i + 1}", ObjectRole.Riser, lengthM: 3.2, dn: 25,
            innerDiameterM: 0.027, context: ctx);
        var retSeg = b.AddPipe($"Стояк О{i + 1}", ObjectRole.Riser, lengthM: 3.2, dn: 25,
            innerDiameterM: 0.027, context: ctx);
        var supBranch = b.AddPipe($"Подводка П{i + 1}", ObjectRole.Pipe, lengthM: 5, dn: 15,
            innerDiameterM: 0.0125, context: ctx);
        var retBranch = b.AddPipe($"Подводка О{i + 1}", ObjectRole.Pipe, lengthM: 5, dn: 15,
            innerDiameterM: 0.0125, context: ctx);
        var valve = b.Add($"Балансировочный клапан {i + 1}", ObjectRole.BalancingValve, portCount: 2, context: ctx);
        b.SetProperty(valve, "Kv", 2.5, "Kv");
        var rad = b.AddRadiator($"Радиатор {i + 1}", loads[i], ctx);

        b.Connect(prevSup, 1, supSeg, 0);
        b.Connect(supSeg, 1, supTee, 0);
        b.Connect(supTee, 2, supBranch, 0);
        b.Connect(supBranch, 1, valve, 0);
        b.Connect(valve, 1, rad, 0);
        b.Connect(rad, 1, retBranch, 0);
        b.Connect(retBranch, 1, retTee, 2);
        b.Connect(prevRet, 1, retSeg, 0);
        b.Connect(retSeg, 1, retTee, 0);

        prevSup = supTee;
        prevRet = retTee;
    }
    return b.Model;
}
