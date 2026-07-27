using System.Drawing;
using System.Globalization;
using System.Security.Cryptography;
using System.Xml.Linq;
using GitKeyRouter.App.Forms;

namespace GitKeyRouter.App.Tests;

public sealed class ApplicationIconTests
{
    private const string ExpectedIconSha256 = "8830596D8D4A3E947A1E071A19FC63F8C1A8F39FF58B894E383D23A95A91906A";
    private const string EmbeddedResourceName = "GitKeyRouter.App.Assets.GitKeyRouter.ico";
    private static readonly int[] ExpectedSizes = [16, 24, 32, 48, 64, 128, 256];

    [Fact]
    public void ApplicationIcon_ContainsRequiredWindowsFramesAndStableHash()
    {
        var iconPath = FindIconPath();
        var bytes = File.ReadAllBytes(iconPath);

        Assert.Equal(ExpectedIconSha256, Convert.ToHexString(SHA256.HashData(bytes)));

        var entries = ReadDirectory(bytes);
        Assert.Equal(ExpectedSizes, entries.Select(entry => entry.Width).ToArray());
        Assert.All(entries, entry =>
        {
            Assert.Equal(entry.Width, entry.Height);
            Assert.Equal((ushort)1, entry.Planes);
            Assert.Equal((ushort)32, entry.BitsPerPixel);
            Assert.True(entry.Offset >= (uint)(6 + (entries.Count * 16)));
            Assert.True((long)entry.Offset + entry.ByteCount <= bytes.Length);
        });
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    public void ApplicationIcon_RemainsRecognizableAtSmallWindowsSizes(int size)
    {
        using var stream = File.OpenRead(FindIconPath());
        using var icon = new Icon(stream, new Size(size, size));
        using var bitmap = icon.ToBitmap();

        Assert.Equal(size, bitmap.Width);
        Assert.Equal(size, bitmap.Height);
        IconAssertions.AssertGitRouterMark(bitmap, requireTransparentCorners: true);
    }

    [Fact]
    public void EmbeddedWindowIcon_MatchesTheApplicationIconAsset()
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream(EmbeddedResourceName);

        Assert.NotNull(stream);
        Assert.Equal(ExpectedIconSha256, Convert.ToHexString(SHA256.HashData(stream)));
    }

    [Fact]
    public void ProjectConfiguration_UsesOneIconAssetForExeAndWindowResources()
    {
        var projectPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "GitKeyRouter.App",
            "GitKeyRouter.App.csproj");
        var document = XDocument.Load(projectPath);
        var ns = document.Root?.Name.Namespace ?? XNamespace.None;

        Assert.Equal(
            @"Assets\GitKeyRouter.ico",
            document.Descendants(ns + "ApplicationIconAsset").Single().Value);
        Assert.Equal(
            "$(ApplicationIconAsset)",
            document.Descendants(ns + "ApplicationIcon").Single().Value);

        var embeddedResource = document.Descendants(ns + "EmbeddedResource").Single(
            element => (string?)element.Attribute("LogicalName") == EmbeddedResourceName);
        Assert.Equal("$(ApplicationIconAsset)", (string?)embeddedResource.Attribute("Include"));
    }

    [Fact]
    public void SiteFavicon_UsesTheSameRoundedGitRouterVisualLanguage()
    {
        var faviconPath = Path.Combine(FindRepositoryRoot(), "site", "favicon.svg");
        var document = XDocument.Load(faviconPath);
        var ns = document.Root?.Name.Namespace ?? XNamespace.None;

        var background = document.Descendants(ns + "rect").Single(
            element => (string?)element.Attribute("data-role") == "background");
        Assert.True(double.Parse(
            (string?)background.Attribute("x") ?? "0",
            CultureInfo.InvariantCulture) > 0);
        Assert.True(double.Parse(
            (string?)background.Attribute("y") ?? "0",
            CultureInfo.InvariantCulture) > 0);
        Assert.InRange(
            double.Parse(
                (string?)background.Attribute("rx") ?? "0",
                CultureInfo.InvariantCulture),
            12,
            20);

        Assert.Equal(3, document.Descendants(ns + "circle").Count());
        Assert.Single(document.Descendants(), HasRole("git-fork"));
        Assert.Single(document.Descendants(), HasRole("route-arrow"));
        Assert.Single(document.Descendants(), HasRole("key-teeth"));
    }

    private static Predicate<XElement> HasRole(string role)
        => element => (string?)element.Attribute("data-role") == role;

    private static string FindIconPath()
        => Path.Combine(
            FindRepositoryRoot(),
            "src",
            "GitKeyRouter.App",
            "Assets",
            "GitKeyRouter.ico");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GitKeyRouter.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the GitKeyRouter repository root.");
    }

    private static IReadOnlyList<IconDirectoryEntry> ReadDirectory(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream);

        Assert.Equal((ushort)0, reader.ReadUInt16());
        Assert.Equal((ushort)1, reader.ReadUInt16());
        var count = reader.ReadUInt16();
        Assert.Equal(ExpectedSizes.Length, count);

        var entries = new List<IconDirectoryEntry>(count);
        for (var index = 0; index < count; index++)
        {
            var width = reader.ReadByte();
            var height = reader.ReadByte();
            _ = reader.ReadByte();
            _ = reader.ReadByte();
            var planes = reader.ReadUInt16();
            var bitsPerPixel = reader.ReadUInt16();
            var byteCount = reader.ReadUInt32();
            var offset = reader.ReadUInt32();

            entries.Add(new IconDirectoryEntry(
                width == 0 ? 256 : width,
                height == 0 ? 256 : height,
                planes,
                bitsPerPixel,
                byteCount,
                offset));
        }

        return entries;
    }

    private sealed record IconDirectoryEntry(
        int Width,
        int Height,
        ushort Planes,
        ushort BitsPerPixel,
        uint ByteCount,
        uint Offset);
}

internal static class IconAssertions
{
    public static void AssertGitRouterMark(Bitmap bitmap, bool requireTransparentCorners = false)
    {
        Assert.InRange(bitmap.Width, 16, 256);
        Assert.Equal(bitmap.Width, bitmap.Height);

        var totalPixels = bitmap.Width * bitmap.Height;
        var darkPixels = 0;
        var mintPixels = 0;
        var cyanPixels = 0;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                darkPixels += IsDark(pixel) ? 1 : 0;
                mintPixels += IsMint(pixel) ? 1 : 0;
                cyanPixels += IsCyan(pixel) ? 1 : 0;
            }
        }

        Assert.True(
            darkPixels >= totalPixels / 3,
            "The icon did not preserve its dark rounded-square field.");
        Assert.True(
            mintPixels >= Math.Max(4, totalPixels / 100),
            "The Git branch mark was not visible.");
        Assert.True(
            cyanPixels >= Math.Max(2, totalPixels / 160),
            "The router/key accent was not visible.");

        AssertColorNear(bitmap, 0.31, 0.23, IsMint, "top Git commit node");
        AssertColorNear(bitmap, 0.58, 0.33, IsMint, "fork commit node");
        AssertColorNear(bitmap, 0.80, 0.33, IsCyan, "route arrow");
        AssertColorNear(bitmap, 0.31, 0.77, IsMint, "lower key commit node");
        AssertColorNear(bitmap, 0.52, 0.77, IsCyan, "key teeth");

        var trunkRows = 0;
        for (var y = bitmap.Height / 5; y < (bitmap.Height * 4) / 5; y++)
        {
            for (var x = bitmap.Width / 5; x <= (bitmap.Width * 2) / 5; x++)
            {
                if (!IsMint(bitmap.GetPixel(x, y)))
                {
                    continue;
                }

                trunkRows++;
                break;
            }
        }

        Assert.True(
            trunkRows >= bitmap.Height / 3,
            "The vertical Git trunk was not continuous.");

        if (requireTransparentCorners)
        {
            Assert.All(
                new[]
                {
                    bitmap.GetPixel(0, 0),
                    bitmap.GetPixel(bitmap.Width - 1, 0),
                    bitmap.GetPixel(0, bitmap.Height - 1),
                    bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1)
                },
                color => Assert.True(color.A <= 16, "The icon corners were not transparent."));
        }
    }

    private static void AssertColorNear(
        Bitmap bitmap,
        double normalizedX,
        double normalizedY,
        Func<Color, bool> predicate,
        string feature)
    {
        var centerX = (int)Math.Round(normalizedX * (bitmap.Width - 1));
        var centerY = (int)Math.Round(normalizedY * (bitmap.Height - 1));
        var radius = Math.Max(1, bitmap.Width / 10);

        for (var y = Math.Max(0, centerY - radius);
             y <= Math.Min(bitmap.Height - 1, centerY + radius);
             y++)
        {
            for (var x = Math.Max(0, centerX - radius);
                 x <= Math.Min(bitmap.Width - 1, centerX + radius);
                 x++)
            {
                if (predicate(bitmap.GetPixel(x, y)))
                {
                    return;
                }
            }
        }

        Assert.Fail($"The {feature} was not visible at {bitmap.Width}x{bitmap.Height}.");
    }

    private static bool IsDark(Color color)
        => color.A > 16 && color.R < 55 && color.G < 85 && color.B < 105;

    private static bool IsMint(Color color)
        => color.A > 16
           && color.R < 170
           && color.G > 155
           && color.B > 95
           && color.G > color.R + 25;

    private static bool IsCyan(Color color)
        => color.A > 16
           && color.R > 75
           && color.G > 165
           && color.B > 175
           && color.B >= color.R;
}
