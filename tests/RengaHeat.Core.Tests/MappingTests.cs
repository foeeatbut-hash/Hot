using RengaHeat.Core.Mapping;
using RengaHeat.Core.Model;
using Xunit;

namespace RengaHeat.Core.Tests;

public class MappingTests
{
    private static NetworkObject Radiator(string name) => new()
    {
        Id = name, Name = name, Role = new RoleAssignment(ObjectRole.Radiator, RoleSource.Manual),
    };

    private static ResolutionContext Ctx(HeatingModel model) => new() { Model = model };

    [Fact]
    public void FallbackChain_UsesFirstAvailableSource()
    {
        var model = new HeatingModel();
        var rad = Radiator("R-1");
        // Свойство «Q_расч» отсутствует, но есть «Теплопотери» — цепочка должна дойти до него.
        var pid = ModelBuilder.PropId("Теплопотери");
        rad.Properties[pid] = new PropertyValue(pid, "Теплопотери", 1500.0);
        model.Add(rad);

        var mappings = new MappingSet();
        mappings.Add(new MappingRule
        {
            Name = "нагрузка",
            Field = StandardFields.DeviceLoad,
            AppliesToRoles = new[] { ObjectRole.Radiator },
            SourceChain = new ValueSource[]
            {
                new InstancePropertySource(ModelBuilder.PropId("Q_расч"), "Q_расч"),
                new InstancePropertySource(ModelBuilder.PropId("Теплопотери"), "Теплопотери"),
                new ManualInputSource(),
            },
            SourceUnitSymbol = "Вт",
        });

        var resolver = new ValueResolver(mappings, Ctx(model));
        var value = resolver.Resolve(rad, StandardFields.DeviceLoad);

        Assert.Equal(ResolveStatus.Ok, value.Status);
        Assert.Equal(1500.0, value.Number);
    }

    [Fact]
    public void RenamedProperty_StillResolvesByStableId()
    {
        var model = new HeatingModel();
        var rad = Radiator("R-2");
        var stableId = ModelBuilder.PropId("Q_расч");
        // Свойство переименовано в модели («Мощность»), но устойчивый ID тот же.
        rad.Properties[stableId] = new PropertyValue(stableId, "Мощность", 2000.0);
        model.Add(rad);

        var mappings = new MappingSet();
        mappings.Add(new MappingRule
        {
            Name = "нагрузка",
            Field = StandardFields.DeviceLoad,
            AppliesToRoles = new[] { ObjectRole.Radiator },
            SourceChain = new ValueSource[] { new InstancePropertySource(stableId, "Q_расч") },
            SourceUnitSymbol = "Вт",
        });

        var resolver = new ValueResolver(mappings, Ctx(model));
        var value = resolver.Resolve(rad, StandardFields.DeviceLoad);
        Assert.Equal(2000.0, value.Number);
    }

    [Fact]
    public void UnitConversion_KwToW()
    {
        var model = new HeatingModel();
        var rad = Radiator("R-3");
        var pid = ModelBuilder.PropId("Q");
        rad.Properties[pid] = new PropertyValue(pid, "Q", 1.5); // 1.5 кВт
        model.Add(rad);

        var mappings = new MappingSet();
        mappings.Add(new MappingRule
        {
            Name = "нагрузка кВт",
            Field = StandardFields.DeviceLoad,
            AppliesToRoles = new[] { ObjectRole.Radiator },
            SourceChain = new ValueSource[] { new InstancePropertySource(pid, "Q") },
            SourceUnitSymbol = "кВт",
        });

        var resolver = new ValueResolver(mappings, Ctx(model));
        var value = resolver.Resolve(rad, StandardFields.DeviceLoad);
        Assert.Equal(1500.0, value.Number); // приведено к Вт
    }

    [Fact]
    public void WhyThisValue_ExplainsProvenance()
    {
        var model = new HeatingModel();
        var rad = Radiator("R-12");
        var pid = ModelBuilder.PropId("Q_расч");
        rad.Properties[pid] = new PropertyValue(pid, "Q_расч", 1000.0);
        model.Add(rad);

        var mappings = new MappingSet();
        mappings.Add(new MappingRule
        {
            Name = "квартиры",
            Field = StandardFields.DeviceLoad,
            AppliesToRoles = new[] { ObjectRole.Radiator },
            SourceChain = new ValueSource[] { new InstancePropertySource(pid, "Q_расч") },
            SourceUnitSymbol = "Вт",
        });

        var resolver = new ValueResolver(mappings, Ctx(model));
        resolver.Resolve(rad, StandardFields.DeviceLoad);

        var why = resolver.WhyThisValue("R-12", StandardFields.DeviceLoad.Key);
        Assert.NotNull(why);
        var text = why!.Explain();
        Assert.Contains("R-12", text);
        Assert.Contains("Радиатор", text);
        Assert.Contains("квартиры", text);
        Assert.Contains("Q_расч", text);
    }

    [Fact]
    public void OutOfRange_ProducesError()
    {
        var model = new HeatingModel();
        var rad = Radiator("R-4");
        var pid = ModelBuilder.PropId("Q_расч");
        rad.Properties[pid] = new PropertyValue(pid, "Q_расч", 5_000_000.0); // выше Max
        model.Add(rad);

        var mappings = new MappingSet();
        mappings.Add(new MappingRule
        {
            Name = "нагрузка",
            Field = StandardFields.DeviceLoad,
            AppliesToRoles = new[] { ObjectRole.Radiator },
            SourceChain = new ValueSource[] { new InstancePropertySource(pid, "Q_расч") },
            SourceUnitSymbol = "Вт",
        });

        var resolver = new ValueResolver(mappings, Ctx(model));
        var value = resolver.Resolve(rad, StandardFields.DeviceLoad);
        Assert.Equal(ResolveStatus.Error, value.Status);
    }

    [Fact]
    public void MissingWithErrorPolicy_ReportsError()
    {
        var model = new HeatingModel();
        var rad = Radiator("R-5");
        model.Add(rad);

        var mappings = new MappingSet();
        mappings.Add(new MappingRule
        {
            Name = "нагрузка",
            Field = StandardFields.DeviceLoad,
            AppliesToRoles = new[] { ObjectRole.Radiator },
            SourceChain = new ValueSource[]
            {
                new InstancePropertySource(ModelBuilder.PropId("Q_расч"), "Q_расч"),
            },
            SourceUnitSymbol = "Вт",
        });

        var resolver = new ValueResolver(mappings, Ctx(model));
        var value = resolver.Resolve(rad, StandardFields.DeviceLoad);
        Assert.Equal(ResolveStatus.Error, value.Status);
    }

    [Theory]
    [InlineData("2 + 3 * 4", 14)]
    [InlineData("(2 + 3) * 4", 20)]
    [InlineData("2 ^ 10", 1024)]
    [InlineData("abs(-5)", 5)]
    [InlineData("max(3; 7)", 7)]
    [InlineData("sqrt(16)", 4)]
    public void FormulaEvaluator_ComputesExpressions(string expr, double expected)
    {
        Assert.Equal(expected, FormulaEvaluator.Evaluate(expr, _ => null), 6);
    }

    [Fact]
    public void FormulaEvaluator_ResolvesVariables()
    {
        var result = FormulaEvaluator.Evaluate("Q / (c * dt)",
            name => name switch { "Q" => 1000.0, "c" => 4190.0, "dt" => 20.0, _ => (object?)null });
        Assert.InRange(result, 0.011, 0.013);
    }
}
