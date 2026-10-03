using System;
using System.Collections.Generic;
using System.IO;

namespace OpenVideoMenu.Dvd.IO
{
    /// <summary>
    /// Read-only view of a DVD-Video file system (ISO image, VIDEO_TS folder, or mounted disc).
    /// Paths are DVD-rooted, e.g. <c>\VIDEO_TS\VIDEO_TS.IFO</c>; separators may be / or \.
    /// </summary>
    public interface IDvdFileSystem : IDisposable
    {
        bool FileExists(string path);
        Stream OpenRead(string path);
        IEnumerable<string> GetFiles(string directory, string searchPattern);

        /// <summary>Disc volume label (e.g. "THE_MATRIX"), or null when unknown.</summary>
        string? VolumeLabel { get; }
    }
}
