using Microsoft.Extensions.Logging.Abstractions;
using PgDataHub.Metadata;

namespace PgDataHub.Tests;

public sealed class CollectionOverrideTests
{
    [Fact]
    public void Apply_makes_hide_win_over_allow()
    {
        var filtered = CollectionOverrideApplier.Apply(
            TestData.Collection("id", "secret", "name"), ["id", "secret"], ["secret"], true,
            NullLogger.Instance);

        Assert.Equal(["id"], filtered.Columns.Select(c => c.Name));
    }

    [Fact]
    public void Apply_does_not_mutate_original_metadata()
    {
        var original = TestData.Collection("id", "secret");
        var filtered = CollectionOverrideApplier.Apply(original, [], ["secret"], true, NullLogger.Instance);

        Assert.Equal(2, original.Columns.Count);
        Assert.Equal(["id"], filtered.Columns.Select(c => c.Name));
    }
}
