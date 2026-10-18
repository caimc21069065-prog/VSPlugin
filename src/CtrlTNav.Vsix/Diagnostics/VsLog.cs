using System;
using CtrlTNav.Core;
using CtrlTNav.Core.Diagnostics;
using Microsoft.VisualStudio.Shell;

namespace CtrlTNav.Vsix.Diagnostics
{
    /// <summary>
    /// <see cref="ILog"/> implementation used by the Visual Studio integration.
    /// All messages go to <c>%LOCALAPPDATA%\CtrlTNav\ctrltnav.log</c>; errors and warnings are also
    /// mirrored into the Visual Studio activity log so that a support engineer can find them with
    /// <c>devenv.exe /log</c>.
    /// </summary>
    internal sealed class VsLog : ILog, IDisposable
    {
        private readonly FileLog? _fileLog;
        private readonly object _errorLock = new object();
        private DateTime _lastActivityLogWriteUtc = DateTime.MinValue;
        private bool _disposed;

        public VsLog(FileLog? fileLog = null)
        {
            _fileLog = fileLog ?? FileLog.TryCreate();
        }

        /// <summary>Path of the log file, or null when the file logger could not be created.</summary>
        public string? LogFilePath
        {
            get
            {
                try
                {
                    return AppPaths.GetLogFilePath();
                }
                catch
                {
                    return null;
                }
            }
        }

        public void Info(string message)
        {
            try
            {
                _fileLog?.Info(message);
            }
            catch
            {
                // Logging must never take the extension down.
            }
        }

        public void Warn(string message)
        {
            try
            {
                _fileLog?.Warn(message);
            }
            catch
            {
                // ignore
            }

            WriteActivityLog(false, message);
        }

        public void Error(string message, Exception? exception = null)
        {
            try
            {
                _fileLog?.Error(message, exception);
            }
            catch
            {
                // ignore
            }

            WriteActivityLog(true, exception == null ? message : message + Environment.NewLine + exception);
        }

        private void WriteActivityLog(bool isError, string message)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                // de-dup bursts: the same failure is frequently reported from several call sites
                lock (_errorLock)
                {
                    DateTime now = DateTime.UtcNow;
                    if ((now - _lastActivityLogWriteUtc).TotalMilliseconds < 50)
                    {
                        return;
                    }

                    _lastActivityLogWriteUtc = now;
                }

                if (isError)
                {
                    ActivityLog.TryLogError(CtrlTNavConstants.ProductName, message);
                }
                else
                {
                    ActivityLog.TryLogWarning(CtrlTNavConstants.ProductName, message);
                }
            }
            catch
            {
                // activity log is best effort only
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                if (_fileLog is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch
            {
                // ignore
            }
        }
    }
}
