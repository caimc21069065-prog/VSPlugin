using System;

namespace CtrlTNav.Core.Indexing
{
    /// <summary>
    /// Describes a source file that should be indexed, together with the project that owns it.
    /// </summary>
    public sealed class SourceFileInfo
    {
        public SourceFileInfo(string filePath, string projectName = "", string projectPath = "", string language = "cpp")
        {
            FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
            ProjectName = projectName ?? string.Empty;
            ProjectPath = projectPath ?? string.Empty;
            Language = language ?? "cpp";
        }

        /// <summary>Absolute path of the source/header file.</summary>
        public string FilePath { get; }

        /// <summary>Display name of the owning project.</summary>
        public string ProjectName { get; }

        /// <summary>Absolute path of the owning project file (.vcxproj).</summary>
        public string ProjectPath { get; }

        /// <summary><c>cpp</c> for C++ files, <c>c</c> for C files.</summary>
        public string Language { get; }

        public override string ToString()
        {
            return FilePath + " [" + ProjectName + "]";
        }
    }
}
