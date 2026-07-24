using RengaHeat.Core.Catalogs;
using RengaHeat.Core.Hydraulics;
using Xunit;

namespace RengaHeat.Core.Tests;

public class HydraulicsTests
{
    [Fact]
    public void PipeSizing_PicksSmallestDnWithinLimits()
    {
        var cat = PipeCatalog.CreateDefault();
        var steel = cat.BySeries("ВГП ГОСТ 3262").Concat(cat.BySeries("Электросварная ГОСТ 10704")).ToList();
        // Малый расход должен пройти на малом DN, большой — потребовать большего.
        var small = PipeSizing.SelectDiameter(steel, 0.05, 70, new SizingConstraints(1.2, 250));
        var big = PipeSizing.SelectDiameter(steel, 3.0, 70, new SizingConstraints(1.2, 250));
        Assert.True(small.Satisfied);
        Assert.True(big.Satisfied);
        Assert.True(big.Pipe!.Dn > small.Pipe!.Dn);       // больший расход — больший диаметр
        Assert.True(big.VelocityMS <= 1.2 + 1e-6);         // в пределах лимита скорости
        Assert.True(big.SpecificLossPaM <= 250 + 1e-6);    // в пределах лимита удельных потерь
    }

    [Fact]
    public void PipeSizing_TransitionsFromVgpToElectrowelded()
    {
        var cat = PipeCatalog.CreateDefault();
        var steel = cat.BySeries("ВГП ГОСТ 3262").Concat(cat.BySeries("Электросварная ГОСТ 10704")).ToList();
        // Очень большой расход — объединённый ряд должен уйти в электросварные (Ду>50).
        var pick = PipeSizing.SelectDiameter(steel, 8.0, 70, new SizingConstraints(1.2, 250));
        Assert.NotNull(pick.Pipe);
        Assert.True(pick.Pipe!.Dn > 50);
        Assert.Equal("Электросварная ГОСТ 10704", pick.Pipe!.Series);
    }

    [Fact]
    public void PipeSizing_ZeroFlow_ReturnsSmallestWithoutError()
    {
        var cat = PipeCatalog.CreateDefault();
        var steel = cat.BySeries("ВГП ГОСТ 3262").ToList();
        var pick = PipeSizing.SelectDiameter(steel, 0, 70, new SizingConstraints(1.2, 250));
        Assert.True(pick.Satisfied);
        Assert.Equal(15, pick.Pipe!.Dn);
    }

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

    [Fact]
    public void BranchResistance_LaminarRegime_GivesLinearPressureDrop()
    {
        // Малый диаметр + малый расход → ламинарный режим (λ=64/Re ∝ 1/v). Тогда ΔP=R·G²
        // должно быть ЛИНЕЙНО по расходу, а не квадратично — это и есть эффект согласования λ
        // с фактическим расходом (раньше λ замораживался на 0.5 м/с и режим игнорировался).
        double d = 0.006, L = 10, rough = 1e-6, zeta = 0;
        var rho = Water.Density(70);
        var nu = Water.KinematicViscosity(70);
        double g1 = 0.002, g2 = 0.004;

        // Обе точки должны быть в ламинарном режиме, иначе тест бессмыслен.
        var reHi = Friction.Reynolds(Friction.Velocity(g2, rho, d), d, nu);
        Assert.True(reHi < Friction.LaminarReynoldsLimit, $"ожидался ламинарный режим, Re={reHi:0}");

        var r1 = Friction.BranchQuadraticResistance(L, d, rough, zeta, null, rho, nu, g1, FrictionMethod.Churchill);
        var r2 = Friction.BranchQuadraticResistance(L, d, rough, zeta, null, rho, nu, g2, FrictionMethod.Churchill);
        var dp1 = r1 * g1 * g1;
        var dp2 = r2 * g2 * g2;
        // Удвоение расхода → примерно удвоение ΔP (линейно), а не вчетверо (квадратично).
        Assert.InRange(dp2 / dp1, 1.85, 2.2);
    }

    [Fact]
    public void BranchResistance_TurbulentRegime_GivesNearQuadraticPressureDrop()
    {
        // Больший диаметр и расход → турбулентный режим: λ меняется слабо, ΔP≈R·G² ~ квадратично.
        double d = 0.02, L = 10, rough = 0.0002, zeta = 0;
        var rho = Water.Density(70);
        var nu = Water.KinematicViscosity(70);
        double g1 = 0.3, g2 = 0.6;

        var reLo = Friction.Reynolds(Friction.Velocity(g1, rho, d), d, nu);
        Assert.True(reLo > 4000, $"ожидался турбулентный режим, Re={reLo:0}");

        var r1 = Friction.BranchQuadraticResistance(L, d, rough, zeta, null, rho, nu, g1, FrictionMethod.Churchill);
        var r2 = Friction.BranchQuadraticResistance(L, d, rough, zeta, null, rho, nu, g2, FrictionMethod.Churchill);
        var dp1 = r1 * g1 * g1;
        var dp2 = r2 * g2 * g2;
        // Удвоение расхода → рост ΔP близко к четырёхкратному (λ чуть падает с Re).
        Assert.InRange(dp2 / dp1, 3.5, 4.1);
    }

    [Fact]
    public void Solver_AutoGroundsIsolatedComponents_InsteadOfThrowing()
    {
        // Два гидравлически несвязанных островка; опорный узел есть только в первом.
        // Реальные модели приходят с разрывами — решатель обязан выдать результат и счётчик,
        // а не «Система вырождена».
        var resistive = new[]
        {
            new ResistiveBranch("a", "n1", "n2", 1000),
            new ResistiveBranch("b", "m1", "m2", 1000),
        };
        var fixedFlows = new[]
        {
            new FixedFlowBranch("dev1", "n2", "n1", 0.05),
            new FixedFlowBranch("dev2", "m2", "m1", 0.05),
        };

        var result = new HydraulicSolver().Solve(resistive, fixedFlows, new[] { "n1" });

        Assert.Equal(1, result.AutoGroundedComponents);
        Assert.True(result.Converged);
        Assert.Equal(0.05, Math.Abs(result.BranchFlows["b"]), 3);   // островок тоже посчитан
    }
}
