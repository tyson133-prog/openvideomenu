using System.Buffers.Binary;
using System.Text;

namespace Jellyfin.Plugin.OpenVideoMenu;

/// <summary>One member of a bundle. Its bytes start at <see cref="DataOffset"/>, found from its local header.</summary>
public sealed class BundleMember
{
    public required string Name { get; init; }

    /// <summary>0 = stored, the only method bundles may use.</summary>
    public required int Method { get; init; }

    public required long Size { get; init; }

    public required long HeaderOffset { get; init; }

    public bool Encrypted { get; init; }

    public long? DataOffset { get; set; }
}

/// <summary>
/// Bundles (format spec §2.1): a ZIP holding manifest.json and its assets, every entry stored
/// uncompressed, so a member is one run of bytes that can be served (and ranged) straight from the
/// file. Mirrors packages/asset-server/src/bundle.ts. ZipArchive doesn't expose where a member's
/// bytes start, so this reads the central directory itself.
/// </summary>
public static class Bundle
{
    public const string Extension = ".ovmz";
    public const string ManifestName = "manifest.json";

    private const uint Eocd = 0x06054b50;
    private const uint Eocd64 = 0x06064b50;
    private const uint Eocd64Locator = 0x07064b50;
    private const uint Central = 0x02014b50;
    private const uint Local = 0x04034b50;

    public static bool IsBundle(string path) => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>The bundle's members, and problems that make it unusable (compressed or encrypted entries, unsafe names).</summary>
    public static (Dictionary<string, BundleMember> Members, List<string> Errors) Read(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var size = fs.Length;
        var tailSize = (int)Math.Min(size, 22 + 0xffff + 20);
        var tail = new byte[tailSize];
        ReadAt(fs, tail, size - tailSize);
        var eocd = -1;
        for (var i = tailSize - 22; i >= 0; i--)
        {
            if (U32(tail, i) == Eocd)
            {
                eocd = i;
                break;
            }
        }

        if (eocd < 0)
        {
            throw new InvalidDataException("Not a ZIP file (no end of central directory).");
        }

        long count = U16(tail, eocd + 10);
        long cdSize = U32(tail, eocd + 12);
        long cdOffset = U32(tail, eocd + 16);
        if (eocd >= 20 && U32(tail, eocd - 20) == Eocd64Locator)
        {
            var rec = new byte[56];
            ReadAt(fs, rec, (long)U64(tail, eocd - 20 + 8));
            if (U32(rec, 0) == Eocd64)
            {
                count = (long)U64(rec, 32);
                cdSize = (long)U64(rec, 40);
                cdOffset = (long)U64(rec, 48);
            }
        }

        var cd = new byte[cdSize];
        ReadAt(fs, cd, cdOffset);
        var members = new Dictionary<string, BundleMember>(StringComparer.Ordinal);
        var errors = new List<string>();
        var p = 0;
        for (var n = 0; n < count; n++)
        {
            if (U32(cd, p) != Central)
            {
                throw new InvalidDataException("Corrupt central directory.");
            }

            var flags = U16(cd, p + 8);
            var method = U16(cd, p + 10);
            long entrySize = U32(cd, p + 24);
            long headerOffset = U32(cd, p + 42);
            var nameLen = U16(cd, p + 28);
            var extraLen = U16(cd, p + 30);
            var commentLen = U16(cd, p + 32);
            var name = ((flags & 0x800) != 0 ? Encoding.UTF8 : Encoding.Latin1).GetString(cd, p + 46, nameLen);
            var e = p + 46 + nameLen;
            var extraEnd = e + extraLen;
            while (e + 4 <= extraEnd)
            {
                var id = U16(cd, e);
                var len = U16(cd, e + 2);
                if (id == 0x0001)
                {
                    var q = e + 4;
                    if (entrySize == 0xffffffff)
                    {
                        entrySize = (long)U64(cd, q);
                        q += 8;
                    }

                    if (U32(cd, p + 20) == 0xffffffff)
                    {
                        q += 8;
                    }

                    if (headerOffset == 0xffffffff)
                    {
                        headerOffset = (long)U64(cd, q);
                    }
                }

                e += 4 + len;
            }

            p = extraEnd + commentLen;
            if (name.EndsWith('/'))
            {
                continue;
            }

            if (name.Contains('\\') || name.StartsWith('/') || name.Split('/').Any(s => s == ".."))
            {
                errors.Add($"Entry name not allowed (absolute, backslashes or \"..\"): {name}");
                continue;
            }

            var member = new BundleMember { Name = name, Method = method, Size = entrySize, HeaderOffset = headerOffset, Encrypted = (flags & 1) == 1 };
            if (member.Encrypted)
            {
                errors.Add($"Encrypted entry: {name}");
            }
            else if (method != 0)
            {
                errors.Add($"Compressed entry (bundles store every entry uncompressed): {name}");
            }

            members[name] = member;
        }

        return (members, errors);
    }

    /// <summary>Where a stored member's bytes start in the bundle file.</summary>
    public static long DataOffset(string file, BundleMember member)
    {
        if (member.DataOffset is { } known)
        {
            return known;
        }

        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[30];
        ReadAt(fs, header, member.HeaderOffset);
        if (U32(header, 0) != Local)
        {
            throw new InvalidDataException($"Corrupt local header for {member.Name}");
        }

        member.DataOffset = member.HeaderOffset + 30 + U16(header, 26) + U16(header, 28);
        return member.DataOffset.Value;
    }

    /// <summary>A stored member's bytes (for small members such as manifest.json).</summary>
    public static byte[] ReadMember(string file, BundleMember member)
    {
        var start = DataOffset(file, member);
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var data = new byte[member.Size];
        ReadAt(fs, data, start);
        return data;
    }

    private static void ReadAt(FileStream fs, byte[] buffer, long offset)
    {
        fs.Position = offset;
        fs.ReadExactly(buffer);
    }

    private static ushort U16(byte[] b, int i) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(i));

    private static uint U32(byte[] b, int i) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i));

    private static ulong U64(byte[] b, int i) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(i));
}

/// <summary>A read-only, seekable window onto part of a file: a bundle member, served with range support.</summary>
public sealed class SliceStream(Stream inner, long start, long length) : Stream
{
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, length);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = (int)Math.Min(count, length - _position);
        if (n <= 0)
        {
            return 0;
        }

        inner.Position = start + _position;
        var read = inner.Read(buffer, offset, n);
        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = (int)Math.Min(buffer.Length, length - _position);
        if (n <= 0)
        {
            return 0;
        }

        inner.Position = start + _position;
        var read = await inner.ReadAsync(buffer[..n], cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => length + offset,
        };
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
