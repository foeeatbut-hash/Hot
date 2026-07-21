namespace RengaHeat.Core.Hydraulics;

/// <summary>
/// Ветвь с квадратичным сопротивлением: ΔP = R · G · |G|, где G — массовый расход (кг/с),
/// R — коэффициент сопротивления, Па/(кг/с)².
/// </summary>
public sealed record ResistiveBranch(string Id, string FromNode, string ToNode, double Resistance);

/// <summary>
/// Ветвь с фиксированным расходом (кг/с) от FromNode к ToNode.
/// Так моделируются отопительные приборы с проектным расходом и источник с суммарным расходом.
/// </summary>
public sealed record FixedFlowBranch(string Id, string FromNode, string ToNode, double Flow);

public sealed record SolverResult(
    IReadOnlyDictionary<string, double> NodePressures,
    IReadOnlyDictionary<string, double> BranchFlows,
    int Iterations,
    bool Converged,
    int AutoGroundedComponents = 0)   // островков схемы, заземлённых автоматически (данные с разрывами)
{
    /// <summary>Перепад давления на ветви с фиксированным расходом (располагаемый напор на ней).</summary>
    public double PressureDropAcross(FixedFlowBranch branch) =>
        NodePressures[branch.FromNode] - NodePressures[branch.ToNode];
}

/// <summary>
/// Узловой гидравлический решатель для кольцевых, попутных и параллельных схем.
/// Метод линеаризации («linear theory»): итеративно решается система балансов расходов
/// в узлах при линеаризованных проводимостях ветвей до сходимости.
///
/// Направление потока определяется знаком расчётного расхода: отрицательный расход —
/// не ошибка, а сигнал, что фактическое направление противоположно исходной гипотезе.
/// </summary>
public sealed class HydraulicSolver
{
    public double ToleranceKgS { get; init; } = 1e-7;
    public int MaxIterations { get; init; } = 200;
    /// <summary>Нижняя граница |G| при линеаризации, чтобы проводимость не вырождалась.</summary>
    public double LinearizationFloorKgS { get; init; } = 1e-5;

    public SolverResult Solve(
        IReadOnlyList<ResistiveBranch> resistive,
        IReadOnlyList<FixedFlowBranch> fixedFlows,
        string referenceNode)
        => Solve(resistive, fixedFlows, new[] { referenceNode });

    /// <summary>
    /// Решение с несколькими опорными узлами. Каждый связный резистивный компонент должен
    /// содержать хотя бы один опорный узел, иначе система вырождена. В двухтрубной сети
    /// подающая и обратная стороны — раздельные компоненты, поэтому заземляются оба узла источника.
    /// </summary>
    public SolverResult Solve(
        IReadOnlyList<ResistiveBranch> resistive,
        IReadOnlyList<FixedFlowBranch> fixedFlows,
        IReadOnlyCollection<string> referenceNodes)
    {
        var nodes = resistive.SelectMany(b => new[] { b.FromNode, b.ToNode })
            .Concat(fixedFlows.SelectMany(b => new[] { b.FromNode, b.ToNode }))
            .Distinct()
            .ToList();
        foreach (var reference in referenceNodes)
            if (!nodes.Contains(reference))
                throw new ArgumentException($"Опорный узел «{reference}» не входит в схему.");

        var index = nodes.Select((n, i) => (n, i)).ToDictionary(t => t.n, t => t.i);
        var n = nodes.Count;

        // Реальные схемы приходят с разрывами: часть узлов образует резистивные «островки» без
        // опорного узла, а узлы только с фиксированными ветвями вовсе не имеют проводимостей.
        // Такие компоненты заземляются автоматически (свой нуль давления в каждом островке) —
        // система остаётся решаемой, а факт разрывов сообщается наверх счётчиком, не исключением.
        var effectiveReferences = new HashSet<string>(referenceNodes);
        var autoGrounded = 0;
        {
            var adjacency = new Dictionary<string, List<string>>();
            foreach (var node in nodes) adjacency[node] = new List<string>();
            foreach (var br in resistive)
            {
                adjacency[br.FromNode].Add(br.ToNode);
                adjacency[br.ToNode].Add(br.FromNode);
            }
            var visited = new HashSet<string>();
            foreach (var start in nodes)
            {
                if (!visited.Add(start)) continue;
                var component = new List<string> { start };
                var queue = new Queue<string>();
                queue.Enqueue(start);
                while (queue.Count > 0)
                    foreach (var next in adjacency[queue.Dequeue()])
                        if (visited.Add(next)) { component.Add(next); queue.Enqueue(next); }
                if (!component.Any(effectiveReferences.Contains))
                {
                    effectiveReferences.Add(component[0]);
                    autoGrounded++;
                }
            }
        }

        // Инжекции от ветвей с фиксированным расходом: из FromNode уходит, в ToNode приходит
        var injection = new double[n];
        foreach (var f in fixedFlows)
        {
            injection[index[f.FromNode]] -= f.Flow;
            injection[index[f.ToNode]] += f.Flow;
        }

        var flows = resistive.ToDictionary(b => b.Id, _ => LinearizationFloorKgS * 10);
        var pressures = new double[n];
        var converged = false;
        var iteration = 0;

        for (; iteration < MaxIterations && !converged; iteration++)
        {
            // Сборка линеаризованной системы: A·p = b
            var a = new double[n, n];
            var b = new double[n];
            for (var i = 0; i < n; i++) b[i] = injection[i];

            foreach (var br in resistive)
            {
                var g = Math.Max(Math.Abs(flows[br.Id]), LinearizationFloorKgS);
                var conductance = 1.0 / (br.Resistance * g); // G = c·(p_from − p_to)
                int i = index[br.FromNode], j = index[br.ToNode];
                a[i, i] += conductance;
                a[j, j] += conductance;
                a[i, j] -= conductance;
                a[j, i] -= conductance;
            }

            // Опорные узлы: p = 0 (включая автозаземлённые островки)
            foreach (var reference in effectiveReferences)
            {
                var r = index[reference];
                for (var k = 0; k < n; k++) { a[r, k] = 0; }
                a[r, r] = 1;
                b[r] = 0;
            }

            pressures = SolveLinearSystem(a, b);

            // Обновление расходов с релаксацией
            var maxChange = 0.0;
            foreach (var br in resistive)
            {
                var g = Math.Max(Math.Abs(flows[br.Id]), LinearizationFloorKgS);
                var conductance = 1.0 / (br.Resistance * g);
                var newFlow = conductance * (pressures[index[br.FromNode]] - pressures[index[br.ToNode]]);
                var damped = 0.5 * (newFlow + flows[br.Id]);
                maxChange = Math.Max(maxChange, Math.Abs(damped - flows[br.Id]));
                flows[br.Id] = damped;
            }
            converged = maxChange < ToleranceKgS;
        }

        var nodePressures = nodes.ToDictionary(nd => nd, nd => pressures[index[nd]]);
        var branchFlows = new Dictionary<string, double>(flows);
        foreach (var f in fixedFlows)
            branchFlows[f.Id] = f.Flow;

        return new SolverResult(nodePressures, branchFlows, iteration, converged, autoGrounded);
    }

    /// <summary>Гауссово исключение с выбором главного элемента по столбцу.</summary>
    private static double[] SolveLinearSystem(double[,] a, double[] b)
    {
        var n = b.Length;
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var row = col + 1; row < n; row++)
                if (Math.Abs(a[row, col]) > Math.Abs(a[pivot, col]))
                    pivot = row;
            if (Math.Abs(a[pivot, col]) < 1e-14)
                throw new InvalidOperationException(
                    "Система вырождена: проверьте связность схемы и наличие опорного узла.");
            if (pivot != col)
            {
                for (var k = 0; k < n; k++)
                    (a[col, k], a[pivot, k]) = (a[pivot, k], a[col, k]);
                (b[col], b[pivot]) = (b[pivot], b[col]);
            }
            for (var row = col + 1; row < n; row++)
            {
                var factor = a[row, col] / a[col, col];
                if (factor == 0) continue;
                for (var k = col; k < n; k++)
                    a[row, k] -= factor * a[col, k];
                b[row] -= factor * b[col];
            }
        }

        var x = new double[n];
        for (var row = n - 1; row >= 0; row--)
        {
            var sum = b[row];
            for (var k = row + 1; k < n; k++)
                sum -= a[row, k] * x[k];
            x[row] = sum / a[row, row];
        }
        return x;
    }
}
