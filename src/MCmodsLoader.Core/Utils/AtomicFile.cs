using System.Text;

namespace MCmodsLoader.Core.Utils;

/// <summary>
/// Replaces a file's content in one step: the text goes to a temp file in the same
/// directory, which is then swapped in. A crash or full disk mid-write leaves either
/// the old or the new content behind, never a truncated file.
/// </summary>
internal static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void WriteAllText(string path, string contents)
    {
        string fullPath = Path.GetFullPath(path);
        string dir = Path.GetDirectoryName(fullPath) ?? throw new IOException($"'{path}' has no parent directory.");
        Directory.CreateDirectory(dir);

        // Same directory, so the final rename never crosses a volume boundary.
        string tempPath = Path.Combine(dir, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(Utf8NoBom.GetBytes(contents));
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                try
                {
                    File.Replace(tempPath, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (IOException)
                {
                    // File.Replace refuses some file systems and network shares; a
                    // replacing rename is still atomic there.
                    File.Move(tempPath, fullPath, overwrite: true);
                }
            }
            else
            {
                File.Move(tempPath, fullPath);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // A stray temp file is harmless; the original is what matters.
            }
        }
    }
}
