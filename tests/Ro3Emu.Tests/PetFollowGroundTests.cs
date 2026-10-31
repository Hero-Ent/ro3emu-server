using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Ro3Emu.Server.Net;

namespace Ro3Emu.Tests;

/// <summary>The deployed pet's follow spot stays on the map's walkable ground (PetFollowerActor.Place).</summary>
public sealed class PetFollowGroundTests
{
    private const int Size = 12, BlockSize = 30;

    /// <summary>A synthetic *_dynamicmapdata grid: one block, each cell (walkable, height in m) from <paramref name="cell"/>.</summary>
    private static NavGrid Grid(Func<int, int, (bool Walkable, float Height)> cell)
    {
        const int header = 48, table = 16;
        var data = new byte[header + table + BlockSize * BlockSize * 4];
        BitConverter.GetBytes(Size).CopyTo(data, 20);
        BitConverter.GetBytes(Size).CopyTo(data, 24);
        BitConverter.GetBytes(BlockSize).CopyTo(data, 28);
        BitConverter.GetBytes(header + table).CopyTo(data, header);      // the block's cells
        BitConverter.GetBytes(header + table).CopyTo(data, header + 8);  // no extra layers
        for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++)
            {
                var (walkable, height) = cell(x, z);
                int at = header + table + (z * BlockSize + x) * 4;
                data[at] = walkable ? (byte)0x80 : (byte)0;
                BitConverter.GetBytes((short)MathF.Round(height * 100)).CopyTo(data, at + 2);
            }
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, data);
            return NavGrid.Load(path);
        }
        finally { File.Delete(path); }
    }

    private static GameSession Owner(float x, float y, float z)
    {
        var channel = new PhonestChannel(new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp), NullLogger.Instance);
        return new GameSession(channel, null!)
        {
            Role = new Role(11, "acc", 1, "Owner", 1, 1000, 1, 0, 0, 0, 10111, x, y, z, 0),
            SceneEntry = 10111,
            Position = new Vec3 { X = x, Y = y, Z = z },
            PreviousPosition = new Vec3 { X = x, Y = y, Z = z },
        };
    }

    private static PetFollowerActor Pet(GameSession owner, NavGrid? grid) =>
        new(1L << 52, owner.Role!.Id, 10001, 2300440, 1, owner.Position.Clone(), owner.Face, 700, 1, grid == null ? null : _ => grid);

    [Fact]
    public void FollowSpotTakesTheGroundHeightNotTheOwners()
    {
        var owner = Owner(5.5f, 0f, 5.5f);
        var pet = Pet(owner, Grid((x, z) => (true, x * 0.25f)));  // a slope rising along x
        pet.AtOwner(owner);
        float expected = MathF.Floor(pet.Position.X) * 0.25f;
        Assert.NotEqual(owner.Position.X, pet.Position.X);
        Assert.Equal(expected, pet.Position.Y, 2);
    }

    [Fact]
    public void FollowSpotBehindAWallMovesToAWalkableSide()
    {
        var owner = Owner(5.5f, 0f, 5.5f);
        var grid = Grid((x, z) => (x < 6, 0f));                  // everything from x = 6 on is wall
        var open = Pet(owner, null);
        open.AtOwner(owner);
        Assert.True(open.Position.X >= 6f);                       // the plain offset lands in the wall

        var pet = Pet(owner, grid);
        pet.AtOwner(owner);
        Assert.True(grid.IsWalkable(pet.Position.X, pet.Position.Y, pet.Position.Z));
        Assert.True(pet.Position.X != owner.Position.X || pet.Position.Z != owner.Position.Z);
    }

    [Fact]
    public void WithoutANavGridTheOffsetKeepsTheOwnersHeight()
    {
        var owner = Owner(5.5f, 3f, 5.5f);
        var pet = Pet(owner, null);
        pet.AtOwner(owner);
        Assert.Equal(3f, pet.Position.Y);
    }
}
