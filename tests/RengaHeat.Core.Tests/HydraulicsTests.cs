using RengaHeat.Core.Hydraulics;
using Xunit;

namespace RengaHeat.Core.Tests;

public class HydraulicsTests
{
    [Fact]
    public void MassFlow_FromLoad_Matches_Formula()
    {
        // 1000 Вт при графике 80/60: G = Q / (c·ΔT). c≈4190 при 70°C, ΔT=20 → ~0.01194 кг/с ≈ 43 кг/ч
        var g = Water.MassFlowFromLoad(1000, 80, 60);
        Assert.InRange(g * 3600, 42, 44);
    }

    [Fact]
    public void MassFlow_ThrowsOnNonPositiveDeltaT()
    {
        Assert.Throws<ArgumentException>(() => Water.MassFlowFromLoad(1000, 60, 60));
    }

    [Fact]
    public void Reynolds_LaminarBelowTransition()
    {
        var nu = Water.KinematicViscosity(70);
        var re = Friction.Reynolds(0.01, 0.012, nu);
        Assert.True(re < Friction.LaminarReynoldsLimit);
    }

    [Theory]
    [InlineData(FrictionMethod.Churchill)]
    [InlineData(FrictionMethod.ColebrookWhite)]
    [InlineData(FrictionMethod.Altshul)]
    public void FrictionFactor_TurbulentInReasonableRange(FrictionMethod method)
    {
        // v=1 м/с, d=0.05, сталь: λ обычно 0.02–0.04
        var nu = Water.KinematicViscosity(70);
        var re = Friction.Reynolds(1.0, 0.05, nu);
        var lambda = Friction.FrictionFactor(method, re, 0.0002 / 0.05);
        Assert.InRange(lambda, 0.015, 0.06);
    }

    [Fact]
    public void Laminar_MatchesHagenPoiseuille()
    {
        // В ламинарном режиме λ = 64/Re (проверяем Черчилля)
        var re = 1000;
        var lambda = Friction.FrictionFactor(FrictionMethod.Churchill, re, 0.001);
        Assert.InRange(lambda, 64.0 / re * 0.9, 64.0 / re * 1.15);
    }

    [Fact]
    public void KvPressureDrop_And_RequiredKv_AreInverse()
    {
        var dp = Friction.KvPressureDrop(volumeFlowM3H: 2.0, kv: 4.0); // (2/4)²=0.25 бар=25000 Па
        Assert.InRange(dp, 24999, 25001);
        var kv = Friction.RequiredKv(2.0, dp);
        Assert.InRange(kv, 3.99, 4.01);
    }

    [Fact]
    public void DarcyWeisbach_ScalesWithLength()
    {
        var single = Friction.DarcyWeisbach(0.03, 1.0, 0.02, 977, 1.0);
        var tenfold = Friction.DarcyWeisbach(0.03, 10.0, 0.02, 977, 1.0);
        Assert.InRange(tenfold / single, 9.99, 10.01);
    }

    [Fact]
    public void Hydrostatic_PositiveForHeight()
    {
        var p = Friction.Hydrostatic(977, 10);
        Assert.InRange(p, 95000, 96000); // ~ρgh
    }
}
