using RengaHeat.Core.Model;

namespace RengaHeat.Core.Tests;

/// <summary>Строители типовых схем из ТЗ для тестов сессии и проверок.</summary>
public static class TestScenarios
{
    /// <summary>
    /// Двухтрубная тупиковая: ИТП → подающая магистраль → тройники к N приборам → обратка → ИТП.
    /// Каждый прибор — отдельное кольцо через подающий и обратный тройник.
    /// </summary>
    public static HeatingModel TwoPipeDeadEnd(int radiators = 3, string apartmentPrefix = "кв.")
    {
        var b = new ModelBuilder("Двухтрубная тупиковая");
        var src = b.Add("ИТП", ObjectRole.HeatSource);
        var supplyMain = b.AddPipe("Подающая магистраль", ObjectRole.SupplyMain, lengthM: 5, dn: 32,
            innerDiameterM: 0.0359);
        var returnMain = b.AddPipe("Обратная магистраль", ObjectRole.ReturnMain, lengthM: 5, dn: 32,
            innerDiameterM: 0.0359);
        // supplyMain: p0 от источника, p1 к первому подающему тройнику
        // returnMain: p0 к источнику, p1 от первого обратного тройника
        b.Connect(src, 0, supplyMain, 0);
        b.Connect(returnMain, 0, src, 1);

        NetworkObject prevSupplyTee = supplyMain;   // выход «дальше по магистрали» — порт 1
        NetworkObject prevReturnTee = returnMain;   // вход «от следующего тройника» — порт 1
        for (var i = 0; i < radiators; i++)
        {
            var ctx = new BuildingContext(Section: "1", Floor: 1, Apartment: $"{apartmentPrefix}{i + 1}");
            // Тройник подачи: p0 вход, p1 далее по магистрали, p2 в прибор
            var supTee = b.Add($"Тройник подача {i + 1}", ObjectRole.Tee, portCount: 3, context: ctx);
            // Тройник обратки: p0 к магистрали, p1 от следующего тройника, p2 от прибора
            var retTee = b.Add($"Тройник обратка {i + 1}", ObjectRole.Tee, portCount: 3, context: ctx);
            var supBranch = b.AddPipe($"Подводка П{i + 1}", ObjectRole.Pipe, lengthM: 4, dn: 15,
                innerDiameterM: 0.0125, context: ctx);
            var retBranch = b.AddPipe($"Подводка О{i + 1}", ObjectRole.Pipe, lengthM: 4, dn: 15,
                innerDiameterM: 0.0125, context: ctx);
            var valve = b.Add($"Балансировочный клапан {i + 1}", ObjectRole.BalancingValve, portCount: 2, context: ctx);
            b.SetProperty(valve, "Kv", 2.5, "Kv");
            var rad = b.AddRadiator($"Радиатор {i + 1}", 1000, ctx);

            // Участки магистрали между тройниками: дальние кольца получают больше потерь,
            // ближние — избыток давления, который гасят балансировочные клапаны.
            var supMainSeg = b.AddPipe($"Магистраль П{i + 1}", ObjectRole.SupplyMain, lengthM: 6, dn: 25,
                innerDiameterM: 0.027, context: ctx);
            var retMainSeg = b.AddPipe($"Магистраль О{i + 1}", ObjectRole.ReturnMain, lengthM: 6, dn: 25,
                innerDiameterM: 0.027, context: ctx);
            b.Connect(prevSupplyTee, 1, supMainSeg, 0);
            b.Connect(supMainSeg, 1, supTee, 0);
            b.Connect(supTee, 2, supBranch, 0);
            b.Connect(supBranch, 1, valve, 0);
            b.Connect(valve, 1, rad, 0);
            b.Connect(rad, 1, retBranch, 0);
            b.Connect(retBranch, 1, retTee, 2);
            b.Connect(prevReturnTee, 1, retMainSeg, 0);
            b.Connect(retMainSeg, 1, retTee, 0);

            prevSupplyTee = supTee;
            prevReturnTee = retTee;
        }
        return b.Model;
    }

    /// <summary>Схема с перепутанными портами прибора: вход/выход прибора поменяны местами.</summary>
    public static HeatingModel SwappedDevicePorts()
    {
        var b = new ModelBuilder("Перепутанные порты прибора");
        var src = b.Add("ИТП", ObjectRole.HeatSource);
        var supply = b.AddPipe("Подача", ObjectRole.SupplyMain, dn: 20, innerDiameterM: 0.0212);
        var ret = b.AddPipe("Обратка", ObjectRole.ReturnMain, dn: 20, innerDiameterM: 0.0212);
        var ctx = new BuildingContext(Apartment: "кв.1");
        var rad = b.AddRadiator("Радиатор", 1200, ctx);
        b.Connect(src, 0, supply, 0);
        // Подача приходит в порт 1 прибора (а не 0), обратка уходит из порта 0 — «перепутано».
        b.Connect(supply, 1, rad, 1);
        b.Connect(rad, 0, ret, 0);
        b.Connect(ret, 1, src, 1);
        return b.Model;
    }

    /// <summary>Коллектор, обслуживающий заданное число квартир (для проверки лимита ЧТУ).</summary>
    public static HeatingModel ManifoldWithApartments(int apartments)
    {
        var b = new ModelBuilder("Коллектор с квартирами");
        var src = b.Add("ИТП", ObjectRole.HeatSource);
        var ctxSec = new BuildingContext(Building: "1", Section: "A");
        var supMan = b.Add("Подающий коллектор", ObjectRole.SupplyManifold, portCount: apartments + 1, context: ctxSec);
        var retMan = b.Add("Обратный коллектор", ObjectRole.ReturnManifold, portCount: apartments + 1, context: ctxSec);
        var dpr = b.Add("Регулятор перепада", ObjectRole.DifferentialPressureRegulator, portCount: 2, context: ctxSec);
        b.Connect(src, 0, dpr, 0);
        b.Connect(dpr, 1, supMan, 0);
        b.Connect(retMan, 0, src, 1);

        for (var i = 0; i < apartments; i++)
        {
            var ctx = new BuildingContext(Building: "1", Section: "A", Apartment: $"кв.{i + 1}");
            var rad = b.AddRadiator($"Радиатор кв.{i + 1}", 900, ctx);
            b.Connect(supMan, i + 1, rad, 0);
            b.Connect(rad, 1, retMan, i + 1);
        }
        return b.Model;
    }

    /// <summary>Горизонтальное кольцо квартиры с заданным числом приборов (для лимита «10 приборов»).</summary>
    public static HeatingModel ApartmentLoopWithDevices(int devices)
    {
        var b = new ModelBuilder("Поквартирное кольцо");
        var src = b.Add("ИТП", ObjectRole.HeatSource);
        var ctx = new BuildingContext(Building: "1", Section: "A", Apartment: "кв.1");
        var supply = b.AddPipe("Подача в квартиру", ObjectRole.SupplyMain, context: ctx);
        var ret = b.AddPipe("Обратка из квартиры", ObjectRole.ReturnMain, context: ctx);
        b.Connect(src, 0, supply, 0);
        b.Connect(ret, 1, src, 1);
        NetworkObject prevSup = supply, prevRet = ret;
        for (var i = 0; i < devices; i++)
        {
            var supTee = b.Add($"Тройник П{i}", ObjectRole.Tee, portCount: 3, context: ctx);
            var retTee = b.Add($"Тройник О{i}", ObjectRole.Tee, portCount: 3, context: ctx);
            var rad = b.AddRadiator($"Прибор {i + 1}", 700, ctx);
            b.Connect(prevSup, 1, supTee, 0);
            b.Connect(supTee, 2, rad, 0);
            b.Connect(rad, 1, retTee, 2);
            b.Connect(prevRet, 1, retTee, 0);
            prevSup = supTee; prevRet = retTee;
        }
        return b.Model;
    }
}
