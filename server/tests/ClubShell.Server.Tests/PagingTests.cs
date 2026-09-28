using ClubShell.Server.Infrastructure;

namespace ClubShell.Server.Tests;

public sealed class PagingTests
{
    [Theory]
    [InlineData(null, null, 200, 1, 50)]
    [InlineData("3", "20", 200, 3, 20)]
    [InlineData("0", "0", 200, 1, 50)]
    [InlineData("-2", "-5", 200, 1, 1)]
    [InlineData("x", "y", 200, 1, 50)]
    [InlineData("2", "500", 200, 2, 200)]
    [InlineData("1", "5000", 1000, 1, 1000)]
    // parseInt like the mock: leading digits count, overflow saturates, so a huge size is the max and a huge page stays huge.
    [InlineData("1.5", "1e3", 200, 1, 1)]
    [InlineData(" 4", " 20", 200, 4, 20)]
    [InlineData("+2", "20abc", 200, 2, 20)]
    [InlineData("3000000000", "99999999999", 200, int.MaxValue, 200)]
    [InlineData("-99999999999", "-99999999999", 200, 1, 1)]
    public void Normalizes_instead_of_rejecting(string? page, string? pageSize, int max, int expectedPage, int expectedSize)
    {
        Assert.Equal((expectedPage, expectedSize), Paging.Normalize(page, pageSize, max));
    }
}
