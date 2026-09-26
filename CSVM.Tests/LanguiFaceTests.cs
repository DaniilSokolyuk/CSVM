using CSVM.Mech3;
using CSVM.UI.Boards;
using Xunit;

namespace CSVM.Tests;

/// <summary>A langui <c>[FONTID]</c> tag read as a typeface: family letters, point size, style
/// suffix (docs/formats/strings.md, "Font prefix").</summary>
public class LanguiFaceTests
{
    [Theory]
    [InlineData("[IMP36]", "Impact", 36, false, false)]
    [InlineData("BEL14B", "Bell MT", 14, true, false)]
    [InlineData("CSB9I", "Century Schoolbook", 9, false, true)]
    [InlineData("[VIN14]", "Viner Hand ITC", 14, false, false)]
    [InlineData("AB14I", "Book Antiqua", 14, true, true)]
    public void ATagNamesItsFamilySizeAndStyle(string tag, string family, int points, bool bold, bool italic)
    {
        var face = LanguiFace.Parse(tag);

        Assert.NotNull(face);
        Assert.Equal(family, face.Value.Family);
        Assert.Equal(points, face.Value.Points);
        Assert.Equal(bold, face.Value.Bold);
        Assert.Equal(italic, face.Value.Italic);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[XYZ12]")]
    [InlineData("[IMP]")]
    [InlineData("[TNR14Q]")]
    public void AnUnreadableTagIsNoFace(string? tag) => Assert.Null(LanguiFace.Parse(tag));

    /// <summary>Points at 96 dpi: a 12-point face is 16 board pixels.</summary>
    [Fact]
    public void PointsBecomeBoardPixelsAt96Dpi() => Assert.Equal(16f, LanguiFace.Parse("COUR12")!.Value.Pixels);

    /// <summary>The tag reaches the table either as the row's own font field or still leading the
    /// text of a multi-line row; both are read, and a row with neither has no face.</summary>
    [Fact]
    public void TheStringTableKeepsEachRowsTag()
    {
        var strings = UiStrings.Parse(
            "[{\"id\":1,\"text\":\"Headline\",\"font\":\"[IMP36]\",\"dll\":\"langui\"}," +
            "{\"id\":2,\"text\":\"[TNR14]\\nBody\",\"dll\":\"langui\"}," +
            "{\"id\":3,\"text\":\"Plain\",\"dll\":\"langui\"}]");

        Assert.Equal("IMP36", strings.Face(1)?.Trim('[', ']'));
        Assert.Equal("TNR14", strings.Face(2)?.Trim('[', ']'));
        Assert.Equal("\nBody", strings.Text(2));
        Assert.Null(strings.Face(3));
    }
}
