using System;
using System.Globalization;
using System.Windows;
using CtrlTNav.UI;

namespace CtrlTNav.UiDemo
{
    /// <summary>Entry point of the UI demo/screenshot tool.</summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                DemoOptions options = DemoOptions.Parse(args);
                if (options.ShowHelp)
                {
                    DemoOptions.PrintUsage();
                    return 0;
                }

                // An Application instance gives the dispatcher its normal configuration and makes sure the
                // theme dictionaries used by the built-in controls are available in both modes.
                var application = new Application
                {
                    ShutdownMode = options.RenderDirectory != null ? ShutdownMode.OnExplicitShutdown : ShutdownMode.OnLastWindowClose,
                };

                if (options.RenderDirectory != null)
                {
                    ScreenshotRenderer.RenderAll(options.RenderDirectory, options.Query);
                    return 0;
                }

                return RunInteractive(application, options);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("CtrlTNav.UiDemo failed: " + ex);
                return 1;
            }
        }

        private static int RunInteractive(Application application, DemoOptions options)
        {
            var searchService = new FakeSearchService(SampleData.CreateItems());
            var navigationService = new FakeNavigationService();
            var windowOptions = new SearchWindowOptions
            {
                RelativePathBaseDirectory = SampleData.BaseDirectory,
            };

            SearchWindowController controller = SearchWindowController.Create(
                searchService,
                navigationService,
                SearchWindowTheme.Dark(),
                windowOptions);

            try
            {
                var shell = new DemoShellWindow(controller, options.Query);
                application.Run(shell);
                return 0;
            }
            finally
            {
                controller.Dispose();
            }
        }
    }

    /// <summary>Parsed command line of the demo tool.</summary>
    internal sealed class DemoOptions
    {
        private const string DefaultRenderDirectory = @"artifacts\screenshots";

        private DemoOptions()
        {
            Query = "device";
        }

        public string? RenderDirectory { get; private set; }

        public string Query { get; private set; }

        public bool ShowHelp { get; private set; }

        public static DemoOptions Parse(string[] args)
        {
            var options = new DemoOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string argument = args[i] ?? string.Empty;
                switch (argument.ToLowerInvariant())
                {
                    case "--render":
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                        {
                            options.RenderDirectory = args[i + 1];
                            i++;
                        }
                        else
                        {
                            options.RenderDirectory = DefaultRenderDirectory;
                        }

                        break;

                    case "--query":
                        if (i + 1 < args.Length)
                        {
                            options.Query = args[i + 1];
                            i++;
                        }

                        break;

                    case "--help":
                    case "-h":
                    case "-?":
                        options.ShowHelp = true;
                        break;
                }
            }

            return options;
        }

        public static void PrintUsage()
        {
            Console.WriteLine("CtrlTNav.UiDemo - interactive UI demo and offscreen screenshot renderer");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  CtrlTNav.UiDemo                          show the interactive demo window");
            Console.WriteLine("  CtrlTNav.UiDemo --render [directory]     render dark/light PNGs (default artifacts\\screenshots)");
            Console.WriteLine("  CtrlTNav.UiDemo --query <text>           prefill the search box (default: device)");
            Console.WriteLine();
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "Screenshots are written at 2x scale as search-window-dark.png and search-window-light.png."));
        }
    }
}
