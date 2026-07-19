using RengaHeat.Core.Calculation;
using RengaHeat.Core.Classification;
using RengaHeat.Core.Profiles;
using RengaHeat.Core.Topology;
using RengaHeat.Core.Validation;
using Xunit;

namespace RengaHeat.Core.Tests;

public class ValidationTests
{
    private static List<Finding> Validate(RengaHeat.Core.Model.HeatingModel model)
    {
        new Classifier().ClassifyAll(model); // роли уже назначены вручную в сценариях
        var topology = new DirectionInference().Analyze(model);
        return new ModelValidator(RequirementsProfile.Novosaratovka()).Validate(model, topology);
    }

    [Fact]
    public void MoreThan8ApartmentsPerManifold_RaisesError()
    {
        var model = TestScenarios.ManifoldWithApartments(9);
        var findings = Validate(model);
        Assert.Contains(findings, f => f.Code == "CTU-001" && f.Status == FindingStatus.Error);
    }

    [Fact]
    public void Exactly8ApartmentsPerManifold_NoError()
    {
        var model = TestScenarios.ManifoldWithApartments(8);
        var findings = Validate(model);
        Assert.DoesNotContain(findings, f => f.Code == "CTU-001");
    }

    [Fact]
    public void MoreThan10DevicesPerLoop_RaisesError()
    {
        var model = TestScenarios.ApartmentLoopWithDevices(11);
        var findings = Validate(model);
        Assert.Contains(findings, f => f.Code == "CTU-003" && f.Status == FindingStatus.Error);
    }

    [Fact]
    public void Exactly10DevicesPerLoop_NoError()
    {
        var model = TestScenarios.ApartmentLoopWithDevices(10);
        var findings = Validate(model);
        Assert.DoesNotContain(findings, f => f.Code == "CTU-003");
    }

    [Fact]
    public void MissingSource_WithOpenEnd_ProducesItpBoundaryAssumption()
    {
        var b = new RengaHeat.Core.Model.ModelBuilder();
        var supply = b.AddPipe("Подача", RengaHeat.Core.Model.ObjectRole.SupplyMain);
        var rad = b.AddRadiator("Радиатор", 1000, new RengaHeat.Core.Model.BuildingContext(Apartment: "кв.1"));
        b.Connect(supply, 1, rad, 0);
        // Открытый конец supply.p0 → принимается как граница ИТП (допущение SRC-003), а не жёсткое SRC-001.
        var findings = Validate(b.Model);
        Assert.Contains(findings, f => f.Code == "SRC-003" && f.Status == FindingStatus.Assumption);
    }

    [Fact]
    public void ManifoldWithoutDpr_RaisesError()
    {
        var b = new RengaHeat.Core.Model.ModelBuilder();
        var src = b.Add("ИТП", RengaHeat.Core.Model.ObjectRole.HeatSource);
        var man = b.Add("Подающий коллектор", RengaHeat.Core.Model.ObjectRole.SupplyManifold, portCount: 2,
            context: new RengaHeat.Core.Model.BuildingContext(Section: "A"));
        b.Connect(src, 0, man, 0); // между источником и коллектором нет регулятора перепада
        var findings = Validate(b.Model);
        Assert.Contains(findings, f => f.Code == "CTU-006");
    }

    [Fact]
    public void UnknownRole_ProducesNeedsDecision()
    {
        var b = new RengaHeat.Core.Model.ModelBuilder();
        var src = b.Add("ИТП", RengaHeat.Core.Model.ObjectRole.HeatSource);
        var mystery = b.Add("Неизвестный объект", RengaHeat.Core.Model.ObjectRole.Unknown,
            roleSource: RengaHeat.Core.Model.RoleSource.Unassigned);
        b.Connect(src, 0, mystery, 0);
        // Классификатор без правил роль не определит.
        var topology = new DirectionInference().Analyze(b.Model);
        var findings = new ModelValidator(RequirementsProfile.Novosaratovka()).Validate(b.Model, topology);
        Assert.Contains(findings, f => f.Code == "CLS-001");
    }

    [Fact]
    public void NovosaratovkaProfile_HasCorrectLimits()
    {
        var p = RequirementsProfile.Novosaratovka();
        Assert.Equal(8, p.MaxApartmentsPerManifold);
        Assert.Equal(10, p.MaxDevicesPerHorizontalLoop);
        Assert.Equal(18, p.MaxFloorsLowerZone);
        Assert.Equal(80, p.HeatingSchedule.SupplyC);
        Assert.Equal(60, p.HeatingSchedule.ReturnC);
        Assert.Equal(90, p.VentilationSchedule.SupplyC);
        Assert.Equal(1.4, p.MaxApartmentRadiatorLengthM);
    }
}
