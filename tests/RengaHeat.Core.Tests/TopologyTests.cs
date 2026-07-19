using RengaHeat.Core.Model;
using RengaHeat.Core.Topology;
using Xunit;

namespace RengaHeat.Core.Tests;

public class TopologyTests
{
    private static (HeatingModel, ModelBuilder) BuildSimpleTwoPipe()
    {
        var b = new ModelBuilder("Двухтрубная тупиковая");
        var src = b.Add("ИТП", ObjectRole.HeatSource);
        var supply = b.AddPipe("Подача", ObjectRole.SupplyMain);
        var rad = b.AddRadiator("Радиатор 1", 1000,
            new BuildingContext(Section: "1", Floor: 1, Apartment: "кв.1"));
        var ret = b.AddPipe("Обратка", ObjectRole.ReturnMain);
        // ИТП(p0)→Подача→Радиатор→Обратка→ИТП(p1)
        b.Connect(src, 0, supply, 0);
        b.Connect(supply, 1, rad, 0);
        b.Connect(rad, 1, ret, 0);
        b.Connect(ret, 1, src, 1);
        return (b.Model, b);
    }

    [Fact]
    public void ConnectedGraph_SingleComponent()
    {
        var (model, _) = BuildSimpleTwoPipe();
        var graph = new NetworkGraph(model);
        Assert.Single(graph.ConnectedComponents());
    }

    [Fact]
    public void BrokenNetwork_ProducesMultipleFragments()
    {
        var b = new ModelBuilder("Разрыв сети");
        var src = b.Add("ИТП", ObjectRole.HeatSource);
        var supply = b.AddPipe("Подача", ObjectRole.SupplyMain);
        b.Connect(src, 0, supply, 0);
        // Второй прибор оставлен несвязанным — разрыв сети
        b.AddRadiator("Радиатор изолированный", 800, new BuildingContext(Apartment: "кв.2"));

        var analysis = new DirectionInference().Analyze(b.Model);
        Assert.True(analysis.Fragments.Count >= 2);
        Assert.Contains(analysis.Notes, n => n.Contains("фрагмент"));
    }

    [Fact]
    public void Source_And_Devices_GetDeterminedSides()
    {
        var (model, _) = BuildSimpleTwoPipe();
        var analysis = new DirectionInference().Analyze(model);
        Assert.Single(analysis.Sources);
        Assert.Equal(NetworkSide.Source, analysis.SideOf(model.Objects.Values.First(o => o.Name == "ИТП").Id).Side);
        Assert.Equal(NetworkSide.Device, analysis.SideOf(model.Objects.Values.First(o => o.Name == "Радиатор 1").Id).Side);
    }

    [Fact]
    public void SupplyReturnRoles_InferSides()
    {
        var (model, _) = BuildSimpleTwoPipe();
        var analysis = new DirectionInference().Analyze(model);
        var supply = model.Objects.Values.First(o => o.Name == "Подача");
        var ret = model.Objects.Values.First(o => o.Name == "Обратка");
        Assert.Equal(NetworkSide.Supply, analysis.SideOf(supply.Id).Side);
        Assert.Equal(NetworkSide.Return, analysis.SideOf(ret.Id).Side);
    }

    [Fact]
    public void MissingSource_Noted_ButAnalysisContinues()
    {
        var b = new ModelBuilder("Без ИТП");
        var supply = b.AddPipe("Подача", ObjectRole.SupplyMain);
        var rad = b.AddRadiator("Радиатор", 1000, new BuildingContext(Apartment: "кв.1"));
        b.Connect(supply, 1, rad, 0);

        var analysis = new DirectionInference().Analyze(b.Model);
        Assert.Empty(analysis.Sources);
        Assert.Contains(analysis.Notes, n => n.Contains("Источник тепла не найден"));
    }

    [Fact]
    public void TemporarySource_UsedWhenNoItp()
    {
        var b = new ModelBuilder("Временный источник");
        var node = b.Add("Ввод (условно источник)", ObjectRole.Pipe);
        var rad = b.AddRadiator("Радиатор", 1000, new BuildingContext(Apartment: "кв.1"));
        b.Connect(node, 1, rad, 0);

        var inference = new DirectionInference { TemporarySourceObjectId = node.Id };
        var analysis = inference.Analyze(b.Model);
        Assert.Contains(analysis.Sources, s => s.Id == node.Id);
        Assert.Contains(analysis.Notes, n => n.Contains("временный источник"));
    }
}
