using System;
using System.IO;
using System.Text;

namespace RaceTrade
{
    /// <summary>
    /// Crash-safe file writes.
    ///
    /// A plain File.WriteAllText truncates the target before writing, so a crash,
    /// power loss or exception mid-write leaves a truncated/corrupt config. These
    /// helpers write to a temp file in the same directory, flush it to disk, then
    /// atomically swap it into place (keeping a .bak of the previous content).
    /// </summary>
    public static class AtomicFile
    {
        public static void WriteAllText(string path, string contents)
        {
            WriteAllText(path, contents, new UTF8Encoding(false));
        }

        public static void WriteAllText(string path, string contents, Encoding encoding)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be null or empty.", nameof(path));

            contents = contents ?? string.Empty;

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Unique temp name: a fixed "path.tmp" makes two concurrent saves of the
            // same file collide (FileShare.None → IOException for the second writer).
            string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                // 1) Write the full payload to a temp file and force it to disk.
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(fs, encoding))
                {
                    writer.Write(contents);
                    writer.Flush();
                    fs.Flush(true); // flush OS buffers to the physical disk
                }

                // 2) Swap it into place.
                if (File.Exists(path))
                {
                    string backupPath = path + ".bak";

                    try
                    {
                        // File.Replace is atomic and also refreshes the backup.
                        File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
                        return;
                    }
                    catch (Exception)
                    {
                        // File.Replace can fail across volumes or on some filesystems.
                        // Fall through to a best-effort replace below.
                    }

                    try
                    {
                        if (File.Exists(backupPath))
                            File.Delete(backupPath);

                        File.Move(path, backupPath);
                    }
                    catch (Exception)
                    {
                        // If we can't back up the original, still try to put the new file in.
                    }
                }

                // No existing file (or the swap above failed) - move the temp into place.
                if (File.Exists(path))
                    File.Delete(path);

                File.Move(tempPath, path);
            }
            finally
            {
                // Never leave orphaned temp files behind on failure.
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch { /* best effort */ }
            }
        }
    }
}
