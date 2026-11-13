namespace Sankofa.Tests;

public class GlobTests
{
    [Theory]
    [InlineData("src/a.c", "src/a.c", true)]
    [InlineData("src/*.c", "src/a.c", true)]
    [InlineData("src/*.c", "src/sub/a.c", false)]
    [InlineData("src/**", "src/sub/deep/a.c", true)]
    [InlineData("src/", "src/a.c", true)]
    [InlineData("src/**/*.h", "src/x/y/a.h", true)]
    [InlineData("src/**/*.h", "src/a.h", true)]
    [InlineData("src/**/*.h", "src/a.c", false)]
    [InlineData("./src//a?.c", "src/ab.c", true)]
    [InlineData("**", "anything/at/all", true)]
    [InlineData("docs/*", "src/a.c", false)]
    [InlineData("**/**/a.c", "x/y/a.c", true)]
    [InlineData("a/**/b/**/c", "a/b/c", true)]
    [InlineData("a/**/b/**/c", "a/x/b/y/b/z/c", true)]
    [InlineData("a/**/b/**/c", "a/x/c", false)]
    [InlineData("**/x", "x", true)]
    [InlineData("src/**/", "src", true)]
    public void Match(string glob, string path, bool expected) => Assert.Equal(expected, Glob.Match(glob, path));

    // a glob is whatever a session claims; many ** against a deep path must not take the daemon down
    [Fact]
    public void ManyDoubleStarsMatchInLinearTime()
    {
        var glob = string.Concat(Enumerable.Repeat("**/", 24)) + "nope";
        var path = string.Join('/', Enumerable.Repeat("a", 40)) + "/b.c";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 100; i++) Assert.False(Glob.Match(glob, path));
        Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Theory]
    [InlineData("src/a.c", "src/a.c", true)]
    [InlineData("src/a.c", "src/b.c", false)]
    [InlineData("src/**", "src/a.c", true)]
    [InlineData("src/*.c", "src/*.h", false)]
    [InlineData("src/*.c", "src/main*", true)]
    [InlineData("src/*", "docs/*", false)]
    [InlineData("src/**", "src/ui/**", true)]
    [InlineData("src/ui/**", "src/net/**", false)]
    [InlineData("src/*/a.c", "src/*.c", false)]
    [InlineData("include/shared.h", "include/*.h", true)]
    public void Overlap(string a, string b, bool expected)
    {
        Assert.Equal(expected, Glob.Overlap(a, b));
        Assert.Equal(expected, Glob.Overlap(b, a));
    }
}
