using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Soul Echo element slots (itemInfos 1021+): fixed marks, rolled mark 0 and the slots a level reaches.</summary>
public sealed class EmblemMarkTests
{
    private static readonly (int Level, long Mark)[] Initial = [(5, 1001), (10, 0), (15, 0), (20, 0)];
    private static readonly long[] Pool = [1001, 1002, 1003, 1004];

    [Fact]
    public void ReachedMarkZeroRollsAnElementAndUnreachedSlotsStayUnlit()
    {
        var attrs = new Dictionary<int, long>();
        Assert.True(EmblemService.LightMarks(attrs, Initial, 12, Pool, n => n - 1));
        Assert.Equal(1001, attrs[1021]);
        Assert.Equal(1004, attrs[1022]);
        Assert.False(attrs.ContainsKey(1023));
    }

    [Fact]
    public void LitSlotsKeepTheirElementOnLaterLevels()
    {
        var attrs = new Dictionary<int, long>();
        EmblemService.LightMarks(attrs, Initial, 10, Pool, _ => 1);
        Assert.True(EmblemService.LightMarks(attrs, Initial, 20, Pool, _ => 3));
        Assert.Equal([1001, 1002, 1004, 1004], [attrs[1021], attrs[1022], attrs[1023], attrs[1024]]);
        Assert.False(EmblemService.LightMarks(attrs, Initial, 20, Pool, _ => 0));
    }

    [Fact]
    public void EmptySlotsOfAnEarlierLevelUpAreRepaired()
    {
        var attrs = new Dictionary<int, long> { [1021] = 1001, [1022] = 0, [1023] = 0, [1024] = 0 };
        Assert.True(EmblemService.LightMarks(attrs, Initial, 20, Pool, _ => 2));
        Assert.Equal([1001, 1003, 1003, 1003], [attrs[1021], attrs[1022], attrs[1023], attrs[1024]]);
    }

    [Fact]
    public void AnEmptyPoolLeavesTheEmptyElement()
    {
        var attrs = new Dictionary<int, long>();
        EmblemService.LightMarks(attrs, Initial, 10, [], _ => throw new InvalidOperationException());
        Assert.Equal(0, attrs[1022]);
    }
}
