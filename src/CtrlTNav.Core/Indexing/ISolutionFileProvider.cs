using System.Collections.Generic;

namespace CtrlTNav.Core.Indexing
{
    /// <summary>
    /// Supplies the set of files that belong to the current solution. The Visual Studio integration provides
    /// an implementation backed by the solution/project system; the indexing assembly provides a .sln parser
    /// based fallback for tests and for "no VS" scenarios.
    /// </summary>
    public interface ISolutionFileProvider
    {
        /// <summary>Absolute path of the open solution, or null when no solution is open.</summary>
        string? SolutionPath { get; }

        /// <summary>Returns the source files of the solution. Never returns null.</summary>
        IReadOnlyList<SourceFileInfo> GetSourceFiles();
    }
}
