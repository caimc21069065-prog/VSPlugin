using System;
using System.ComponentModel.Design;
using CtrlTNav.Core.Options;
using Microsoft.VisualStudio.Shell;

namespace CtrlTNav.Vsix.Commands
{
    /// <summary>
    /// Menu/keybinding handler for the two commands of the extension.
    /// <list type="bullet">
    /// <item><description><c>cmdidGoToEverything</c> (Ctrl+T): shows the pre-created search window, or runs
    /// the native <c>Edit.GoToAll</c> when the user disabled the Ctrl+T override.</description></item>
    /// <item><description><c>cmdidGoToEverythingAlt</c> (Ctrl+,): shows the search window, or runs
    /// <c>Tools.Options</c> when the user disabled the Ctrl+, shortcut.</description></item>
    /// </list>
    /// </summary>
    internal sealed class GoToEverythingCommand
    {
        private readonly CtrlTNavPackage _package;
        private readonly int _commandId;

        private GoToEverythingCommand(CtrlTNavPackage package, int commandId)
        {
            _package = package;
            _commandId = commandId;
        }

        /// <summary>Registers one of the two commands with the Visual Studio menu command service.</summary>
        public static void Register(CtrlTNavPackage package, OleMenuCommandService menuCommandService, int commandId)
        {
            if (package == null)
            {
                throw new ArgumentNullException(nameof(package));
            }

            if (menuCommandService == null)
            {
                throw new ArgumentNullException(nameof(menuCommandService));
            }

            var command = new GoToEverythingCommand(package, commandId);
            var commandIdObject = new CommandID(CtrlTNavConstants.CommandSetGuid, commandId);
            var menuItem = new OleMenuCommand(command.Execute, command.BeforeQueryStatus, commandIdObject);
            menuCommandService.AddCommand(menuItem);
        }

        /// <summary>Keeps the menu caption in sync with the options (the shortcut hint is dynamic).</summary>
        private void BeforeQueryStatus(object sender, EventArgs e)
        {
            try
            {
                if (!(sender is OleMenuCommand command))
                {
                    return;
                }

                command.Visible = true;
                command.Enabled = true;

                CtrlTNavOptions options = _package.CurrentOptions;
                if (_commandId == CtrlTNavConstants.CommandIdGoToEverythingAlt)
                {
                    command.Text = options.UseCtrlCommaShortcut
                        ? "Go to Everything (Ctrl+,)"
                        : "Go to Everything (Ctrl+, disabled)";
                }
                else
                {
                    command.Text = "Go to Everything...";
                }
            }
            catch (Exception ex)
            {
                _package.LogWarn("BeforeQueryStatus failed: " + ex.Message);
            }
        }

        private void Execute(object sender, EventArgs e)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                CtrlTNavOptions options = _package.CurrentOptions;

                if (_commandId == CtrlTNavConstants.CommandIdGoToEverythingAlt)
                {
                    if (!options.UseCtrlCommaShortcut)
                    {
                        _package.SetStatusText(CtrlTNavConstants.ProductName + ": opening Tools > Options (Ctrl+, shortcut is disabled)");
                        if (!_package.TryExecuteNativeCommand(CtrlTNavConstants.NativeOptionsCommand))
                        {
                            _package.LogWarn("The native Tools.Options command could not be executed.");
                        }

                        return;
                    }

                    _package.ShowSearchWindow();
                    return;
                }

                if (!options.OverrideCtrlT)
                {
                    _package.SetStatusText(CtrlTNavConstants.ProductName + ": running the native Go to All command (Ctrl+T override is disabled)");
                    if (!_package.TryExecuteNativeCommand(CtrlTNavConstants.NativeGoToAllCommand))
                    {
                        _package.LogWarn("The native Edit.GoToAll command could not be executed; falling back to the Ctrl+T Navigator window.");
                        _package.ShowSearchWindow();
                    }

                    return;
                }

                _package.ShowSearchWindow();
            }
            catch (Exception ex)
            {
                _package.LogError("The Go to Everything command failed.", ex);

                // Last resort: try to show the window directly; a broken command must not look like a
                // dead shortcut to the user.
                try
                {
                    _package.ShowSearchWindow();
                }
                catch (Exception inner)
                {
                    _package.LogError("The fallback attempt to show the search window failed too.", inner);
                }
            }
        }
    }
}
