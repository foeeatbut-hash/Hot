using RengaHeat.Core.Hydraulics;
using Xunit;

namespace RengaHeat.Core.Tests;

public class SolverTests
{
    [Fact]
    public void TwoParallelBranches_SplitFlowByResistance()
    {
        // Источник гонит 1 кг/с из R (return) в S (supply). Между S и R две параллельные ветви:
        // одна вдвое «легче» другой → через неё проходит больше расхода.
        var resistive = new[]
        {
            new ResistiveBranch("b1", "S", "R", 1000.0),
            new ResistiveBranch("b2", "S", "R", 4000.0),
        };
        var fixedFlows = new[] { new FixedFlowBranch("src", "R", "S", 1.0) };
        var result = new HydraulicSolver().Solve(resistive, fixedFlows, "S");

        Assert.True(result.Converged);
        var f1 = Math.Abs(result.BranchFlows["b1"]);
        var f2 = Math.Abs(result.BranchFlows["b2"]);
        Assert.InRange(f1 + f2, 0.99, 1.01);            // баланс расходов
        Assert.True(f1 > f2);                            // меньше сопротивление — больше расход
        // R·G² равны для параллельных ветвей: 1000·f1² ≈ 4000·f2²
        Assert.InRange(1000 * f1 * f1 / (4000 * f2 * f2), 0.9, 1.1);
    }

    [Fact]
    public void SeriesLoop_FlowConserved()
    {
        // S → A → R последовательно, источник замыкает R → S.
        var resistive = new[]
        {
            new ResistiveBranch("b1", "S", "A", 1000.0),
            new ResistiveBranch("b2", "A", "R", 1000.0),
        };
        var fixedFlows = new[] { new FixedFlowBranch("src", "R", "S", 0.5) };
        var result = new HydraulicSolver().Solve(resistive, fixedFlows, "S");

        Assert.True(result.Converged);
        Assert.InRange(Math.Abs(result.BranchFlows["b1"]), 0.49, 0.51);
        Assert.InRange(Math.Abs(result.BranchFlows["b2"]), 0.49, 0.51);
    }

    [Fact]
    public void ReversedHypothesis_ProducesNegativeFlowSign()
    {
        // Ветвь задана как S→R, но источник гонит воду R→S по кольцу через неё:
        // знак расхода на ветви отрицателен — направление противоположно гипотезе.
        var resistive = new[] { new ResistiveBranch("b1", "R", "S", 1000.0) };
        var fixedFlows = new[] { new FixedFlowBranch("src", "R", "S", 0.3) };
        var result = new HydraulicSolver().Solve(resistive, fixedFlows, "S");

        Assert.True(result.Converged);
        // b1 определён R→S, но фактический поток по кольцу идёт S→R через ветвь → знак < 0
        Assert.True(result.BranchFlows["b1"] < 0);
    }
}
