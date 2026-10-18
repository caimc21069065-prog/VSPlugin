using System;

namespace CtrlTNav.Core.Model
{
    /// <summary>Additional information about a symbol that influences ranking and display.</summary>
    [Flags]
    public enum ItemFlags
    {
        None = 0,

        Public = 1 << 0,
        Protected = 1 << 1,
        Private = 1 << 2,

        Static = 1 << 3,

        /// <summary>Marked with an export attribute (for example <c>__declspec(dllexport)</c>).</summary>
        Exported = 1 << 4,

        /// <summary>Declared inside a <c>template&lt;...&gt;</c>.</summary>
        Template = 1 << 5,

        /// <summary>Declaration (typically in a header) without a body.</summary>
        Declaration = 1 << 6,

        /// <summary>Definition with a body.</summary>
        Definition = 1 << 7,

        /// <summary>Declared at namespace / global scope.</summary>
        NamespaceScope = 1 << 8,

        /// <summary>Declared inside a type body.</summary>
        ClassScope = 1 << 9,

        /// <summary>Declared in a header file.</summary>
        HeaderFile = 1 << 10,
    }
}
