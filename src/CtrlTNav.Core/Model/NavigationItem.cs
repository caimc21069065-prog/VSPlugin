using System;
using System.IO;

namespace CtrlTNav.Core.Model
{
    /// <summary>
    /// A single navigable entry: either a source file (one entry per indexed file) or a C/C++ symbol
    /// extracted from a file. Instances are created by the indexer and consumed by the search engine.
    /// </summary>
    public sealed class NavigationItem
    {
        public NavigationItem(string name, ItemKind kind, string filePath, int line = 0, int column = 0)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Kind = kind;
            FilePath = filePath ?? string.Empty;
            Line = line;
            Column = column;
        }

        /// <summary>Simple (unqualified) name shown in the result list, for example <c>PushBack</c>.</summary>
        public string Name { get; set; }

        /// <summary>Qualified name used for secondary matching, for example <c>std::vector::PushBack</c>.</summary>
        public string FullName { get; set; } = string.Empty;

        public ItemKind Kind { get; set; }

        /// <summary>Absolute path of the file that contains the item.</summary>
        public string FilePath { get; set; }

        /// <summary>1-based line number, or 0 when unknown.</summary>
        public int Line { get; set; }

        /// <summary>1-based column number, or 0 when unknown.</summary>
        public int Column { get; set; }

        /// <summary>Name of the project owning the file, or empty for loose files.</summary>
        public string ProjectName { get; set; } = string.Empty;

        /// <summary>Absolute path of the owning project file (.vcxproj), used for the current-project boost.</summary>
        public string ProjectPath { get; set; } = string.Empty;

        public ItemFlags Flags { get; set; }

        /// <summary>Enclosing namespace and/or type name, for example <c>std::vector</c>.</summary>
        public string ContainerName { get; set; } = string.Empty;

        /// <summary>Optional signature/parameter text, for example <c>(int, const char*)</c>.</summary>
        public string Signature { get; set; } = string.Empty;

        /// <summary>Source language: <c>cpp</c> or <c>c</c>.</summary>
        public string Language { get; set; } = "cpp";

        public bool IsFile
        {
            get { return Kind == ItemKind.File; }
        }

        public bool IsPublicSymbol
        {
            get { return (Flags & (ItemFlags.Public | ItemFlags.Exported)) != 0; }
        }

        public string FileName
        {
            get
            {
                try
                {
                    return Path.GetFileName(FilePath);
                }
                catch (ArgumentException)
                {
                    return FilePath;
                }
            }
        }

        /// <summary>
        /// Stable identity of the item inside a solution: file + line + column + kind + qualified name.
        /// Used by the recent-usage store and for de-duplication.
        /// </summary>
        public string GetIdentity()
        {
            return Kind + "|" + FilePath + "|" + Line + "|" + Column + "|" + (FullName.Length > 0 ? FullName : Name);
        }

        public NavigationItem CloneWithFile(string filePath)
        {
            var clone = (NavigationItem)MemberwiseClone();
            clone.FilePath = filePath;
            return clone;
        }

        public override string ToString()
        {
            return Kind + " " + (FullName.Length > 0 ? FullName : Name) + " (" + FilePath + ":" + Line + ")";
        }
    }
}
