using System;
using System.IO;

namespace CtrlTNav.Tests.Indexing
{
    /// <summary>Per-test temporary directory; deleted on dispose.</summary>
    internal sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "CtrlTNavTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Combine(string relativePath)
        {
            return System.IO.Path.Combine(Path, relativePath.Replace('\\', System.IO.Path.DirectorySeparatorChar));
        }

        public string Write(string relativePath, string content)
        {
            string fullPath = Combine(relativePath);
            string? directory = System.IO.Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory!);
            }

            File.WriteAllText(fullPath, content);
            return fullPath;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, true);
                }
            }
            catch
            {
                // Best effort test cleanup.
            }
        }
    }
}
