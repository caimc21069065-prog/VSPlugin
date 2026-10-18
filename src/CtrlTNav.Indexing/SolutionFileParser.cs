using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CtrlTNav.Core.Indexing;

namespace CtrlTNav.Indexing
{
    /// <summary>
    /// Parses Visual C++ solution/project files into a flat list of <see cref="SourceFileInfo"/> entries.
    /// Parsing is tolerant: malformed XML, missing files, BOMs, MSBuild conditions and unresolvable
    /// <c>$(...)</c> variables never throw and simply skip the offending entries (NF6).
    /// </summary>
    public static class SolutionFileParser
    {
        /// <summary>Project type GUID of solution folders; they are not C++ projects.</summary>
        private const string SolutionFolderTypeGuid = "2150E333-8FDC-42A3-9474-1A3956D46DE8";

        private static readonly Regex SolutionProjectRegex = new Regex(
            "^\\s*Project\\(\"\\{(?<type>[0-9A-Fa-f-]+)\\}\"\\)\\s*=\\s*\"(?<name>[^\"]*)\"\\s*,\\s*\"(?<path>[^\"]*)\"\\s*,\\s*\"\\{?(?<id>[0-9A-Fa-f-]+)\\}?\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex ProjectDirVariableRegex = new Regex(
            "\\$\\((?:ProjectDir|MSBuildProjectDirectory)\\)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>
        /// Parses a <c>.sln</c> file and all C++ projects referenced from it.
        /// </summary>
        public static IReadOnlyList<SourceFileInfo> ParseSolution(string solutionPath)
        {
            var result = new List<SourceFileInfo>();
            try
            {
                if (string.IsNullOrEmpty(solutionPath) || !File.Exists(solutionPath))
                {
                    return result;
                }

                string fullSolutionPath = Path.GetFullPath(solutionPath);
                string solutionDirectory = Path.GetDirectoryName(fullSolutionPath) ?? string.Empty;
                string content = File.ReadAllText(fullSolutionPath);

                var projects = new List<KeyValuePair<string, string>>();
                var seenProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                using (var reader = new StringReader(content))
                {
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        Match match = SolutionProjectRegex.Match(line);
                        if (!match.Success)
                        {
                            continue;
                        }

                        if (string.Equals(match.Groups["type"].Value, SolutionFolderTypeGuid, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string relativePath = match.Groups["path"].Value;
                        if (relativePath.Length == 0)
                        {
                            continue;
                        }

                        string extension = CppFileTypes.GetExtension(relativePath);
                        if (!string.Equals(extension, ".vcxproj", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(extension, ".vcproj", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string projectPath;
                        try
                        {
                            projectPath = Path.GetFullPath(Path.Combine(
                                solutionDirectory,
                                NormalizeSeparators(relativePath)));
                        }
                        catch
                        {
                            continue;
                        }

                        if (!seenProjects.Add(projectPath))
                        {
                            continue;
                        }

                        string projectName = match.Groups["name"].Value;
                        projects.Add(new KeyValuePair<string, string>(projectPath, projectName));
                    }
                }

                var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < projects.Count; index++)
                {
                    IReadOnlyList<SourceFileInfo> files = ParseProject(projects[index].Key, projects[index].Value);
                    for (int fileIndex = 0; fileIndex < files.Count; fileIndex++)
                    {
                        SourceFileInfo file = files[fileIndex];
                        if (seenFiles.Add(file.FilePath))
                        {
                            result.Add(file);
                        }
                    }
                }
            }
            catch
            {
                // Solution parsing is best-effort; a broken solution must not break indexing.
            }

            return result;
        }

        /// <summary>
        /// Parses a <c>.vcxproj</c> file and returns its C/C++ source/header items.
        /// </summary>
        /// <param name="projectPath">Absolute path of the project file.</param>
        /// <param name="projectName">Display name; defaults to the file name without extension.</param>
        public static IReadOnlyList<SourceFileInfo> ParseProject(string projectPath, string? projectName = null)
        {
            var result = new List<SourceFileInfo>();
            try
            {
                if (string.IsNullOrEmpty(projectPath) || !File.Exists(projectPath))
                {
                    return result;
                }

                string fullProjectPath = Path.GetFullPath(projectPath);
                string projectDirectory = Path.GetDirectoryName(fullProjectPath) ?? string.Empty;
                string name = string.IsNullOrEmpty(projectName)
                    ? Path.GetFileNameWithoutExtension(fullProjectPath)
                    : projectName!;

                XDocument document;
                try
                {
                    document = XDocument.Load(fullProjectPath, LoadOptions.None);
                }
                catch
                {
                    return result;
                }

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (XElement element in document.Descendants())
                {
                    string localName = element.Name.LocalName;
                    if (!string.Equals(localName, "ClCompile", StringComparison.Ordinal) &&
                        !string.Equals(localName, "ClInclude", StringComparison.Ordinal) &&
                        !string.Equals(localName, "None", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string? include = null;
                    foreach (XAttribute attribute in element.Attributes())
                    {
                        if (string.Equals(attribute.Name.LocalName, "Include", StringComparison.Ordinal))
                        {
                            include = attribute.Value;
                            break;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(include))
                    {
                        continue;
                    }

                    string rawPath = include!.Trim();
                    if (rawPath.IndexOf('*') >= 0 || rawPath.IndexOf('?') >= 0)
                    {
                        continue;
                    }

                    if (rawPath.IndexOf("%(", StringComparison.Ordinal) >= 0)
                    {
                        continue;
                    }

                    string expanded = ProjectDirVariableRegex.Replace(
                        rawPath,
                        match => MatchesProjectDir(projectDirectory));
                    if (expanded.IndexOf("$(", StringComparison.Ordinal) >= 0)
                    {
                        // Unresolvable MSBuild variable (for example $(SolutionDir)); skip it.
                        continue;
                    }

                    string resolved;
                    try
                    {
                        string normalized = NormalizeSeparators(expanded);
                        resolved = Path.GetFullPath(
                            Path.IsPathRooted(normalized)
                                ? normalized
                                : Path.Combine(projectDirectory, normalized));
                    }
                    catch
                    {
                        continue;
                    }

                    if (!CppFileTypes.IsSupportedExtension(resolved, CppFileTypes.DefaultExtensions))
                    {
                        continue;
                    }

                    if (!seen.Add(resolved))
                    {
                        continue;
                    }

                    result.Add(new SourceFileInfo(
                        resolved,
                        name,
                        fullProjectPath,
                        CppFileTypes.GetLanguage(resolved)));
                }
            }
            catch
            {
                // Best effort: never throw out of project parsing.
            }

            return result;
        }

        private static string MatchesProjectDir(string projectDirectory)
        {
            return projectDirectory.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? projectDirectory
                : projectDirectory + Path.DirectorySeparatorChar;
        }

        private static string NormalizeSeparators(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            char separator = Path.DirectorySeparatorChar;
            return path.Replace('\\', separator).Replace('/', separator);
        }
    }
}
