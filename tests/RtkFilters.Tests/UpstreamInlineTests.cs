using RtkFilters;

namespace RtkFilters.Tests;

/// <summary>Runs every [[tests.*]] case shipped in upstream src/filters/*.toml against the .NET engine.</summary>
public class UpstreamInlineTests
{
    private static readonly FilterRegistry Registry = FilterRegistry.LoadBuiltin();

    public static IEnumerable<object[]> Cases() =>
        Registry.Tests.Select(t => new object[] { t.FilterName, t.Name });

    [Theory]
    [MemberData(nameof(Cases))]
    public void Upstream_inline_case_matches(string filter, string name)
    {
        var tc = Registry.Tests.Single(t => t.FilterName == filter && t.Name == name);
        var f = Registry.Get(filter);
        Assert.NotNull(f);
        Assert.Equal(tc.Expected.TrimEnd('\n'), f!.Apply(tc.Input).TrimEnd('\n'));
    }

    [Fact]
    public void All_upstream_filters_load_without_warnings()
    {
        Assert.Empty(Registry.Warnings);
        Assert.Equal(62, Registry.Filters.Count);
    }

    [Fact]
    public void Every_filter_has_inline_tests()
    {
        var tested = Registry.Tests.Select(t => t.FilterName).ToHashSet();
        Assert.All(Registry.Filters, f => Assert.Contains(f.Name, tested));
    }
}
