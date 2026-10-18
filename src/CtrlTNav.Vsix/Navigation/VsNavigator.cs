using System;
using System.IO;
using EnvDTE;
using CtrlTNav.Core.Diagnostics;
using CtrlTNav.Core.Model;
using CtrlTNav.Core.Services;
using CtrlTNav.Vsix.Services;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace CtrlTNav.Vsix.Navigation
{
    /// <summary>
    /// <see cref="INavigationService"/> implementation on top of the EnvDTE automation model:
    /// opens the file with <see cref="ItemOperations.OpenFile(string, string)"/>, activates the window and
    /// moves the caret to the item position with <see cref="TextSelection.GotoLine(int, bool)"/>.
    /// </summary>
    /// <remarks>
    /// The method may be called from any thread; the actual COM work is marshalled to the VS main
    /// thread through the package's <see cref="JoinableTaskFactory"/>. Navigation never throws: every
    /// failure is reported through <c>errorMessage</c> so the popup can show it in the footer.
    /// </remarks>
    public sealed class VsNavigator : INavigationService
    {
        private const int CaretRetryCount = 4;
        private const int CaretRetryDelayMs = 25;

        private readonly CtrlTNavPackage _package;
        private readonly ILog _log;
        private readonly JoinableTaskFactory _joinableTaskFactory;

        internal VsNavigator(CtrlTNavPackage package, ILog log)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _joinableTaskFactory = package.JoinableTaskFactory;
        }

        public bool Navigate(NavigationItem item, out string? errorMessage)
        {
            if (item == null)
            {
                errorMessage = "No item selected.";
                return false;
            }

            string? error = null;
            bool result;
            try
            {
                if (ThreadHelper.CheckAccess())
                {
                    result = NavigateOnMainThread(item, out error);
                }
                else
                {
                    string? workerError = null;
                    result = _joinableTaskFactory.Run(async () =>
                    {
                        await _joinableTaskFactory.SwitchToMainThreadAsync();
                        bool navigated = NavigateOnMainThread(item, out string? innerError);
                        workerError = innerError;
                        return navigated;
                    });
                    error = workerError;
                }
            }
            catch (Exception ex)
            {
                _log.Error("Navigation failed for " + item, ex);
                result = false;
                error = "Navigation failed: " + ex.Message;
            }

            errorMessage = error;
            return result;
        }

        private bool NavigateOnMainThread(NavigationItem item, out string? errorMessage)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            errorMessage = null;

            if (string.IsNullOrWhiteSpace(item.FilePath))
            {
                errorMessage = "The selected item has no source file.";
                return false;
            }

            DTE? dte = VsDteUtilities.GetDte(_package);
            if (dte == null)
            {
                errorMessage = "Visual Studio automation is not available.";
                return false;
            }

            if (!File.Exists(item.FilePath))
            {
                errorMessage = "The file no longer exists: " + item.FilePath;
                return false;
            }

            Window? window;
            try
            {
                window = dte.ItemOperations.OpenFile(item.FilePath, EnvDTE.Constants.vsViewKindTextView);
            }
            catch (Exception ex)
            {
                _log.Warn("OpenFile with text view kind failed for '" + item.FilePath + "': " + ex.Message);
                try
                {
                    window = dte.ItemOperations.OpenFile(item.FilePath, EnvDTE.Constants.vsViewKindAny);
                }
                catch (Exception inner)
                {
                    errorMessage = "Could not open " + Path.GetFileName(item.FilePath) + ": " + inner.Message;
                    _log.Error("Could not open '" + item.FilePath + "'.", inner);
                    return false;
                }
            }

            try
            {
                window?.Activate();
            }
            catch (Exception ex)
            {
                _log.Warn("Could not activate the document window for '" + item.FilePath + "': " + ex.Message);
            }

            if (item.Line <= 0)
            {
                // File pseudo items do not have a meaningful position; opening and activating is enough.
                return true;
            }

            Document? document = null;
            for (int attempt = 0; attempt < CaretRetryCount; attempt++)
            {
                try
                {
                    document = window?.Document ?? dte.ActiveDocument;
                }
                catch (Exception ex)
                {
                    _log.Warn("Could not read the active document after opening '" + item.FilePath + "': " + ex.Message);
                }

                if (document != null)
                {
                    break;
                }

                // The editor sometimes creates the document lazily; give it a moment before giving up.
                System.Threading.Thread.Sleep(CaretRetryDelayMs);
            }

            if (document == null)
            {
                _log.Warn("Opened '" + item.FilePath + "' but the document is not ready; caret was not moved.");
                return true;
            }

            if (!TryMoveCaret(document, item))
            {
                _log.Warn("Opened '" + item.FilePath + "' but could not move the caret to line " + item.Line + ".");
            }

            return true;
        }

        private bool TryMoveCaret(Document document, NavigationItem item)
        {
            try
            {
                object selectionObject = document.Selection;
                if (!(selectionObject is TextSelection selection))
                {
                    return false;
                }

                int line = Math.Max(1, item.Line);
                selection.GotoLine(line, true);

                if (item.Column > 1)
                {
                    try
                    {
                        selection.MoveToLineAndOffset(line, item.Column, false);
                    }
                    catch (Exception ex)
                    {
                        // Column information is best effort; the line jump above already succeeded.
                        _log.Info("Could not move to column " + item.Column + " on line " + line + ": " + ex.Message);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _log.Warn("TextSelection navigation failed: " + ex.Message);
                return false;
            }
        }
    }
}
