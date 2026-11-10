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
    public void Match(string glob, string path, bool expected) => Assert.Equal(expected, Glob.Match(glob, path));

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
