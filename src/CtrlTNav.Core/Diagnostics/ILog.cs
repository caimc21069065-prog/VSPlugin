using System;
using System.IO;
using System.Text;

namespace CtrlTNav.Core.Diagnostics
{
    /// <summary>Minimal logging abstraction so engines do not depend on Visual Studio.</summary>
    public interface ILog
    {
        void Info(string message);

        void Warn(string message);

        void Error(string message, Exception? exception = null);
    }

    public sealed class NullLog : ILog
    {
        public static readonly NullLog Instance = new NullLog();

        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
        }

        public void Error(string message, Exception? exception = null)
        {
        }
    }

    /// <summary>Simple thread-safe file logger used when no VS logging service is available.</summary>
    public sealed class FileLog : ILog, IDisposable
    {
        private readonly object _lock = new object();
        private readonly string _path;
        private StreamWriter? _writer;
        private bool _disposed;

        public FileLog(string? path = null)
        {
            _path = path ?? AppPaths.GetLogFilePath();
        }

        public static FileLog? TryCreate()
        {
            try
            {
                return new FileLog();
            }
            catch
            {
                return null;
            }
        }

        public void Info(string message)
        {
            Write("INFO", message, null);
        }

        public void Warn(string message)
        {
            Write("WARN", message, null);
        }

        public void Error(string message, Exception? exception = null)
        {
            Write("ERROR", message, exception);
        }

        private void Write(string level, string message, Exception? exception)
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                try
                {
                    if (_writer == null)
                    {
                        string dir = Path.GetDirectoryName(_path) ?? string.Empty;
                        if (dir.Length > 0)
                        {
                            Directory.CreateDirectory(dir);
                        }

                        _writer = new StreamWriter(_path, true, Encoding.UTF8);
                    }

                    var sb = new StringBuilder();
                    sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                    sb.Append(" [").Append(level).Append("] ");
                    sb.Append(message);
                    if (exception != null)
                    {
                        sb.AppendLine();
                        sb.Append(exception);
                    }

                    _writer.WriteLine(sb.ToString());
                    _writer.Flush();
                }
                catch
                {
                    // Logging must never break the extension.
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                if (_writer != null)
                {
                    try
                    {
                        _writer.Dispose();
                    }
                    catch
                    {
                        // ignore
                    }

                    _writer = null;
                }
            }
        }
    }
}
