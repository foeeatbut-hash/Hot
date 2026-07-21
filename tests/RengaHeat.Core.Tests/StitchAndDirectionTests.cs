using RengaHeat.Core.Calculation;
using RengaHeat.Core.Model;
using RengaHeat.Core.Profiles;
using RengaHeat.Core.Topology;

namespace RengaHeat.Core.Tests;

/// <summary>Автосоединение близких точек трассировки и аудит направлений потока.</summary>
public sealed class StitchAndDirectionTests
{
    // ---------- Автосоединение (ProximityStitcher) ----------

    [Fact]
    public void Stitch_ConnectsNearbyFreePorts()
    {
        var b = new ModelBuilder("Разрыв 10 мм");
        var a = b.AddPipe("A");
        var c = b.AddPipe("B");
        b.SetPortLocation(a, 1, 1000, 0, 0);
        b.SetPortLocation(c, 0, 1010, 0, 0);   // зазор 10 мм

        var stitched = ProximityStitcher.Stitch(b.Model, toleranceMm: 50);

        var pair = Assert.Single(stitched);
        Assert.Equal(10, pair.DistanceMm, 1);
        Assert.Single(b.Model.Connections);
        Assert.True(a.Ports[1].IsConnected);
        Assert.True(c.Ports[0].IsConnected);
        Assert.False(b.Model.Connections[0].ModeledAtoB);   // направление не моделировалось
    }

    [Fact]
    public void Stitch_RespectsToleranceAndDnCompatibility()
    {
        var b = new ModelBuilder("Далеко и разные DN");
        var far1 = b.AddPipe("Далеко-1");
        var far2 = b.AddPipe("Далеко-2");
        b.SetPortLocation(far1, 1, 0, 0, 0);
        b.SetPortLocation(far2, 0, 200, 0, 0);      // 200 мм > допуска

        var dn15 = b.AddPipe("Ду15", dn: 15);
        var dn20 = b.AddPipe("Ду20", dn: 20);
        b.SetPortLocation(dn15, 1, 5000, 0, 0);
        b.SetPortLocation(dn20, 0, 5005, 0, 0);     // рядом, но DN не совпадают

        Assert.Empty(ProximityStitcher.Stitch(b.Model, toleranceMm: 50));
        Assert.Empty(b.Model.Connections);
    }

    [Fact]
    public void Stitch_ThreeEndsInOnePoint_JoinsOnlyClosestPair()
    {
        var b = new ModelBuilder("Три конца в точке");
        var p1 = b.AddPipe("П1");
        var p2 = b.AddPipe("П2");
        var p3 = b.AddPipe("П3");
        b.SetPortLocation(p1, 1, 0, 0, 0);
        b.SetPortLocation(p2, 0, 5, 0, 0);     // ближайшая пара: П1–П2
        b.SetPortLocation(p3, 0, 20, 0, 0);

        var stitched = ProximityStitcher.Stitch(b.Model, toleranceMm: 50);

        // Каждый порт участвует один раз: третий конец остаётся открытым на решение инженера.
        var pair = Assert.Single(stitched);
        Assert.Equal(5, pair.DistanceMm, 1);
        Assert.False(p3.Ports[0].IsConnected);
    }

    [Fact]
    public void Stitch_NeverJoinsPortsOfSameObject()
    {
        var b = new ModelBuilder("Короткая труба");
        var pipe = b.AddPipe("Труба");
        b.SetPortLocation(pipe, 0, 0, 0, 0);
        b.SetPortLocation(pipe, 1, 10, 0, 0);   // концы одной трубы рядом

        Assert.Empty(ProximityStitcher.Stitch(b.Model, toleranceMm: 50));
    }

    [Fact]
    public void Session_StitchesGap_AndReportsAssumption()
    {
        var b = new ModelBuilder("Контур с разрывом");
        var source = b.Add("ИТП", ObjectRole.HeatSource);
        var supply = b.AddPipe("Подача", ObjectRole.SupplyMain);
        var rad = b.AddRadiator("Радиатор", 1000, new BuildingContext(Apartment: "кв.1"));
        var ret = b.AddPipe("Обратка", ObjectRole.ReturnMain);
        b.Connect(source, 0, supply, 0);
        // supply.p1 и rad.p0 НЕ соединены — разрыв 20 мм, закрывается автосшивкой
        b.SetPortLocation(supply, 1, 0, 0, 0);
        b.SetPortLocation(rad, 0, 20, 0, 0);
        b.Connect(rad, 1, ret, 0);
        b.Connect(ret, 1, source, 1);

        var outcome = SessionFactory.CreateSession(RequirementsProfile.Novosaratovka()).Run(b.Model);

        Assert.Single(outcome.Stitched);
        Assert.Contains(outcome.AllFindings, f => f.Code == "CON-101");
        Assert.Contains(outcome.Provenance.Assumptions, a => a.Contains("Автосоединение"));
        // После сшивки сеть связна: один фрагмент вместо двух.
        Assert.Single(outcome.Topology.Fragments);
    }

    // ---------- Аудит направлений (DirectionAudit) ----------

    [Fact]
    public void DirectionAudit_FlagsEdgeOrientedAgainstFlow()
    {
        var b = new ModelBuilder("Кольцо с одной перевёрнутой трассой");
        var source = b.Add("ИТП", ObjectRole.HeatSource);
        var supply1 = b.AddPipe("Подача-1", ObjectRole.SupplyMain);
        var supply2 = b.AddPipe("Подача-2", ObjectRole.SupplyMain);
        var rad = b.AddRadiator("Радиатор", 1000, new BuildingContext(Apartment: "кв.1"));
        var ret = b.AddPipe("Обратка", ObjectRole.ReturnMain);

        b.ConnectDirected(source, 0, supply1, 0);   // верно: подача выходит из источника
        b.ConnectDirected(supply2, 1, supply1, 1);  // ПЕРЕВЁРНУТО: должен течь supply1 → supply2
        b.ConnectDirected(supply2, 0, rad, 0);      // верно: подводка входит в прибор
        b.ConnectDirected(rad, 1, ret, 0);          // верно: обратка выходит из прибора
        b.ConnectDirected(ret, 1, source, 1);       // верно: обратка входит в источник

        var topo = new DirectionInference().Analyze(b.Model);
        var audit = DirectionAudit.Audit(b.Model, topo);

        Assert.Equal(5, audit.Checked);
        Assert.Equal(4, audit.Confirmed);
        Assert.Equal(0, audit.Undecidable);
        var issue = Assert.Single(audit.Issues);
        Assert.Equal(supply1.Id, issue.CorrectFromId);   // правильный исток — Подача-1
    }

    [Fact]
    public void DirectionAudit_ReportsUndecidable_InsteadOfGuessing()
    {
        // Кольцо из безликих труб: стороны подача/обратка неизвестны (нет ни ролей, ни приборов).
        // Честный аудит помечает все рёбра неопределимыми, а не «угадывает» несовпадения.
        var b = new ModelBuilder("Неизвестные стороны");
        var source = b.Add("ИТП", ObjectRole.HeatSource);
        var x = b.AddPipe("Труба-X", ObjectRole.Pipe);
        var y = b.AddPipe("Труба-Y", ObjectRole.Pipe);
        var z = b.AddPipe("Труба-Z", ObjectRole.Pipe);
        b.ConnectDirected(source, 0, x, 0);
        b.ConnectDirected(x, 1, y, 0);
        b.ConnectDirected(source, 1, z, 0);
        b.ConnectDirected(z, 1, y, 1);

        var topo = new DirectionInference().Analyze(b.Model);
        var audit = DirectionAudit.Audit(b.Model, topo);

        Assert.Equal(4, audit.Checked);
        Assert.Empty(audit.Issues);
        Assert.Equal(4, audit.Undecidable);
    }

    [Fact]
    public void ManualSideAssignment_WinsOverInference()
    {
        var b = new ModelBuilder("Ручная сторона");
        var source = b.Add("ИТП", ObjectRole.HeatSource);
        var pipe = b.AddPipe("Труба", ObjectRole.Pipe);
        b.Connect(source, 0, pipe, 0);

        var inference = new DirectionInference();
        inference.ManualSides[pipe.Id] = NetworkSide.Return;   // инженер: это обратка
        var topo = inference.Analyze(b.Model);

        Assert.Equal(NetworkSide.Return, topo.SideOf(pipe.Id).Side);
        Assert.Equal(DirectionConfidence.EngineerSet, topo.SideOf(pipe.Id).Confidence);
    }
}
