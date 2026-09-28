using System.IO.Compression;
using System.Text;
using Xunit;

namespace Jellyfin.Plugin.OpenVideoMenu.Tests;

public sealed class BundleTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ovm-bundle-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Make(string name, CompressionLevel level, params (string Name, string Text)[] entries)
    {
        var file = Path.Combine(_dir, name);
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
        {
            foreach (var (entryName, text) in entries)
            {
                using var s = zip.CreateEntry(entryName, level).Open();
                s.Write(Encoding.UTF8.GetBytes(text));
            }
        }

        return file;
    }

    [Fact]
    public void ReadsStoredMembersAndTheirBytes()
    {
        var file = Make("a.ovmz", CompressionLevel.NoCompression, ("manifest.json", "{\"x\":1}"), ("menus/bg.jpg", "JPEGDATA"));
        var (members, errors) = Bundle.Read(file);
        Assert.Empty(errors);
        Assert.Equal(["manifest.json", "menus/bg.jpg"], members.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("{\"x\":1}", Encoding.UTF8.GetString(Bundle.ReadMember(file, members["manifest.json"])));

        // A member served as a slice of the bundle file, the way the files endpoint does.
        var bg = members["menus/bg.jpg"];
        using var slice = new SliceStream(File.OpenRead(file), Bundle.DataOffset(file, bg), bg.Size);
        slice.Seek(4, SeekOrigin.Begin);
        var rest = new byte[10];
        var n = slice.Read(rest, 0, rest.Length);
        Assert.Equal("DATA", Encoding.ASCII.GetString(rest, 0, n));
    }

    [Fact]
    public void RefusesCompressedEntries()
    {
        var file = Make("b.ovmz", CompressionLevel.Optimal, ("manifest.json", new string('x', 500)));
        var (_, errors) = Bundle.Read(file);
        Assert.Contains(errors, e => e.StartsWith("Compressed entry", StringComparison.Ordinal));
    }
}
