using Huddle;
namespace Huddle.Tests;

public class FindMapTests
{
    [Fact]
    public void Add_ReturnsContiguousDisplayNumbersAcrossKinds()
    {
        var map = new FindMap();
        Assert.Equal(1, map.Add(FindMap.Kind.Doc, 0));
        Assert.Equal(2, map.Add(FindMap.Kind.Session, 0));
        Assert.Equal(3, map.Add(FindMap.Kind.Doc, 1));
        Assert.Equal(3, map.Count);
    }

    [Fact]
    public void Resolve_TranslatesToBackingListAndIndex()
    {
        var map = new FindMap();
        map.Add(FindMap.Kind.Doc, 0);
        map.Add(FindMap.Kind.Session, 0);

        var slot = map.Resolve(2);
        Assert.NotNull(slot);
        Assert.Equal(FindMap.Kind.Session, slot!.Value.kind);
        Assert.Equal(0, slot.Value.index);
    }

    [Fact]
    public void Resolve_OutOfRangeReturnsNull()
    {
        var map = new FindMap();
        map.Add(FindMap.Kind.Doc, 0);
        Assert.Null(map.Resolve(0));
        Assert.Null(map.Resolve(2));
    }
}
