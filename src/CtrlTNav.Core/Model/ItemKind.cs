using System;

namespace CtrlTNav.Core.Model
{
    /// <summary>
    /// Kind of a navigable item. The values are stable and are serialized by the index cache,
    /// so never renumber existing members (append new ones instead).
    /// </summary>
    public enum ItemKind
    {
        Unknown = 0,

        /// <summary>A source file (one pseudo item per indexed file).</summary>
        File = 1,

        Namespace = 2,

        Class = 3,
        Struct = 4,
        Union = 5,
        Interface = 6,
        Enum = 7,
        EnumMember = 8,
        TypeAlias = 9,

        Function = 10,
        Method = 11,
        Constructor = 12,
        Destructor = 13,
        Operator = 14,

        Property = 15,
        Field = 16,
        GlobalVariable = 17,

        Macro = 18,
        Concept = 19,
        Template = 20,
    }

    /// <summary>Filter category selected by a search prefix such as <c>t </c>, <c>f </c>, <c># </c>.</summary>
    public enum ItemKindFilter
    {
        All = 0,
        Types = 1,
        Files = 2,
        Symbols = 3,
        Members = 4,
    }

    public static class ItemKindExtensions
    {
        /// <summary>True for kinds that represent a type (used by the <c>t </c> filter).</summary>
        public static bool IsTypeKind(this ItemKind kind)
        {
            switch (kind)
            {
                case ItemKind.Class:
                case ItemKind.Struct:
                case ItemKind.Union:
                case ItemKind.Interface:
                case ItemKind.Enum:
                case ItemKind.TypeAlias:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>True for kinds that are scoped members of a type/namespace.</summary>
        public static bool IsMemberKind(this ItemKind kind)
        {
            switch (kind)
            {
                case ItemKind.Method:
                case ItemKind.Constructor:
                case ItemKind.Destructor:
                case ItemKind.Operator:
                case ItemKind.Property:
                case ItemKind.Field:
                case ItemKind.EnumMember:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>True for everything that is a code symbol (as opposed to a file).</summary>
        public static bool IsSymbolKind(this ItemKind kind)
        {
            return kind != ItemKind.File && kind != ItemKind.Unknown;
        }

        public static bool IsCallableKind(this ItemKind kind)
        {
            switch (kind)
            {
                case ItemKind.Function:
                case ItemKind.Method:
                case ItemKind.Constructor:
                case ItemKind.Destructor:
                case ItemKind.Operator:
                    return true;
                default:
                    return false;
            }
        }

        public static bool MatchesFilter(this ItemKind kind, ItemKindFilter filter)
        {
            switch (filter)
            {
                case ItemKindFilter.All:
                    return kind != ItemKind.Unknown;
                case ItemKindFilter.Types:
                    return kind.IsTypeKind();
                case ItemKindFilter.Files:
                    return kind == ItemKind.File;
                case ItemKindFilter.Symbols:
                    return kind.IsSymbolKind();
                case ItemKindFilter.Members:
                    return kind.IsMemberKind();
                default:
                    return false;
            }
        }

        /// <summary>Short one or two character badge shown in the result list icon.</summary>
        public static string GetShortCode(this ItemKind kind)
        {
            switch (kind)
            {
                case ItemKind.File: return "F";
                case ItemKind.Namespace: return "N";
                case ItemKind.Class: return "C";
                case ItemKind.Struct: return "S";
                case ItemKind.Union: return "U";
                case ItemKind.Interface: return "I";
                case ItemKind.Enum: return "E";
                case ItemKind.EnumMember: return "EM";
                case ItemKind.TypeAlias: return "TA";
                case ItemKind.Function: return "M";
                case ItemKind.Method: return "M";
                case ItemKind.Constructor: return "C";
                case ItemKind.Destructor: return "D";
                case ItemKind.Operator: return "OP";
                case ItemKind.Property: return "P";
                case ItemKind.Field: return "V";
                case ItemKind.GlobalVariable: return "V";
                case ItemKind.Macro: return "#";
                case ItemKind.Concept: return "CP";
                case ItemKind.Template: return "T";
                default: return "?";
            }
        }

        /// <summary>Human readable kind name used in the UI tooltip and settings.</summary>
        public static string GetDisplayName(this ItemKind kind)
        {
            switch (kind)
            {
                case ItemKind.File: return "File";
                case ItemKind.Namespace: return "Namespace";
                case ItemKind.Class: return "Class";
                case ItemKind.Struct: return "Struct";
                case ItemKind.Union: return "Union";
                case ItemKind.Interface: return "Interface";
                case ItemKind.Enum: return "Enum";
                case ItemKind.EnumMember: return "Enum member";
                case ItemKind.TypeAlias: return "Type alias";
                case ItemKind.Function: return "Function";
                case ItemKind.Method: return "Method";
                case ItemKind.Constructor: return "Constructor";
                case ItemKind.Destructor: return "Destructor";
                case ItemKind.Operator: return "Operator";
                case ItemKind.Property: return "Property";
                case ItemKind.Field: return "Field";
                case ItemKind.GlobalVariable: return "Variable";
                case ItemKind.Macro: return "Macro";
                case ItemKind.Concept: return "Concept";
                case ItemKind.Template: return "Template";
                default: return "Symbol";
            }
        }
    }
}
