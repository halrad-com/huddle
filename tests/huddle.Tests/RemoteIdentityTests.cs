using Huddle;
namespace Huddle.Tests;

public class RemoteIdentityTests
{
    [Theory]
    [InlineData("https://contoso@dev.azure.com/contoso/LIB/_git/LIB", "dev.azure.com/contoso/LIB")]
    [InlineData("https://dev.azure.com/contoso/ReferenceCode/_git/Other", "dev.azure.com/contoso/ReferenceCode/Other")]
    [InlineData("https://github.com/halrad-com/otherapp.git", "github.com/halrad-com/otherapp")]
    [InlineData("git@github.com:halrad-com/huddle.git", "github.com/halrad-com/huddle")]
    [InlineData("ssh://git@github.com/halrad-com/huddle.git", "github.com/halrad-com/huddle")]
    [InlineData("https://user:token@github.com/o/r", "github.com/o/r")]
    [InlineData("https://GitHub.com/O/R/", "github.com/O/R")]
    public void Parses_to_host_org_repo(string url, string expect) => Assert.Equal(expect, RemoteIdentity.Parse(url));

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("   ")] [InlineData("not a url")] [InlineData("C:\\local\\bare.git")]
    public void Unresolvable_is_null(string? url) => Assert.Null(RemoteIdentity.Parse(url));

    [Fact]
    public void Userinfo_never_survives()
    {
        var r = RemoteIdentity.Parse("https://contoso@dev.azure.com/contoso/LIB/_git/LIB")!;
        Assert.DoesNotContain("contoso@", r);
        Assert.DoesNotContain("@", r);
    }

    [Fact]
    public void Parses_git_remote_v_output()
    {
        var text = "github\thttps://github.com/halrad-com/otherapp.git (fetch)\ngithub\thttps://github.com/halrad-com/otherapp.git (push)\norigin\thttps://contoso@dev.azure.com/contoso/LIB/_git/LIB (fetch)\norigin\thttps://contoso@dev.azure.com/contoso/LIB/_git/LIB (push)\n";
        var m = RemoteIdentity.ParseRemoteList(text);
        Assert.Equal(2, m.Count);
        Assert.Equal("dev.azure.com/contoso/LIB", m["origin"]);
        Assert.Equal("github.com/halrad-com/otherapp", m["github"]);
    }
}
