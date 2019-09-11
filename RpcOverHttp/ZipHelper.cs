﻿using System.IO;
using System.IO.Compression;

namespace RpcOverHttp
{
    public class ZipHelper
    {
        public static void UnZip(string zipFile, string destination)
        {
            using (var zipfs = File.OpenRead(zipFile))
            {
                using (ZipArchive archive = new ZipArchive(zipfs))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        using (var stream = entry.Open())
                        {
                            var itemPath = Path.Combine(destination, entry.FullName);
                            var itemDir = Path.GetDirectoryName(itemPath);
                            if (!Directory.Exists(itemDir))
                                Directory.CreateDirectory(itemDir);
                            using (var fs = File.Create(itemPath))
                            {
                                stream.CopyTo(fs);
                            }
                        }
                    }
                }
            }
        }

        public static void UnZip(Stream zipFileStream, string destination)
        {
            using (ZipArchive archive = new ZipArchive(zipFileStream))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    using (var stream = entry.Open())
                    {
                        var itemPath = Path.Combine(destination, entry.FullName);
                        var itemDir = Path.GetDirectoryName(itemPath);
                        if (!Directory.Exists(itemDir))
                            Directory.CreateDirectory(itemDir);
                        if (!string.IsNullOrEmpty(entry.Name))
                        {
                            using (var fs = File.Create(itemPath))
                            {
                                stream.CopyTo(fs);
                            }
                        }
                    }
                }
            }
        }
    }
}
