using System;
using System.IO;

namespace OpenVideoMenu.Dvd.IO
{
    public static class DvdFileSystemFactory
    {
        /// <summary>
        /// Opens an ISO file, a VIDEO_TS folder, or a folder/drive that contains VIDEO_TS.
        /// </summary>
        public static IDvdFileSystem Open(string path)
        {
            if (File.Exists(path))
                return new IsoDvdFileSystem(path);

            if (Directory.Exists(path))
            {
                var dir = new DirectoryInfo(path);
                if (string.Equals(dir.Name, "VIDEO_TS", StringComparison.OrdinalIgnoreCase) && dir.Parent is not null)
                    return new PhysicalDvdFileSystem(dir.Parent.FullName);
                return new PhysicalDvdFileSystem(dir.FullName);
            }

            throw new FileNotFoundException("DVD source not found.", path);
        }
    }
}
