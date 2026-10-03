using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DiscUtils;
using DiscUtils.Iso9660;
using DiscUtils.Udf;

namespace OpenVideoMenu.Dvd.IO
{
    /// <summary>
    /// DVD file system backed by an ISO image: UDF, as players read it (a few discs' ISO 9660 directories
    /// point elsewhere), with ISO 9660/Joliet as the fallback when the UDF can't be read.
    /// </summary>
    public sealed class IsoDvdFileSystem : IDvdFileSystem
    {
        private readonly Stream _isoStream;
        private readonly DiscFileSystem _fs;

        public IsoDvdFileSystem(string isoPath)
        {
            var file = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, FileOptions.RandomAccess);
            // Some images are cut short of the size their UDF partition claims (the last, empty sectors
            // weren't written). DiscUtils refuses those, so the image reads as zeros past its end.
            _isoStream = new ZeroPaddedStream(file);

            DiscFileSystem? fs = null;
            if (UdfReader.Detect(_isoStream))
            {
                try { fs = new UdfReader(_isoStream); }
                catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or InvalidOperationException) { fs = null; }
            }
            _fs = fs ?? new CDReader(_isoStream, joliet: true);

            try { VolumeLabel = string.IsNullOrWhiteSpace(_fs.VolumeLabel) ? null : _fs.VolumeLabel.Trim(); }
            catch { VolumeLabel = null; }
        }

        public string? VolumeLabel { get; }

        public bool FileExists(string path) => _fs.FileExists(NormalizeIsoPath(path));

        public Stream OpenRead(string path) =>
            _fs.OpenFile(NormalizeIsoPath(path), FileMode.Open, FileAccess.Read);

        public IEnumerable<string> GetFiles(string directory, string searchPattern)
        {
            var isoDir = NormalizeIsoPath(directory);
            if (!_fs.DirectoryExists(isoDir))
                return Enumerable.Empty<string>();
            return _fs.GetFiles(isoDir, searchPattern);
        }

        private const char Sep = (char)92; // backslash

        private static string NormalizeIsoPath(string path)
        {
            var p = path.Replace('/', Sep);
            if (p.Length == 0 || p[0] != Sep)
                p = Sep + p;
            return p;
        }

        public void Dispose()
        {
            _fs.Dispose();
            _isoStream.Dispose();
        }
    }

    /// <summary>
    /// A read-only image that claims to be as large as the biggest DVD and reads zeros past the real end,
    /// so a file system whose descriptors reach beyond a truncated image still opens.
    /// </summary>
    internal sealed class ZeroPaddedStream : Stream
    {
        /// <summary>A dual-layer DVD-9 is 8.5 GB; leave room for over-claimed partitions.</summary>
        private const long VirtualLength = 16L * 1024 * 1024 * 1024;

        private readonly Stream _inner;
        private long _position;

        public ZeroPaddedStream(Stream inner) => _inner = inner;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => Math.Max(VirtualLength, _inner.Length);

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= Length || buffer.Length == 0) return 0;
            var want = (int)Math.Min(buffer.Length, Length - _position);
            var real = _inner.Length;
            var got = 0;
            if (_position < real)
            {
                _inner.Position = _position;
                var fromFile = (int)Math.Min(want, real - _position);
                while (got < fromFile)
                {
                    var n = _inner.Read(buffer.Slice(got, fromFile - got));
                    if (n == 0) break;
                    got += n;
                }
            }
            buffer.Slice(got, want - got).Clear();
            _position += want;
            return want;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => Length + offset,
            };
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
