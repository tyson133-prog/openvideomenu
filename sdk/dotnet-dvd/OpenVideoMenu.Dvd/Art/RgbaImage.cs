using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace OpenVideoMenu.Dvd.Art;

/// <summary>One pixel: red, green, blue and alpha, 0-255 each.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A);

/// <summary>
/// A small RGBA picture (a button's highlight), with a PNG writer of its own so the package needs no imaging
/// library. Pixels start transparent.
/// </summary>
public sealed class RgbaImage : IDisposable
{
    private readonly Rgba[] _pixels;

    public RgbaImage(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "An image needs at least one pixel.");
        Width = width;
        Height = height;
        _pixels = new Rgba[width * height];
    }

    public int Width { get; }
    public int Height { get; }

    public Rgba this[int x, int y]
    {
        get => _pixels[y * Width + x];
        set => _pixels[y * Width + x] = value;
    }

    /// <summary>The picture as a PNG: 8-bit RGBA, no interlacing, each row unfiltered.</summary>
    public byte[] ToPng()
    {
        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }); // the PNG signature

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), Height);
        header[8] = 8; // bits per channel
        header[9] = 6; // RGBA
        Chunk(png, "IHDR", header);

        using (var data = new MemoryStream())
        {
            using (var z = new ZLibStream(data, CompressionLevel.Optimal, leaveOpen: true))
            {
                var row = new byte[1 + Width * 4];
                for (var y = 0; y < Height; y++)
                {
                    row[0] = 0; // filter: none
                    for (var x = 0; x < Width; x++)
                    {
                        var p = this[x, y];
                        var i = 1 + x * 4;
                        (row[i], row[i + 1], row[i + 2], row[i + 3]) = (p.R, p.G, p.B, p.A);
                    }
                    z.Write(row);
                }
            }
            Chunk(png, "IDAT", data.ToArray());
        }
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    public void SavePng(string path)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, ToPng());
    }

    public void Dispose() { }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        s.Write(number);
        var name = Encoding.ASCII.GetBytes(type);
        s.Write(name);
        s.Write(data);
        var crc = Crc32(Crc32(0xFFFFFFFFu, name), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        s.Write(number);
    }

    private static readonly uint[] CrcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
