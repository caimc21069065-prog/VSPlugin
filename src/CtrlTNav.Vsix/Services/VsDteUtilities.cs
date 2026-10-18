using System;
using System.IO;
using EnvDTE;
using Microsoft.VisualStudio.Shell;

namespace CtrlTNav.Vsix.Services
{
    /// <summary>
    /// Defensive wrappers around the EnvDTE automation object. Every member swallows COM exceptions and
    /// returns a neutral value, because the automation model throws for unloaded projects, solution
    /// folders, documents opened with a non-text editor, and similar perfectly normal states.
    /// </summary>
    /// <remarks>
    /// The methods that talk to <see cref="DTE"/> must be called on the Visual Studio main thread; the
    /// callers in this assembly marshal through <c>JoinableTaskFactory</c> before calling them.
    /// </remarks>
    internal static class VsDteUtilities
    {
        /// <summary>
        /// Returns the EnvDTE automation object for the given service provider. Must be called on the
        /// UI thread (the automation object is an STA COM object).
        /// </summary>
        public static DTE? GetDte(IServiceProvider? serviceProvider)
        {
            try
            {
                if (serviceProvider != null)
                {
                    if (serviceProvider.GetService(typeof(DTE)) is DTE fromProvider)
                    {
                        return fromProvider;
                    }
                }
            }
            catch
            {
                // fall through to the global service provider
            }

            try
            {
                return Package.GetGlobalService(typeof(DTE)) as DTE;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Absolute path of the open solution, or null when no solution is open.</summary>
        public static string? GetSolutionPath(DTE? dte)
        {
            try
            {
                if (dte == null)
                {
                    return null;
                }

                Solution? solution = dte.Solution;
                if (solution == null || !solution.IsOpen)
                {
                    return null;
                }

                string fullName = solution.FullName;
                return string.IsNullOrWhiteSpace(fullName) ? null : fullName;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Directory that contains the open solution, or null.</summary>
        public static string? GetSolutionDirectory(DTE? dte)
        {
            string? solutionPath = GetSolutionPath(dte);
            if (string.IsNullOrEmpty(solutionPath))
            {
                return null;
            }

            try
            {
                return Path.GetDirectoryName(solutionPath!);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Full path of the project that owns the active document, or null.</summary>
        public static string? GetActiveProjectPath(DTE? dte)
        {
            try
            {
                Document? document = dte?.ActiveDocument;
                if (document == null)
                {
                    return null;
                }

                ProjectItem? projectItem = document.ProjectItem;
                Project? project = projectItem?.ContainingProject;
                string? fullName = project?.FullName;
                return string.IsNullOrWhiteSpace(fullName) ? null : fullName;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Full path of the active document, or null when no document is active.</summary>
        public static string? GetActiveDocumentPath(DTE? dte)
        {
            try
            {
                string? fullName = dte?.ActiveDocument?.FullName;
                return string.IsNullOrWhiteSpace(fullName) ? null : fullName;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Executes a named Visual Studio command. Returns false when the command cannot run.</summary>
        public static bool TryExecuteCommand(DTE? dte, string commandName)
        {
            if (dte == null || string.IsNullOrWhiteSpace(commandName))
            {
                return false;
            }

            try
            {
                dte.ExecuteCommand(commandName, string.Empty);
                return true;
            }
            catch
            {
                // Some shell commands reject an empty argument list; retry with no arguments at all.
                try
                {
                    dte.ExecuteCommand(commandName, null);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}
