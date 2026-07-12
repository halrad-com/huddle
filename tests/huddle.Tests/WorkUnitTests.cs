using Huddle;
namespace Huddle.Tests;

public class WorkUnitTests
{
    [Fact]
    public void WorkUnit_holds_its_fields()
    {
        var u = new WorkUnit("u1", "huddle", "backenddev", "do it", ["a.cs"], []);
        Assert.Equal("u1", u.Id);
        Assert.Empty(u.DependsOn);
    }
}
