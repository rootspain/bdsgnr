using BlastDesigner.Domain.Geometry;
using BlastDesigner.Rendering;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>The view cube: which parts are seen, where they look from and the view each one gives.</summary>
public class ViewCubeTests
{
    [Theory]
    [InlineData(0, -1, 0, 0, 0)]      // the south face looks north
    [InlineData(0, 1, 0, 0, 180)]     // the north face looks south
    [InlineData(1, 0, 0, 0, -90)]     // the east face looks west
    [InlineData(-1, 0, 0, 0, 90)]     // the west face looks east
    [InlineData(0, 0, 1, 90, 0)]      // the top is the plan, north up
    [InlineData(0, -1, 1, 45, 0)]     // the edge of the top and the south
    [InlineData(1, -1, 0, 0, -45)]    // the south-east edge
    public void APartLooksFromItsSide(double x, double y, double z, double pitch, double yaw)
    {
        var (p, w) = ViewCube.ViewFrom(new Vec3(x, y, z));
        Assert.Equal(pitch, p, 6);
        Assert.Equal(yaw, w, 6);
    }

    [Fact]
    public void ACornerIsTheIsometricView()
    {
        var (p, w) = ViewCube.ViewFrom(new Vec3(1, -1, 1));
        Assert.Equal(Math.Atan(1 / Math.Sqrt(2)) / GeoMath.Deg, p, 6);
        Assert.Equal(-45, w, 6);
    }

    [Fact]
    public void TheViewFromAPartIsTheOneThePartShows()
    {
        var cube = ViewCube.CubeCamera(30, 84);
        foreach (var part in ViewCube.Parts(cube))
        {
            var (pitch, yaw) = ViewCube.ViewFrom(part.From);
            var view = new Camera { Pitch = pitch, Yaw = yaw };
            Assert.True(ViewCube.Shows(view, part), $"{part.Face} {part.From}");
        }
    }

    [Fact]
    public void TheCubeTurnsWithTheView()
    {
        // turned north-up, the top and the south are seen; turned round, the top and the north
        Assert.Equal(new[] { "S", "Top" }, ViewCube.VisibleFaces(ViewCube.CubeCamera(0, 84)).Select(f => f.Name));
        Assert.Equal(new[] { "N", "Top" }, ViewCube.VisibleFaces(ViewCube.CubeCamera(180, 84)).Select(f => f.Name));
        Assert.Equal(new[] { "S", "E", "Top" }, ViewCube.VisibleFaces(ViewCube.CubeCamera(-45, 84)).Select(f => f.Name));
    }

    [Fact]
    public void NoPartLooksFromBelow()
    {
        foreach (var yaw in new[] { 0.0, 45, 135, 200, 300 })
            Assert.All(ViewCube.Parts(ViewCube.CubeCamera(yaw, 84)), p => Assert.True(p.From.Z >= 0));
    }

    [Fact]
    public void TheMiddleOfTheTopIsThePlanAndItsCornerACorner()
    {
        var cube = ViewCube.CubeCamera(-45, 84);
        var (cx, cy) = cube.ToScreen(new Vec3(0, 0, 1));
        Assert.Equal(new Vec3(0, 0, 1), ViewCube.At(cube, cx, cy)!.From);
        // close to the top's south-east corner, inside the face
        var (kx, ky) = cube.ToScreen(new Vec3(0.9, -0.9, 1));
        var corner = ViewCube.At(cube, kx, ky)!;
        Assert.Equal("Top", corner.Face);
        Assert.Equal(1 / Math.Sqrt(3), corner.From.X, 6);
        Assert.Equal(-1 / Math.Sqrt(3), corner.From.Y, 6);
        Assert.Equal(1 / Math.Sqrt(3), corner.From.Z, 6);
    }
}
