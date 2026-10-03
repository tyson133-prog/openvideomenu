using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenVideoMenu.Dvd.IO
{
    /// <summary>
    /// DVD file system backed by a directory that contains VIDEO_TS (a mounted disc or a ripped folder).
    /// File lookups are case-insensitive so this also works on case-sensitive file systems.
    /// </summary>
    public sealed class PhysicalDvdFileSystem : IDvdFileSystem
    {
        private const char Sep = (char)92; // backslash: DVD paths always use it
        private readonly string _root;

        public PhysicalDvdFileSystem(string root)
        {
            _root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            try
            {
                var full = Path.GetFullPath(_root);
                var drive = Path.GetPathRoot(full);
                var isDriveRoot = drive is not null &&
                                  string.Equals(full.TrimEnd(Sep, '/'), drive.TrimEnd(Sep, '/'),
                                      StringComparison.OrdinalIgnoreCase);
                VolumeLabel = isDriveRoot && drive is not null
                    ? new DriveInfo(drive).VolumeLabel
                    : new DirectoryInfo(_root).Name;
                if (string.IsNullOrWhiteSpace(VolumeLabel)) VolumeLabel = null;
            }
            catch
            {
                VolumeLabel = null;
            }
        }

        public string? VolumeLabel { get; }

        public bool FileExists(string path) => Resolve(path) is { } p && File.Exists(p);

        public Stream OpenRead(string path)
        {
            var physical = Resolve(path) ?? throw new FileNotFoundException("DVD file not found.", path);
            return new FileStream(physical, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, FileOptions.RandomAccess);
        }

        public IEnumerable<string> GetFiles(string directory, string searchPattern)
        {
            var physicalDir = Resolve(directory);
            if (physicalDir is null || !Directory.Exists(physicalDir))
                return Enumerable.Empty<string>();

            var dvdDir = Sep + directory.Replace('/', Sep).Trim(Sep) + Sep;
            return Directory
                .GetFiles(physicalDir, searchPattern, SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(f => f != null)
                .Select(f => dvdDir + f!);
        }

        /// <summary>Maps a DVD path to a physical path, matching each segment case-insensitively.</summary>
        private string? Resolve(string dvdPath)
        {
            var segments = dvdPath.Replace('/', Sep).Split(Sep, StringSplitOptions.RemoveEmptyEntries);
            var current = _root;
            foreach (var seg in segments)
            {
                var direct = Path.Combine(current, seg);
                if (File.Exists(direct) || Directory.Exists(direct)) { current = direct; continue; }
                if (!Directory.Exists(current)) return null;
                var match = Directory.EnumerateFileSystemEntries(current)
                    .FirstOrDefault(e => string.Equals(Path.GetFileName(e), seg, StringComparison.OrdinalIgnoreCase));
                if (match is null) return direct;
                current = match;
            }
            return current;
        }

        public void Dispose()
        {
        }
    }
}
