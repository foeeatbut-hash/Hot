using RengaHeat.Core.Calculation;
using RengaHeat.Core.Profiles;
using RengaHeat.Core.Validation;
using Xunit;

namespace RengaHeat.Core.Tests;

public class SessionTests
{
    private static SessionOutcome Run(RengaHeat.Core.Model.HeatingModel model,
        CalculationScenario? scenario = null)
    {
        var session = SessionFactory.CreateSession(RequirementsProfile.Novosaratovka(), scenario);
        return session.Run(model);
    }

    [Fact]
    public void TwoPipeDeadEnd_Calculates_FlowsAndPump()
    {
        var outcome = Run(TestScenarios.TwoPipeDeadEnd(3));
        Assert.Single(outcome.Results);
        var r = outcome.Results[0];
        Assert.True(r.Converged);
        Assert.Equal(3, r.Devices.Count);
        // Суммарный расход ≈ 3·43 кг/ч
        Assert.InRange(r.TotalFlowKgS * 3600, 120, 135);
        Assert.NotNull(r.CriticalRingDeviceId);
        Assert.True(r.RequiredHeadPa > 0);
    }

    [Fact]
    public void DeviceLoad_ParametrizedFromProperty_ProducesExpectedFlow()
    {
        var outcome = Run(TestScenarios.TwoPipeDeadEnd(1));
        var d = outcome.Results[0].Devices[0];
        Assert.Equal(1000, d.LoadW, 0);
        Assert.InRange(d.MassFlowKgS * 3600, 42, 44); // 1000 Вт при 80/60
    }

    [Fact]
    public void CriticalRing_IsFarthestDevice()
    {
        var outcome = Run(TestScenarios.TwoPipeDeadEnd(3));
        var r = outcome.Results[0];
        // Критическое кольцо — с наименьшим избытком давления (самый дальний прибор)
        Assert.NotNull(r.CriticalRingDeviceId);
        var critical = r.Devices.First(d => d.DeviceId == r.CriticalRingDeviceId);
        var minAvailable = r.Devices.Min(d => d.AvailablePressurePa - d.CircuitLossPa);
        Assert.Equal(minAvailable, critical.AvailablePressurePa - critical.CircuitLossPa, 3);
    }

    [Fact]
    public void SwappedDevicePorts_StillCalculates_WithoutModelChange()
    {
        var model = TestScenarios.SwappedDevicePorts();
        var outcome = Run(model);
        var r = outcome.Results[0];
        // Несмотря на перепутанные порты, прибор рассчитан и получил положительный расход.
        Assert.Single(r.Devices);
        Assert.True(r.Devices[0].MassFlowKgS > 0);
    }

    [Fact]
    public void Balancing_ProducesPresetForNonCriticalRings()
    {
        var outcome = Run(TestScenarios.TwoPipeDeadEnd(3));
        var r = outcome.Results[0];
        // Хотя бы у одного кольца есть рассчитанная преднастройка n.
        Assert.Contains(r.Balancing, b => b.PresetN is not null);
    }

    [Fact]
    public void PreviewChanges_Generated_ButNotApplied()
    {
        var model = TestScenarios.TwoPipeDeadEnd(2);
        var outcome = Run(model);
        Assert.NotEmpty(outcome.PreviewChanges.Changes);
        // По умолчанию режим «только анализ»: ни одно изменение не одобрено.
        Assert.All(outcome.PreviewChanges.Changes, c => Assert.False(c.Approved));
        // Свойства модели не изменены (расход не записан).
        Assert.All(model.Objects.Values, o =>
            Assert.DoesNotContain(o.Properties.Values, p => p.Name == "Расход, кг/ч"));
    }

    [Fact]
    public void QuietScenario_IsSelectable()
    {
        var outcome = Run(TestScenarios.TwoPipeDeadEnd(2), CalculationScenario.Quiet);
        Assert.Equal("Тихий", outcome.Provenance.ScenarioName);
        Assert.True(outcome.Results[0].Converged);
    }

    [Fact]
    public void Provenance_IsReproducible()
    {
        var outcome = Run(TestScenarios.TwoPipeDeadEnd(2));
        Assert.Equal("ЧТУ Новосаратовка", outcome.Provenance.ProfileName);
        Assert.Equal("ЧТУ Новосаратовка 2.pdf", outcome.Provenance.ProfileSource);
        Assert.NotEmpty(outcome.Provenance.Catalogs);
        Assert.Equal(CalculationSession.EngineVersion, outcome.Provenance.EngineVersion);
    }

    [Fact]
    public void ValueJournal_SupportsWhyThisValue()
    {
        var outcome = Run(TestScenarios.TwoPipeDeadEnd(1));
        var radiatorId = outcome.Results[0].Devices[0].DeviceId;
        var record = outcome.ValueJournal.LastOrDefault(r =>
            r.ObjectId == radiatorId && r.FieldKey == "device.load");
        Assert.NotNull(record);
        Assert.Contains("Радиатор", record!.Explain());
    }

    [Fact]
    public void ProfileOverride_ChangesEffectiveProfile()
    {
        var baseProfile = RequirementsProfile.Novosaratovka();
        var over = new ProfileOverride { HeatingSupplyC = 95, HeatingReturnC = 70, MaxVelocityMainMS = 1.5 };
        Assert.True(over.Any);
        var eff = over.ApplyTo(baseProfile);
        Assert.Equal(95, eff.HeatingSchedule.SupplyC);
        Assert.Equal(70, eff.HeatingSchedule.ReturnC);
        Assert.Equal(1.5, eff.MaxVelocityMainMS);
        // Незаданные параметры унаследованы от базового профиля
        Assert.Equal(baseProfile.MaxApartmentsPerManifold, eff.MaxApartmentsPerManifold);
        Assert.Equal(baseProfile.VentilationSchedule.SupplyC, eff.VentilationSchedule.SupplyC);
    }

    [Fact]
    public void ProfileOverride_Empty_LeavesProfileUnchanged()
    {
        var baseProfile = RequirementsProfile.Novosaratovka();
        var eff = new ProfileOverride().ApplyTo(baseProfile);
        Assert.False(new ProfileOverride().Any);
        Assert.Equal(baseProfile.HeatingSchedule.DeltaT, eff.HeatingSchedule.DeltaT);
        Assert.Equal(baseProfile.MaxSpecificLossPaM, eff.MaxSpecificLossPaM);
    }
}
