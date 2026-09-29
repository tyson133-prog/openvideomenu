using System.Buffers.Binary;
using System.Text;

namespace OpenVideoMenu;

/// <summary>One file inside a bundle. Its bytes are one run in the bundle file (entries are stored).</summary>
public sealed class BundleMember
{
    public required string Name { get; init; }

    /// <summary>0 = stored, the only method bundles may use.</summary>
    public required int Method { get; init; }

    public required long Size { get; init; }

    /// <summary>Where the entry's local header starts.</summary>
    public required long HeaderOffset { get; init; }

    public bool Encrypted { get; init; }

    /// <summary>Where the bytes start, once looked up (see <see cref="Bundle.DataOffset"/>).</summary>
    public long? DataOffset { get; internal set; }
}

/// <summary>What's in a bundle, and the problems that make it unusable.</summary>
/// <param name="Members">By name, e.g. "manifest.json", "menus/main.jpg".</param>
/// <param name="Errors">Compressed or encrypted entries, unsafe names. Empty for a good bundle.</param>
public sealed record BundleContents(IReadOnlyDictionary<string, BundleMember> Members, IReadOnlyList<string> Errors);

/// <summary>The result of <see cref="Bundle.Pack(string, string)"/>.</summary>
/// <param name="Written">Files in the bundle, manifest.json included.</param>
/// <param name="Problems">Assets not found, or outside the manifest's folder, so not bundled.</param>
public sealed record PackResult(int Written, IReadOnlyList<string> Problems);

/// <summary>
/// Bundles (spec §2.1): <c>&lt;Title&gt;.ovmz</c> is a ZIP holding <c>manifest.json</c> at its root and
/// the menu assets at their manifest paths, every entry stored (no compression, no encryption), so
/// each member is one run of bytes a server can send, and range, straight from the file. Media
/// refs resolve against the folder that holds the bundle. Mirrors packages/asset-server/src/bundle.ts.
/// </summary>
public static class Bundle
{
    private const uint Eocd = 0x06054b50;
    private const uint Eocd64 = 0x06064b50;
    private const uint Eocd64Locator = 0x07064b50;
    private const uint Central = 0x02014b50;
    private const uint Local = 0x04034b50;

    // --- reading ----------------------------------------------------------------------------------

    /// <summary>A bundle's members and its problems. Throws <see cref="InvalidDataException"/> when it isn't a ZIP.</summary>
    public static BundleContents Read(string file)
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
                    // ZIP64: only the fields whose 32-bit value is 0xffffffff are here, in this order.
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

            if (!IsSafeName(name))
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

        return new BundleContents(members, errors);
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

    /// <summary>A read-only, seekable stream over one member: what a server sends, with range support.</summary>
    public static Stream OpenMember(string file, BundleMember member)
    {
        var start = DataOffset(file, member);
        return new SliceStream(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true), start, member.Size);
    }

    /// <summary>
    /// Check a bundle: stored entries with safe names, a valid <c>manifest.json</c> at its root, and
    /// every menu asset it names present (missing ones are warnings, path "/bundle/&lt;asset&gt;").
    /// On success the result carries the manifest.
    /// </summary>
    public static ValidationResult Validate(string file)
    {
        BundleContents contents;
        try
        {
            contents = Read(file);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or EndOfStreamException)
        {
            return ValidationResult.Failed(new Issue("", e.Message));
        }

        if (contents.Errors.Count > 0)
        {
            return ValidationResult.Failed(contents.Errors.Select(e => new Issue("", e)).ToArray());
        }

        if (!contents.Members.TryGetValue(Format.BundleManifestName, out var manifestMember))
        {
            return ValidationResult.Failed(new Issue("", $"No {Format.BundleManifestName} at the bundle's root."));
        }

        var result = Manifest.Parse(Encoding.UTF8.GetString(ReadMember(file, manifestMember)));
        if (!result.Ok)
        {
            return result;
        }

        var warnings = result.Warnings.ToList();
        foreach (var asset in result.Manifest!.MenuAssets())
        {
            if (!contents.Members.ContainsKey(NormalizePosix(asset) ?? asset))
            {
                warnings.Add(new Issue($"/bundle/{asset}", $"Menu asset not in the bundle: {asset}"));
            }
        }

        return new ValidationResult(result.Errors, warnings, result.Manifest);
    }

    // --- writing ----------------------------------------------------------------------------------

    /// <summary>
    /// Pack a loose manifest file and the local assets it names into a bundle. The manifest must be
    /// valid. Assets missing, or outside the manifest's folder ("../"), are reported, not packed.
    /// </summary>
    public static PackResult Pack(string manifestFile, string outFile)
    {
        var text = File.ReadAllText(manifestFile);
        var parsed = Manifest.Parse(text);
        if (!parsed.Ok)
        {
            throw new InvalidDataException($"The manifest has errors: {string.Join("; ", parsed.Errors)}");
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(manifestFile))!;
        return PackFiles(Encoding.UTF8.GetBytes(text), parsed.Manifest!, name => OpenIfExists(Path.Combine(dir, name.Replace('/', Path.DirectorySeparatorChar))), outFile);
    }

    /// <summary>
    /// Pack a manifest built in code, with its assets read from <paramref name="assetDirectory"/> at
    /// their manifest paths. The manifest must be valid.
    /// </summary>
    public static PackResult Pack(Manifest manifest, string assetDirectory, string outFile) =>
        Pack(manifest, name => OpenIfExists(Path.Combine(assetDirectory, name.Replace('/', Path.DirectorySeparatorChar))), outFile);

    /// <summary>
    /// Pack a manifest built in code, with each asset's bytes from <paramref name="openAsset"/>
    /// (given the normalized path; return null when there's no such asset). For tools that make
    /// their art in memory. The manifest must be valid.
    /// </summary>
    public static PackResult Pack(Manifest manifest, Func<string, Stream?> openAsset, string outFile)
    {
        var checkedResult = manifest.Validate();
        if (!checkedResult.Ok)
        {
            throw new InvalidDataException($"The manifest has errors: {string.Join("; ", checkedResult.Errors)}");
        }

        return PackFiles(Encoding.UTF8.GetBytes(manifest.ToJson() + "\n"), manifest, openAsset, outFile);
    }

    /// <summary>
    /// Unpack a bundle into <paramref name="outDir"/> as <paramref name="manifestName"/> (default
    /// <c>&lt;Title&gt;.openvideomenu.json</c>, after the bundle's name) plus its assets. Returns the files written.
    /// </summary>
    public static int Unpack(string bundleFile, string outDir, string? manifestName = null)
    {
        var contents = Read(bundleFile);
        if (contents.Errors.Count > 0)
        {
            throw new InvalidDataException(string.Join("; ", contents.Errors));
        }

        manifestName ??= Path.GetFileName(bundleFile)[..^Format.BundleExtension.Length] + Format.ManifestSuffix;
        var n = 0;
        foreach (var member in contents.Members.Values)
        {
            var target = member.Name == Format.BundleManifestName
                ? Path.Combine(outDir, manifestName)
                : Path.Combine([outDir, .. member.Name.Split('/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var input = OpenMember(bundleFile, member))
            using (var output = File.Create(target))
            {
                input.CopyTo(output);
            }

            n++;
        }

        return n;
    }

    private static PackResult PackFiles(byte[] manifestJson, Manifest manifest, Func<string, Stream?> openAsset, string outFile)
    {
        var problems = new List<string>();
        var written = 0;
        var temp = outFile + ".tmp";
        using (var output = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var zip = new StoredZipWriter(output);
            using (var m = new MemoryStream(manifestJson))
            {
                zip.Add(Format.BundleManifestName, m);
            }

            written++;
            foreach (var asset in manifest.MenuAssets())
            {
                var name = NormalizePosix(asset);
                if (name is null)
                {
                    problems.Add($"Outside the manifest's folder, so not bundled: {asset}");
                    continue;
                }

                using var data = openAsset(name);
                if (data is null)
                {
                    problems.Add($"Menu asset not found: {asset}");
                    continue;
                }

                zip.Add(name, data);
                written++;
            }

            zip.Finish();
        }

        File.Move(temp, outFile, overwrite: true);
        return new PackResult(written, problems);
    }

    private static Stream? OpenIfExists(string path) => File.Exists(path) ? File.OpenRead(path) : null;

    /// <summary>"menus/./a.jpg" → "menus/a.jpg"; null for paths that leave the folder or are absolute.</summary>
    internal static string? NormalizePosix(string path)
    {
        if (path.StartsWith('/') || path.Contains('\\'))
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            if (segment is "" or ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (parts.Count == 0)
                {
                    return null;
                }

                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(segment);
        }

        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    private static bool IsSafeName(string name) =>
        !name.Contains('\\') && !name.StartsWith('/') && !name.Split('/').Any(s => s == "..") && !(name.Length >= 2 && name[1] == ':');

    private static void ReadAt(Stream fs, byte[] buffer, long offset)
    {
        fs.Position = offset;
        fs.ReadExactly(buffer);
    }

    private static ushort U16(byte[] b, int i) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(i));

    private static uint U32(byte[] b, int i) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i));

    private static ulong U64(byte[] b, int i) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(i));

    /// <summary>
    /// Writes a ZIP with every entry stored, UTF-8 names, sizes and CRC in the local headers (no
    /// data descriptors, which some readers mishandle for stored entries). No ZIP64: under 4 GB.
    /// </summary>
    private sealed class StoredZipWriter(FileStream output)
    {
        private readonly MemoryStream _central = new();
        private readonly (ushort Time, ushort Date) _stamp = DosStamp(DateTime.Now);
        private int _count;

        public void Add(string name, Stream data)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var offset = output.Position;
            if (offset > uint.MaxValue)
            {
                throw new IOException("Bundles over 4 GB need ZIP64, which Pack doesn't write yet.");
            }

            // Local header now with size and CRC left blank, then the data, then fill them in.
            var local = new byte[30];
            BinaryPrimitives.WriteUInt32LittleEndian(local, Local);
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(4), 10);
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(6), 0x800);
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(8), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(10), _stamp.Time);
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(12), _stamp.Date);
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(26), (ushort)nameBytes.Length);
            output.Write(local);
            output.Write(nameBytes);

            var crc = new Crc32();
            long size = 0;
            var buffer = new byte[81920];
            int n;
            while ((n = data.Read(buffer, 0, buffer.Length)) > 0)
            {
                crc.Append(buffer.AsSpan(0, n));
                output.Write(buffer, 0, n);
                size += n;
            }

            if (size > uint.MaxValue || output.Position > uint.MaxValue)
            {
                throw new IOException("Bundles over 4 GB need ZIP64, which Pack doesn't write yet.");
            }

            var end = output.Position;
            var fields = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(fields, crc.Value);
            BinaryPrimitives.WriteUInt32LittleEndian(fields.AsSpan(4), (uint)size);
            BinaryPrimitives.WriteUInt32LittleEndian(fields.AsSpan(8), (uint)size);
            output.Position = offset + 14;
            output.Write(fields);
            output.Position = end;

            var entry = new byte[46];
            BinaryPrimitives.WriteUInt32LittleEndian(entry, Central);
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(4), 20);
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(6), 10);
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(8), 0x800);
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(10), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(12), _stamp.Time);
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(14), _stamp.Date);
            fields.CopyTo(entry.AsSpan(16));
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(28), (ushort)nameBytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(42), (uint)offset);
            _central.Write(entry);
            _central.Write(nameBytes);
            _count++;
        }

        public void Finish()
        {
            var cdOffset = output.Position;
            _central.Position = 0;
            _central.CopyTo(output);
            if (_count > ushort.MaxValue || output.Position > uint.MaxValue)
            {
                throw new IOException("Bundles over 4 GB or 65,535 files need ZIP64, which Pack doesn't write yet.");
            }

            var end = new byte[22];
            BinaryPrimitives.WriteUInt32LittleEndian(end, Eocd);
            BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(8), (ushort)_count);
            BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(10), (ushort)_count);
            BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(12), (uint)_central.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(16), (uint)cdOffset);
            output.Write(end);
        }

        private static (ushort Time, ushort Date) DosStamp(DateTime t) =>
            ((ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2)), (ushort)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day));
    }

    /// <summary>CRC-32 (the ZIP/IEEE polynomial), so the library needs no extra packages.</summary>
    private sealed class Crc32
    {
        private static readonly uint[] Table = Enumerable.Range(0, 256).Select(i =>
        {
            var c = (uint)i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            return c;
        }).ToArray();

        private uint _crc = 0xffffffff;

        public uint Value => ~_crc;

        public void Append(ReadOnlySpan<byte> data)
        {
            foreach (var b in data)
            {
                _crc = Table[(_crc ^ b) & 0xff] ^ (_crc >> 8);
            }
        }
    }
}

/// <summary>A read-only, seekable window onto part of a stream: a bundle member, served with range support.</summary>
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

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

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
