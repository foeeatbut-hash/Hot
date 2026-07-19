using RengaHeat.Core.Calculation;
using RengaHeat.Core.Classification;
using RengaHeat.Core.Mapping;
using RengaHeat.Core.Model;
using RengaHeat.Core.Rules;
using Xunit;

namespace RengaHeat.Core.Tests;

public class ClassificationAndRulesTests
{
    [Theory]
    [InlineData("Радиатор стальной 500", ObjectRole.Radiator)]
    [InlineData("Конвектор напольный", ObjectRole.Convector)]
    [InlineData("Насос циркуляционный", ObjectRole.Pump)]
    [InlineData("Балансировочный клапан ASV", ObjectRole.BalancingValve)]
    [InlineData("Фильтр сетчатый", ObjectRole.Strainer)]
    [InlineData("Стояк Т1", ObjectRole.Riser)]
    [InlineData("Труба ВГП", ObjectRole.Pipe)]
    public void DefaultClassifier_RecognisesByName(string name, ObjectRole expected)
    {
        var obj = new NetworkObject { Id = "x", Name = name };
        Assert.Equal(expected, SessionFactory.DefaultClassifier().Classify(obj).Assigned.Role);
    }

    [Fact]
    public void DefaultClassifier_TeeByPortCount_WhenNameSilent()
    {
        var obj = new NetworkObject { Id = "x", Name = "фитинг узла" };
        obj.Ports.Add(new Port("a", Dn: 25));
        obj.Ports.Add(new Port("b", Dn: 25));
        obj.Ports.Add(new Port("c", Dn: 20));
        Assert.Equal(ObjectRole.Tee, SessionFactory.DefaultClassifier().Classify(obj).Assigned.Role);
    }

    [Fact]
    public void SuggestRolesByType_TakesMajorityPerType()
    {
        var model = new HeatingModel { Name = "m" };
        model.Add(new NetworkObject { Id = "1", Name = "Радиатор A", RengaTypeId = "TYPE-RAD" });
        model.Add(new NetworkObject { Id = "2", Name = "Радиатор B", RengaTypeId = "TYPE-RAD" });
        model.Add(new NetworkObject { Id = "3", Name = "нечто", RengaTypeId = "TYPE-RAD" });
        var suggestions = SessionFactory.DefaultClassifier().SuggestRolesByType(model);
        Assert.Equal(ObjectRole.Radiator, suggestions["TYPE-RAD"]);
    }

    [Fact]
    public void MappingsFor_PrependsUserPropertyPerField()
    {
        var overrides = new Dictionary<string, string>
        {
            [StandardFields.PipeLength.Key] = "МояДлина",
        };
        var mappings = SessionFactory.MappingsFor(overrides);
        var lengthRule = mappings.Rules.First(r => r.Field.Key == StandardFields.PipeLength.Key);
        Assert.Equal("Свойство экземпляра", lengthRule.SourceChain[0].Kind);
        Assert.Contains("МояДлина", ((InstancePropertySource)lengthRule.SourceChain[0]).PropertyName);
    }

    [Fact]
    public void ManualRole_WinsOverRules()
    {
        var obj = new NetworkObject { Id = "x", Name = "радиатор настенный",
            Role = new RoleAssignment(ObjectRole.Convector, RoleSource.Manual) };
        var c = new Classifier();
        c.AddRule(new RoleRule("по имени", new RoleCriteria { NameRegex = "радиатор" }, ObjectRole.Radiator, 10));
        var outcome = c.Classify(obj);
        Assert.Equal(ObjectRole.Convector, outcome.Assigned.Role);
        Assert.Equal(RoleSource.Manual, outcome.Assigned.Source);
    }

    [Fact]
    public void OvRoleProperty_OverridesRules()
    {
        var obj = new NetworkObject { Id = "x", Name = "нечто" };
        var pid = ModelBuilder.PropId(Classifier.OvRolePropertyName);
        obj.Properties[pid] = new PropertyValue(pid, Classifier.OvRolePropertyName, "Стояк");
        var c = new Classifier();
        var outcome = c.Classify(obj);
        Assert.Equal(ObjectRole.Riser, outcome.Assigned.Role);
        Assert.Equal(RoleSource.OvRoleProperty, outcome.Assigned.Source);
    }

    [Fact]
    public void ConflictingSamePriorityRules_ProduceConflict()
    {
        var obj = new NetworkObject { Id = "x", Name = "объект",
            Ports = { }, Category = "Труба" };
        var c = new Classifier();
        c.AddRule(new RoleRule("A", new RoleCriteria { Category = "Труба" }, ObjectRole.SupplyMain, 5));
        c.AddRule(new RoleRule("B", new RoleCriteria { Category = "Труба" }, ObjectRole.ReturnMain, 5));
        var outcome = c.Classify(obj);
        Assert.True(outcome.HasConflict);
        Assert.Equal(ObjectRole.Unknown, outcome.Assigned.Role);
    }

    [Fact]
    public void HigherPriorityRule_Wins()
    {
        var obj = new NetworkObject { Id = "x", Name = "объект", Category = "Труба" };
        var c = new Classifier();
        c.AddRule(new RoleRule("низкий", new RoleCriteria { Category = "Труба" }, ObjectRole.Pipe, 1));
        c.AddRule(new RoleRule("высокий", new RoleCriteria { Category = "Труба" }, ObjectRole.Riser, 10));
        var outcome = c.Classify(obj);
        Assert.Equal(ObjectRole.Riser, outcome.Assigned.Role);
    }

    [Fact]
    public void DnCriteria_MatchesByPort()
    {
        var obj = new NetworkObject { Id = "x", Name = "стояк" };
        obj.Ports.Add(new Port("p0", Dn: 40));
        var c = new Classifier();
        c.AddRule(new RoleRule("малый DN", new RoleCriteria { MaxDn = 50 }, ObjectRole.Riser, 5));
        Assert.Equal(ObjectRole.Riser, c.Classify(obj).Assigned.Role);
    }

    [Fact]
    public void RuleEngine_EffectiveLimit_ConservativeOnConflict()
    {
        var engine = new RuleEngine();
        engine.Add(new EngineeringRule
        {
            Name = "лимит A", Action = new RuleAction(RuleActionKind.SetLimit, LimitKeys.MaxVelocity, 0.8), Priority = 5,
        });
        engine.Add(new EngineeringRule
        {
            Name = "лимит B", Action = new RuleAction(RuleActionKind.SetLimit, LimitKeys.MaxVelocity, 0.6), Priority = 5,
        });
        var obj = new NetworkObject { Id = "x", Name = "труба",
            Role = new RoleAssignment(ObjectRole.Pipe, RoleSource.Manual) };
        var limit = engine.EffectiveLimit(obj, LimitKeys.MaxVelocity, out var conflict);
        Assert.True(conflict);
        Assert.Equal(0.6, limit); // консервативно берём минимальное
    }

    [Fact]
    public void RuleException_SuppressesRule()
    {
        var engine = new RuleEngine();
        engine.Add(new EngineeringRule
        {
            Name = "лимит", Action = new RuleAction(RuleActionKind.SetLimit, LimitKeys.MaxVelocity, 0.8), Priority = 5,
        });
        var obj = new NetworkObject { Id = "x", Name = "труба",
            Role = new RoleAssignment(ObjectRole.Pipe, RoleSource.Manual) };
        engine.AddException("лимит", "x", "особый участок", "инженер");
        var limit = engine.EffectiveLimit(obj, LimitKeys.MaxVelocity, out _);
        Assert.Null(limit); // правило подавлено исключением
        Assert.Single(engine.Exceptions);
    }
}
