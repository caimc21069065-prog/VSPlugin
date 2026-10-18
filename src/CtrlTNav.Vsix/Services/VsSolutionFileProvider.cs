using System;
using System.Collections.Generic;
using System.IO;
using CtrlTNav.Core.Diagnostics;
using CtrlTNav.Core.Indexing;
using CtrlTNav.Core.Options;
using CtrlTNav.Indexing;
using CtrlTNav.Vsix.Options;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace CtrlTNav.Vsix.Services
{
    /// <summary>
    /// <see cref="ISolutionFileProvider"/> implementation that enumerates the files of the open solution
    /// through the EnvDTE automation model, with a <c>.sln</c> parser fallback for project systems that do
    /// not expose their items through automation.
    /// </summary>
    /// <remarks>
    /// The DTE is only touched on the Visual Studio main thread; the returned candidate list is then
    /// filtered (extensions, glob excludes, file size, existence) on the calling background thread. Every
    /// COM call is wrapped because unloaded, unmodeled or broken projects throw on nearly every property.
    /// </remarks>
    public sealed class VsSolutionFileProvider : ISolutionFileProvider
    {
        private const int MaxProjectDepth = 12;
        private const int MaxProjectItemDepth = 24;

        private readonly CtrlTNavPackage _package;
        private readonly OptionsProvider _optionsProvider;
        private readonly ILog _log;
        private readonly JoinableTaskFactory _joinableTaskFactory;

        internal VsSolutionFileProvider(
            CtrlTNavPackage package,
            OptionsProvider optionsProvider,
            ILog log)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _optionsProvider = optionsProvider ?? throw new ArgumentNullException(nameof(optionsProvider));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _joinableTaskFactory = package.JoinableTaskFactory;
        }

        /// <summary>Absolute path of the open solution as of the last <see cref="GetSourceFiles"/> call.</summary>
        public string? SolutionPath { get; private set; }

        public IReadOnlyList<SourceFileInfo> GetSourceFiles()
        {
            CtrlTNavOptions options = _optionsProvider.Current;
            var result = new List<SourceFileInfo>(512);

            try
            {
                List<Candidate> candidates = EnumerateCandidates(options);
                FilterCandidates(candidates, options, result);

                if (result.Count == 0 && !string.IsNullOrWhiteSpace(SolutionPath))
                {
                    TryParseSolutionFallback(SolutionPath!, options, result);
                }

                if (options.IndexIncludeDirectories)
                {
                    ScanAdditionalIncludeDirectories(options, result);
                }

                SortByPath(result);
            }
            catch (Exception ex)
            {
                _log.Error("Enumerating the solution files failed; the extension will index whatever was collected so far.", ex);
            }

            _log.Info("Solution file provider found " + result.Count + " source files (solution: " + (SolutionPath ?? "<none>") + ").");
            return result;
        }

        // ---------------------------------------------------------------------------------------------
        // DTE enumeration (UI thread)
        // ---------------------------------------------------------------------------------------------

        private List<Candidate> EnumerateCandidates(CtrlTNavOptions options)
        {
            return _joinableTaskFactory.Run(async () =>
            {
                await _joinableTaskFactory.SwitchToMainThreadAsync();
                return EnumerateCandidatesOnMainThread(options);
            });
        }

        private List<Candidate> EnumerateCandidatesOnMainThread(CtrlTNavOptions options)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var list = new List<Candidate>(1024);
            DTE? dte = VsDteUtilities.GetDte(_package);
            SolutionPath = VsDteUtilities.GetSolutionPath(dte);
            if (dte == null)
            {
                return list;
            }

            if (!options.IndexSolutionFiles)
            {
                CollectOpenDocuments(dte, list);
                return list;
            }

            try
            {
                Solution? solution = dte.Solution;
                if (solution == null || !solution.IsOpen)
                {
                    CollectOpenDocuments(dte, list);
                    return list;
                }

                Projects? projects = solution.Projects;
                if (projects == null)
                {
                    return list;
                }

                int count;
                try
                {
                    count = projects.Count;
                }
                catch (Exception ex)
                {
                    _log.Warn("Could not read the solution project count: " + ex.Message);
                    return list;
                }

                for (int i = 1; i <= count; i++)
                {
                    Project? project = null;
                    try
                    {
                        project = projects.Item(i);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("Could not read solution project #" + i + ": " + ex.Message);
                        continue;
                    }

                    if (project == null)
                    {
                        continue;
                    }

                    try
                    {
                        CollectProject(project, list, 0);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("Could not enumerate the items of project '" + SafeName(project) + "': " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn("Could not enumerate the solution: " + ex.Message);
            }

            return list;
        }

        private void CollectOpenDocuments(DTE dte, List<Candidate> list)
        {
            try
            {
                Documents? documents = dte.Documents;
                if (documents == null)
                {
                    return;
                }

                foreach (Document? document in documents)
                {
                    try
                    {
                        string? path = document?.FullName;
                        if (string.IsNullOrWhiteSpace(path))
                        {
                            continue;
                        }

                        string projectName = string.Empty;
                        string projectPath = string.Empty;
                        try
                        {
                            Project? project = document!.ProjectItem?.ContainingProject;
                            projectName = project?.Name ?? string.Empty;
                            projectPath = project?.FullName ?? string.Empty;
                        }
                        catch
                        {
                            // loose files have no project
                        }

                        list.Add(new Candidate(path!, projectName, projectPath));
                    }
                    catch (Exception ex)
                    {
                        _log.Info("Skipping an open document: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn("Could not enumerate open documents: " + ex.Message);
            }
        }

        private void CollectProject(Project project, List<Candidate> list, int depth)
        {
            if (project == null || depth > MaxProjectDepth)
            {
                return;
            }

            string projectName = SafeName(project);
            string projectPath = SafeFullName(project);
            bool isSolutionFolder = IsSolutionFolder(project);

            ProjectItems? items;
            try
            {
                items = project.ProjectItems;
            }
            catch (Exception ex)
            {
                // Unloaded projects throw here; log at info level because this is a normal state.
                _log.Info("Project '" + projectName + "' has no enumerable items (" + ex.Message + ").");
                items = null;
            }

            if (items != null)
            {
                CollectProjectItems(items, projectName, projectPath, list, depth, 0);
            }

            if (isSolutionFolder)
            {
                CollectSolutionFolderProjects(project, list, depth);
            }
        }

        private void CollectSolutionFolderProjects(Project folder, List<Candidate> list, int depth)
        {
            ProjectItems? items;
            try
            {
                items = folder.ProjectItems;
            }
            catch
            {
                return;
            }

            if (items == null)
            {
                return;
            }

            int count;
            try
            {
                count = items.Count;
            }
            catch
            {
                return;
            }

            for (int i = 1; i <= count; i++)
            {
                try
                {
                    ProjectItem? item = items.Item(i);
                    Project? subProject = item?.SubProject;
                    if (subProject != null)
                    {
                        CollectProject(subProject, list, depth + 1);
                    }
                }
                catch (Exception ex)
                {
                    _log.Info("Could not read a solution folder item: " + ex.Message);
                }
            }
        }

        private void CollectProjectItems(
            ProjectItems items,
            string projectName,
            string projectPath,
            List<Candidate> list,
            int projectDepth,
            int itemDepth)
        {
            if (items == null || itemDepth > MaxProjectItemDepth)
            {
                return;
            }

            int count;
            try
            {
                count = items.Count;
            }
            catch (Exception ex)
            {
                _log.Info("Could not read the item count of project '" + projectName + "': " + ex.Message);
                return;
            }

            for (int i = 1; i <= count; i++)
            {
                ProjectItem? item = null;
                try
                {
                    item = items.Item(i);
                }
                catch (Exception ex)
                {
                    _log.Info("Could not read item #" + i + " of project '" + projectName + "': " + ex.Message);
                    continue;
                }

                if (item == null)
                {
                    continue;
                }

                string? filePath = TryGetPhysicalFilePath(item);
                if (!string.IsNullOrEmpty(filePath))
                {
                    list.Add(new Candidate(filePath!, projectName, projectPath));
                    continue;
                }

                try
                {
                    Project? subProject = item.SubProject;
                    if (subProject != null)
                    {
                        CollectProject(subProject, list, projectDepth + 1);
                        continue;
                    }
                }
                catch
                {
                    // not a solution folder item
                }

                try
                {
                    ProjectItems? children = item.ProjectItems;
                    if (children != null)
                    {
                        CollectProjectItems(children, projectName, projectPath, list, projectDepth, itemDepth + 1);
                    }
                }
                catch (Exception ex)
                {
                    _log.Info("Could not recurse into a project item of '" + projectName + "': " + ex.Message);
                }
            }
        }

        private static bool IsSolutionFolder(Project project)
        {
            try
            {
                string? kind = project.Kind;
                return !string.IsNullOrEmpty(kind) &&
                       string.Equals(kind, EnvDTE.Constants.vsProjectKindSolutionItems, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string? TryGetPhysicalFilePath(ProjectItem item)
        {
            try
            {
                string? kind = item.Kind;
                if (!string.IsNullOrEmpty(kind) &&
                    (string.Equals(kind, EnvDTE.Constants.vsProjectItemKindPhysicalFolder, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(kind, EnvDTE.Constants.vsProjectItemKindVirtualFolder, StringComparison.OrdinalIgnoreCase)))
                {
                    return null;
                }
            }
            catch
            {
                // unmodeled item: fall through to FileProperties
            }

            try
            {
                // FileProperties was removed from the 17.x interop assembly; Properties exposes the same
                // "FullPath" item and works for every project system that models its items through DTE.
                Properties? properties = item.Properties;
                if (properties == null || properties.Count == 0)
                {
                    return null;
                }

                foreach (Property? property in properties)
                {
                    try
                    {
                        if (property != null && string.Equals(property.Name, "FullPath", StringComparison.OrdinalIgnoreCase))
                        {
                            return property.Value as string;
                        }
                    }
                    catch
                    {
                        // individual properties may throw for virtual items
                    }
                }
            }
            catch
            {
                // virtual folders / unmodeled project systems
            }

            return null;
        }

        private static string SafeName(Project project)
        {
            try
            {
                return project.Name ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string SafeFullName(Project project)
        {
            try
            {
                return project.FullName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Filtering (background thread)
        // ---------------------------------------------------------------------------------------------

        private void FilterCandidates(List<Candidate> candidates, CtrlTNavOptions options, List<SourceFileInfo> result)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] excludePatterns = options.GetExcludePatterns();
            int skipped = 0;

            foreach (Candidate candidate in candidates)
            {
                try
                {
                    string path = candidate.FilePath;
                    if (string.IsNullOrWhiteSpace(path) || !seen.Add(path))
                    {
                        continue;
                    }

                    if (!IsSupported(path, options, excludePatterns))
                    {
                        skipped++;
                        continue;
                    }

                    if (!File.Exists(path))
                    {
                        skipped++;
                        continue;
                    }

                    if (options.MaxFileSizeKb > 0)
                    {
                        long length;
                        try
                        {
                            length = new FileInfo(path).Length;
                        }
                        catch
                        {
                            skipped++;
                            continue;
                        }

                        if (length > options.MaxFileSizeKb * 1024L)
                        {
                            skipped++;
                            continue;
                        }
                    }

                    result.Add(new SourceFileInfo(path, candidate.ProjectName, candidate.ProjectPath, DetectLanguage(path)));
                }
                catch (Exception ex)
                {
                    _log.Info("Skipping candidate '" + candidate.FilePath + "': " + ex.Message);
                }
            }

            if (skipped > 0)
            {
                _log.Info("Skipped " + skipped + " project items (excluded, missing or too large).");
            }
        }

        private bool IsSupported(string path, CtrlTNavOptions options, string[] excludePatterns)
        {
            try
            {
                if (GlobMatcher.IsExcluded(path, excludePatterns))
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                _log.Info("Glob matching failed for '" + path + "': " + ex.Message);
            }

            try
            {
                return SourceFileScanner.IsSupportedFile(path, options);
            }
            catch (Exception ex)
            {
                _log.Info("Extension filtering failed for '" + path + "': " + ex.Message);
                return HasSupportedExtension(path, options);
            }
        }

        private static bool HasSupportedExtension(string path, CtrlTNavOptions options)
        {
            try
            {
                string extension = Path.GetExtension(path);
                if (string.IsNullOrEmpty(extension))
                {
                    return false;
                }

                foreach (string candidate in options.GetSourceExtensions())
                {
                    if (string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // ignore
            }

            return false;
        }

        private void TryParseSolutionFallback(string solutionPath, CtrlTNavOptions options, List<SourceFileInfo> result)
        {
            try
            {
                if (!File.Exists(solutionPath))
                {
                    return;
                }

                IReadOnlyList<SourceFileInfo> parsed = SolutionFileParser.ParseSolution(solutionPath);
                if (parsed == null || parsed.Count == 0)
                {
                    return;
                }

                var candidates = new List<Candidate>(parsed.Count);
                foreach (SourceFileInfo file in parsed)
                {
                    if (file == null || string.IsNullOrWhiteSpace(file.FilePath))
                    {
                        continue;
                    }

                    candidates.Add(new Candidate(file.FilePath, file.ProjectName, file.ProjectPath));
                }

                if (candidates.Count > 0)
                {
                    _log.Info("DTE enumeration produced no files; using the .sln parser fallback (" + candidates.Count + " entries).");
                    FilterCandidates(candidates, options, result);
                }
            }
            catch (Exception ex)
            {
                _log.Warn("The .sln parser fallback failed: " + ex.Message);
            }
        }

        private void ScanAdditionalIncludeDirectories(CtrlTNavOptions options, List<SourceFileInfo> result)
        {
            string[] directories = options.GetAdditionalIncludeDirectories();
            if (directories.Length == 0)
            {
                return;
            }

            var candidates = new List<Candidate>();
            foreach (string directory in directories)
            {
                try
                {
                    if (!Directory.Exists(directory))
                    {
                        _log.Info("Additional include directory does not exist: " + directory);
                        continue;
                    }

                    IReadOnlyList<SourceFileInfo> scanned = SourceFileScanner.ScanDirectory(directory, options);
                    if (scanned == null)
                    {
                        continue;
                    }

                    foreach (SourceFileInfo file in scanned)
                    {
                        if (file != null && !string.IsNullOrWhiteSpace(file.FilePath))
                        {
                            candidates.Add(new Candidate(file.FilePath, file.ProjectName, file.ProjectPath));
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn("Scanning the additional include directory '" + directory + "' failed: " + ex.Message);
                }
            }

            if (candidates.Count > 0)
            {
                _log.Info("Additional include directories contributed " + candidates.Count + " candidate files.");
                FilterCandidates(candidates, options, result);
            }
        }

        private static void SortByPath(List<SourceFileInfo> files)
        {
            try
            {
                files.Sort((left, right) => string.Compare(left.FilePath, right.FilePath, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                // sorting is cosmetic; keep the collected order on failure
            }
        }

        private static string DetectLanguage(string path)
        {
            try
            {
                string extension = Path.GetExtension(path);
                return string.Equals(extension, ".c", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(extension, ".m", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(extension, ".mm", StringComparison.OrdinalIgnoreCase)
                    ? "c"
                    : "cpp";
            }
            catch
            {
                return "cpp";
            }
        }

        /// <summary>Lightweight project item used between the UI-thread enumeration and the background filtering.</summary>
        private readonly struct Candidate
        {
            public Candidate(string filePath, string projectName, string projectPath)
            {
                FilePath = filePath;
                ProjectName = projectName ?? string.Empty;
                ProjectPath = projectPath ?? string.Empty;
            }

            public string FilePath { get; }

            public string ProjectName { get; }

            public string ProjectPath { get; }
        }
    }
}
