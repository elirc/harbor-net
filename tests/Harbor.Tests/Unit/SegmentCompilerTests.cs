using Harbor.Domain;
using Harbor.Infrastructure;

namespace Harbor.Tests.Unit;

public class SegmentCompilerTests
{
    private static SegmentRuleSet Rules(params SegmentCondition[] conditions) =>
        new(SegmentMatch.All, conditions);

    [Fact]
    public void Compile_WithNoConditions_Throws()
    {
        var ex = Assert.Throws<SegmentRuleException>(
            () => SegmentCompiler.Compile(new SegmentRuleSet(SegmentMatch.All, [])));

        Assert.Contains("at least one condition", ex.Message);
    }

    [Fact]
    public void Compile_WithAnUnknownField_Throws()
    {
        var ex = Assert.Throws<SegmentRuleException>(() => SegmentCompiler.Compile(
            Rules(new SegmentCondition("favouriteColour", SegmentOperator.Equals, "blue"))));

        Assert.Contains("Unknown field", ex.Message);
    }

    [Fact]
    public void Compile_WithAMissingValue_Throws()
    {
        var ex = Assert.Throws<SegmentRuleException>(() => SegmentCompiler.Compile(
            Rules(new SegmentCondition("email", SegmentOperator.Equals, null))));

        Assert.Contains("needs a value", ex.Message);
    }

    [Fact]
    public void Compile_WithAnEmptyAttributeKey_Throws()
    {
        Assert.Throws<SegmentRuleException>(() => SegmentCompiler.Compile(
            Rules(new SegmentCondition("attributes.", SegmentOperator.Exists))));
    }

    [Fact]
    public void Compile_WithAnUnparseableDate_Throws()
    {
        var ex = Assert.Throws<SegmentRuleException>(() => SegmentCompiler.Compile(
            Rules(new SegmentCondition("lastSeenAt", SegmentOperator.After, "whenever"))));

        Assert.Contains("not a date", ex.Message);
    }

    [Fact]
    public void Compile_WithADateOperatorOnText_Throws()
    {
        Assert.Throws<SegmentRuleException>(() => SegmentCompiler.Compile(
            Rules(new SegmentCondition("email", SegmentOperator.After, "2026-01-01"))));
    }

    [Fact]
    public void Compile_WithATextOperatorOnADate_Throws()
    {
        Assert.Throws<SegmentRuleException>(() => SegmentCompiler.Compile(
            Rules(new SegmentCondition("createdAt", SegmentOperator.Contains, "2026"))));
    }

    [Fact]
    public void Compile_ExistsOnAnAlwaysPresentField_Throws()
    {
        var ex = Assert.Throws<SegmentRuleException>(() => SegmentCompiler.Compile(
            Rules(new SegmentCondition("createdAt", SegmentOperator.Exists))));

        Assert.Contains("always present", ex.Message);
    }

    [Fact]
    public void Compile_IsCaseInsensitiveAboutFieldNames()
    {
        var predicate = SegmentCompiler.Compile(
            Rules(new SegmentCondition("  EMAIL  ", SegmentOperator.Contains, "acme"))).Compile();

        Assert.True(predicate(new Domain.Entities.Contact
        {
            WorkspaceId = Guid.NewGuid(),
            Name = "Test",
            Email = "someone@ACME.com",
        }));
    }

    [Fact]
    public void AttributeFields_AreRecognisedByPrefix()
    {
        Assert.Equal("plan", SegmentFields.AttributeKey("attributes.plan"));
        Assert.Null(SegmentFields.AttributeKey("email"));
        Assert.True(SegmentFields.IsDateField("createdat"));
        Assert.False(SegmentFields.IsDateField("email"));
    }
}
