using System;
using System.IO;

namespace PickandPlace2026.Classes
{
    // Diagnostic-log
    public static class FileLog
    {
        private static readonly string FilePath =
            Path.Combine(AppContext.BaseDirectory, "DataFiles", "build-debug.log");

        private static readonly object Lock = new object();

        public static void Write(string message)
        {
            try
            {
                lock (Lock)
                {
                    string? directory = Path.GetDirectoryName(FilePath);
                    if (directory != null)
                    {
                        Directory.CreateDirectory(directory);
                    }
                    File.AppendAllText(FilePath,
                        $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // Never let logging itself break the build.
            }
        }
    }
}
