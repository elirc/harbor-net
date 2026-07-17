using Harbor.Infrastructure;

namespace Harbor.Tests.Unit;

public class ApiKeysTests
{
    [Fact]
    public void Generate_ProducesPrefixedUniqueKeys()
    {
        var keys = Enumerable.Range(0, 100).Select(_ => ApiKeys.Generate()).ToList();

        Assert.All(keys, k => Assert.StartsWith("hbk_", k));
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void Hash_IsDeterministic_AndHexEncoded()
    {
        var key = ApiKeys.Generate();

        var first = ApiKeys.Hash(key);
        var second = ApiKeys.Hash(key);

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public void Hash_DiffersPerKey()
    {
        Assert.NotEqual(ApiKeys.Hash("hbk_one"), ApiKeys.Hash("hbk_two"));
    }
}
