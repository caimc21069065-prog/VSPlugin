using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CtrlTNav.Core;
using CtrlTNav.Core.Diagnostics;
using CtrlTNav.Core.Indexing;
using CtrlTNav.Core.Options;
using CtrlTNav.Indexing;
using CtrlTNav.Matching;
using CtrlTNav.UI;
using CtrlTNav.Vsix.Commands;
using CtrlTNav.Vsix.Diagnostics;
using CtrlTNav.Vsix.Navigation;
using CtrlTNav.Vsix.Options;
using CtrlTNav.Vsix.Services;
using CtrlTNav.Vsix.Theming;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace CtrlTNav.Vsix
{
    /// <summary>
    /// The Visual Studio package that hosts the Ctrl+T Navigator: it creates the service graph
    /// (options, C++ index, search index, search service, navigator, theme provider, index coordinator),
    /// registers the two commands, pre-creates the WPF popup so the first Ctrl+T is instant, and wires the
    /// solution lifecycle to background indexing.
    /// </summary>
    /// <remarks>
    /// The package is registered entirely through the attributes below; <c>CreatePkgDef.exe</c> turns them
    /// into <c>CtrlTNav.pkgdef</c> during packaging. Initialization is intentionally fault tolerant: a
    /// failure is logged (file log + activity log) and never allowed to crash devenv.
    /// </remarks>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration(CtrlTNavConstants.ProductName, CtrlTNavConstants.ProductDescription, CtrlTNavConstants.Version)]
    [ProvideMenuResource(CtrlTNavConstants.MenuResourceName, 1)]
    [ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideOptionPage(typeof(CtrlTNavOptionsPage), CtrlTNavConstants.OptionsCategoryName, CtrlTNavConstants.OptionsPageName, 0, 0, true)]
    [ProvideBindingPath]
    [Guid(CtrlTNavConstants.PackageGuidString)]
    public sealed class CtrlTNavPackage : AsyncPackage
    {
        private static CtrlTNavPackage? _instance;

        private VsLog? _log;
        private OptionsProvider? _optionsProvider;
        private CppIndex? _cppIndex;
        private RecentUsageTracker? _recentUsageTracker;
        private SearchIndex? _searchIndex;
        private VsSearchService? _searchService;
        private VsNavigator? _navigator;
        private VsThemeProvider? _themeProvider;
        private VsSolutionFileProvider? _solutionFileProvider;
        private IndexCoordinator? _indexCoordinator;
        private SearchWindowController? _windowController;
        private IVsStatusbar? _statusBar;
        private uint _statusProgressCookie;
        private bool _disposed;

        public CtrlTNavPackage()
        {
            _instance = this;
        }

        /// <summary>The loaded package instance, or null during shutdown.</summary>
        internal static CtrlTNavPackage? Instance
        {
            get { return _instance; }
        }

        // ---------------------------------------------------------------------------------------------
        // Services exposed to the command handler and the index coordinator
        // ---------------------------------------------------------------------------------------------

        internal ILog? Log
        {
            get { return _log; }
        }

        internal OptionsProvider? OptionsProvider
        {
            get { return _optionsProvider; }
        }

        /// <summary>Current options snapshot; never null even if initialization failed.</summary>
        internal CtrlTNavOptions CurrentOptions
        {
            get
            {
                try
                {
                    OptionsProvider? provider = _optionsProvider;
                    return provider != null ? provider.Current : CtrlTNavOptions.CreateDefault();
                }
                catch
                {
                    return CtrlTNavOptions.CreateDefault();
                }
            }
        }

        internal VsSearchService? SearchService
        {
            get { return _searchService; }
        }

        internal VsNavigator? Navigator
        {
            get { return _navigator; }
        }

        internal VsThemeProvider? ThemeProvider
        {
            get { return _themeProvider; }
        }

        /// <summary>The search window controller, or null when it has not been created yet.</summary>
        internal SearchWindowController? WindowController
        {
            get { return _windowController; }
        }

        // ---------------------------------------------------------------------------------------------
        // Initialization
        // ---------------------------------------------------------------------------------------------

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            var log = new VsLog();
            _log = log;

            try
            {
                log.Info("Initializing " + CtrlTNavConstants.ProductName + " " + CtrlTNavConstants.Version + ".");
                await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

                // ----- options -----
                _optionsProvider = new OptionsProvider(this, log);
                _optionsProvider.InitializeOnUiThread();
                _optionsProvider.OptionsChanged += OnOptionsChanged;
                CtrlTNavOptions options = _optionsProvider.Current;

                // ----- indexing engine + search engine -----
                _cppIndex = new CppIndex(options, log);
                _recentUsageTracker = new RecentUsageTracker(AppPaths.GetRecentUsageFilePath(), options.RecentUsageCapacity, log);
                try
                {
                    _recentUsageTracker.Load();
                }
                catch (Exception ex)
                {
                    log.Warn("Could not load the recent usage data; starting with an empty list: " + ex.Message);
                }

                _searchIndex = new SearchIndex(log)
                {
                    RecentUsage = _recentUsageTracker,
                };

                _searchService = new VsSearchService(_searchIndex, _recentUsageTracker, _cppIndex, _optionsProvider, log);
                _navigator = new VsNavigator(this, log);

                // ----- theming + solution file provider + index coordinator -----
                _themeProvider = new VsThemeProvider(log);
                _themeProvider.Initialize();
                _themeProvider.ThemeChanged += OnThemeChanged;

                _solutionFileProvider = new VsSolutionFileProvider(this, _optionsProvider, log);
                _indexCoordinator = new IndexCoordinator(this, _cppIndex, _solutionFileProvider, _searchService, _optionsProvider, log);

                // ----- menu commands -----
                await RegisterCommandsAsync(cancellationToken);

                // ----- pre-create the popup so the first Ctrl+T is instant (best effort) -----
                try
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                    EnsureWindowController();
                }
                catch (Exception ex)
                {
                    log.Warn("Pre-creating the search window failed; it will be created on first use: " + ex.Message);
                }

                // ----- solution lifecycle -----
                try
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                    _indexCoordinator.Initialize();
                }
                catch (Exception ex)
                {
                    log.Error("Index coordinator initialization failed; background indexing is disabled.", ex);
                }

                log.Info("Initialization complete.");
                SetStatusText(CtrlTNavConstants.ProductName + ": press Ctrl+T to go to everything");
            }
            catch (OperationCanceledException)
            {
                log.Warn("Package initialization was cancelled.");
            }
            catch (Exception ex)
            {
                // Never let a package load failure take down devenv. Each subsystem tolerates a null
                // dependency, so whatever was created before the failure keeps working.
                log.Error("Package initialization failed; the extension will run in degraded mode.", ex);
            }
        }

        private async Task RegisterCommandsAsync(CancellationToken cancellationToken)
        {
            try
            {
                object? service = await GetServiceAsync(typeof(IMenuCommandService));
                if (service is OleMenuCommandService menuCommandService)
                {
                    GoToEverythingCommand.Register(this, menuCommandService, CtrlTNavConstants.CommandIdGoToEverything);
                    GoToEverythingCommand.Register(this, menuCommandService, CtrlTNavConstants.CommandIdGoToEverythingAlt);
                    _log?.Info("Menu commands registered.");
                }
                else
                {
                    _log?.Warn("The IMenuCommandService is not available; menu commands were not registered.");
                }
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                _log?.Error("Registering the menu commands failed.", ex);
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Search window
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Creates the search window controller (hidden) if it does not exist yet. Must be called on the UI
        /// thread because the WPF window is created on the dispatcher thread.
        /// </summary>
        internal void EnsureWindowController()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_windowController != null)
            {
                return;
            }

            VsSearchService? searchService = _searchService;
            VsNavigator? navigator = _navigator;
            if (searchService == null || navigator == null)
            {
                return;
            }

            try
            {
                SearchWindowTheme theme = _themeProvider != null ? _themeProvider.CreateTheme() : SearchWindowTheme.Dark();
                _windowController = SearchWindowController.Create(searchService, navigator, theme, CreateWindowOptions());
                _log?.Info("Search window pre-created.");
            }
            catch (Exception ex)
            {
                // The controller is retried on the next ShowSearchWindow call.
                _log?.Error("Could not create the search window; it will be retried on first use.", ex);
            }
        }

        /// <summary>Shows the popup, refreshing the options and the Visual Studio theme first.</summary>
        internal void ShowSearchWindow()
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                if (_searchService == null || _navigator == null)
                {
                    SetStatusText(CtrlTNavConstants.ProductName + " is not ready yet; try again in a moment.");
                    return;
                }

                EnsureWindowController();
                SearchWindowController? controller = _windowController;
                if (controller == null)
                {
                    SetStatusText(CtrlTNavConstants.ProductName + ": the search window is not available - see the log.");
                    return;
                }

                try
                {
                    DTE? dte = VsDteUtilities.GetDte(this);
                    _searchService.UpdateCurrentProjectPath(VsDteUtilities.GetActiveProjectPath(dte));
                }
                catch (Exception ex)
                {
                    _log?.Warn("Could not refresh the current project path: " + ex.Message);
                }

                controller.UpdateOptions(CreateWindowOptions());
                if (_themeProvider != null)
                {
                    controller.ApplyTheme(_themeProvider.CreateTheme());
                }

                SetStatusText(CtrlTNavConstants.ProductName + ": Go to Everything (Ctrl+T)");
                controller.ShowSearch();
            }
            catch (Exception ex)
            {
                _log?.Error("Showing the search window failed.", ex);
                SetStatusText(CtrlTNavConstants.ProductName + ": could not open the search window - see the log.");
            }
        }

        private SearchWindowOptions CreateWindowOptions()
        {
            CtrlTNavOptions options = CurrentOptions;
            string? solutionDirectory = null;
            try
            {
                solutionDirectory = VsDteUtilities.GetSolutionDirectory(VsDteUtilities.GetDte(this));
            }
            catch (Exception ex)
            {
                _log?.Warn("Could not read the solution directory: " + ex.Message);
            }

            return new SearchWindowOptions
            {
                RelativePathBaseDirectory = solutionDirectory,
                ShowLineNumbers = options.ShowLineNumbers,
                ShowRelativePaths = options.ShowRelativePaths,
                CloseOnFocusLost = options.CloseOnFocusLost,
                Placeholder = "Go to everything (Ctrl+T)",
            };
        }

        // ---------------------------------------------------------------------------------------------
        // Status bar helpers
        // ---------------------------------------------------------------------------------------------

        /// <summary>Sets the status bar text from any thread (fire and forget, errors are logged).</summary>
        internal void SetStatusText(string text)
        {
            SetStatusProgress(false, text);
        }

        /// <summary>
        /// Shows or hides the status bar progress animation. Safe to call from any thread; updates are
        /// marshalled to the UI thread and failures are swallowed.
        /// </summary>
        internal void SetStatusProgress(bool inProgress, string label, uint complete = 0, uint total = 0)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                JoinableTaskFactory.RunAsync(async () =>
                {
                    try
                    {
                        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
                        IVsStatusbar? statusBar = GetStatusBar();
                        if (statusBar == null)
                        {
                            return;
                        }

                        uint cookie = _statusProgressCookie;
                        if (inProgress)
                        {
                            statusBar.Progress(ref cookie, 1, label, complete, total == 0 ? 1u : total);
                        }
                        else
                        {
                            statusBar.Progress(ref cookie, 0, string.Empty, 0, 0);
                            if (!string.IsNullOrEmpty(label))
                            {
                                statusBar.SetText(label);
                            }
                        }

                        _statusProgressCookie = cookie;
                    }
                    catch (OperationCanceledException)
                    {
                        // package is shutting down
                    }
                    catch (Exception ex)
                    {
                        _log?.Warn("Updating the status bar failed: " + ex.Message);
                    }
                });
            }
            catch (Exception ex)
            {
                _log?.Warn("Could not marshal the status bar update: " + ex.Message);
            }
        }

        private IVsStatusbar? GetStatusBar()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_statusBar != null)
            {
                return _statusBar;
            }

            try
            {
                _statusBar = GetService(typeof(SVsStatusbar)) as IVsStatusbar;
            }
            catch (Exception ex)
            {
                _log?.Warn("Could not obtain the status bar service: " + ex.Message);
            }

            return _statusBar;
        }

        // ---------------------------------------------------------------------------------------------
        // Native command fallbacks
        // ---------------------------------------------------------------------------------------------

        /// <summary>Executes a native Visual Studio command on the UI thread. Returns false on failure.</summary>
        internal bool TryExecuteNativeCommand(string commandName)
        {
            try
            {
                return JoinableTaskFactory.Run(async () =>
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
                    return VsDteUtilities.TryExecuteCommand(VsDteUtilities.GetDte(this), commandName);
                });
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                _log?.Warn("Executing the native command '" + commandName + "' failed: " + ex.Message);
                return false;
            }
        }

        internal void LogWarn(string message)
        {
            _log?.Warn(message);
        }

        internal void LogError(string message, Exception? exception = null)
        {
            _log?.Error(message, exception);
        }

        // ---------------------------------------------------------------------------------------------
        // Option / theme change plumbing
        // ---------------------------------------------------------------------------------------------

        private void OnOptionsChanged(object? sender, OptionsChangedEventArgs e)
        {
            try
            {
                if (e == null || (!e.DisplayChanged && !e.RecentUsageChanged))
                {
                    return;
                }

                if (e.RecentUsageChanged)
                {
                    // Reconfigures the tracker when the capacity changed; the enable flag is read on
                    // every query/recording and needs no reconfiguration.
                    _searchService?.UpdateOptions(e.Current);
                }

                RefreshWindowAsync();
            }
            catch (Exception ex)
            {
                _log?.Warn("Refreshing the search window after an options change failed: " + ex.Message);
            }
        }

        private void OnThemeChanged(object? sender, EventArgs e)
        {
            RefreshWindowAsync();
        }

        private void RefreshWindowAsync()
        {
            if (_disposed || _windowController == null)
            {
                return;
            }

            try
            {
                JoinableTaskFactory.RunAsync(async () =>
                {
                    try
                    {
                        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
                        SearchWindowController? controller = _windowController;
                        if (controller == null)
                        {
                            return;
                        }

                        controller.UpdateOptions(CreateWindowOptions());
                        if (_themeProvider != null)
                        {
                            controller.ApplyTheme(_themeProvider.CreateTheme());
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // shutting down
                    }
                    catch (Exception ex)
                    {
                        _log?.Warn("Refreshing the search window failed: " + ex.Message);
                    }
                });
            }
            catch (Exception ex)
            {
                _log?.Warn("Could not marshal the search window refresh: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Disposal
        // ---------------------------------------------------------------------------------------------

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing && !_disposed)
                {
                    _disposed = true;
                    if (_instance == this)
                    {
                        _instance = null;
                    }

                    DisposeSafely(() => _indexCoordinator?.Dispose(), "index coordinator");
                    _indexCoordinator = null;

                    DisposeSafely(() => _windowController?.Dispose(), "search window");
                    _windowController = null;

                    DisposeSafely(() => _themeProvider?.Dispose(), "theme provider");
                    _themeProvider = null;

                    DisposeSafely(() => _searchService?.Dispose(), "search service");
                    _searchService = null;

                    DisposeSafely(() => _cppIndex?.Dispose(), "C++ index");
                    _cppIndex = null;

                    DisposeSafely(() => _optionsProvider?.Dispose(), "options provider");
                    _optionsProvider = null;

                    _searchIndex = null;
                    _recentUsageTracker = null;
                    _solutionFileProvider = null;
                    _navigator = null;

                    _log?.Info("Package disposed.");
                    DisposeSafely(() => _log?.Dispose(), "log");
                    _log = null;
                }
            }
            catch (Exception ex)
            {
                try
                {
                    _log?.Error("Disposing the package failed.", ex);
                }
                catch
                {
                    // nothing else we can do during shutdown
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }

        private void DisposeSafely(Action? action, string name)
        {
            if (action == null)
            {
                return;
            }

            try
            {
                action();
            }
            catch (Exception ex)
            {
                _log?.Warn("Disposing the " + name + " failed: " + ex.Message);
            }
        }
    }
}
