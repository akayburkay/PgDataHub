using PgDataHub.Config;

namespace PgDataHub.Tests;

public sealed class GlobTests
{
    [Theory]
    [InlineData("public.get_roads", "*.get_*", true)]
    [InlineData("public.fn1", "public.fn?", true)]
    [InlineData("PUBLIC.ROADS", "public.*", true)]
    [InlineData("private.fn1", "public.*", false)]
    [InlineData("public.road", "public.roads", false)]
    [InlineData("anything", "*", true)]
    public void Match_supports_case_insensitive_wildcards(string value, string pattern, bool expected)
        => Assert.Equal(expected, Glob.Match(value, pattern));
}
