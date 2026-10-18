using System;
using System.Collections.Generic;
using CtrlTNav.Core.Model;
using CtrlTNav.Indexing.Cpp;

namespace CtrlTNav.Indexing
{
    /// <summary>
    /// Tolerant hand-written C/C++ symbol extractor. It does not need clang or tree-sitter: it runs the
    /// token stream through a forgiving declaration scanner that understands the constructs that matter
    /// for navigation (namespaces, types, functions, members, variables, macros, aliases, concepts).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The extractor is designed for real-world, frequently incomplete code: malformed statements are
    /// skipped, unterminated strings/comments do not derail the lexer, function bodies are skipped, and no
    /// exception ever escapes <see cref="Extract"/>.
    /// </para>
    /// <para>
    /// The instance is stateless and therefore safe for concurrent use from a parallel index build.
    /// </para>
    /// <para>Known limitations (documented, acceptable for v1):</para>
    /// <list type="bullet">
    /// <item><description>No semantic analysis: a global variable initialized with constructor call
    /// arguments (<c>Foo f(1);</c>) can be classified as a function (the "most vexing parse").</description></item>
    /// <item><description>Macro-generated declarations are only visible when the macro is not
    /// parameterized in a way that hides the declared name; all-caps annotation macros are skipped.</description></item>
    /// <item><description>Conditional compilation branches with unknown conditions are all indexed; a macro
    /// or declaration can therefore appear twice.</description></item>
    /// </list>
    /// </remarks>
    public sealed class CppSymbolExtractor
    {
        public CppSymbolExtractor()
        {
        }

        /// <summary>
        /// Extracts every navigable symbol from <paramref name="content"/>.
        /// </summary>
        /// <param name="filePath">Absolute path of the file; used for the item's <c>FilePath</c> and header detection.</param>
        /// <param name="content">Full file text. May be null/empty or malformed.</param>
        /// <param name="projectName">Owning project display name (optional).</param>
        /// <param name="projectPath">Owning project file path (optional).</param>
        /// <param name="language"><c>cpp</c> or <c>c</c> (optional).</param>
        public IReadOnlyList<NavigationItem> Extract(
            string? filePath,
            string? content,
            string projectName = "",
            string projectPath = "",
            string language = "cpp")
        {
            var run = new ExtractionRun(
                filePath ?? string.Empty,
                content ?? string.Empty,
                projectName ?? string.Empty,
                projectPath ?? string.Empty,
                language ?? "cpp");
            return run.Execute();
        }

        // =============================================================================================
        // Extraction state machine
        // =============================================================================================

        private enum FrameKind
        {
            Global = 0,
            Namespace = 1,
            Class = 2,
            Enum = 3,
            ExternC = 4,
        }

        private sealed class ScopeFrame
        {
            public FrameKind Kind;
            public string Name = string.Empty;
            public string FullName = string.Empty;
            public ItemFlags Access = ItemFlags.Public;
        }

        private struct FunctionCandidate
        {
            public bool Found;
            public int NameListPos;
            public int NameTokenIndex;
            public int ParamsOpenTokenIndex;
            public string Name;
            public string Qualifier;
            public bool IsOperator;
            public bool IsDestructor;
        }

        private sealed class ExtractionRun
        {
            private const int MaxItems = 100000;
            private const int MaxSignatureLength = 200;

            private static readonly CppToken EofToken = new CppToken(CppTokenKind.EndOfFile, 0, 0, 0, 0);

            private readonly string _filePath;
            private readonly string _content;
            private readonly string _projectName;
            private readonly string _projectPath;
            private readonly string _language;
            private readonly bool _isHeaderFile;

            private readonly List<NavigationItem> _items = new List<NavigationItem>(64);
            private readonly List<ScopeFrame> _scope = new List<ScopeFrame>(16);

            private List<CppToken> _tokens = new List<CppToken>();
            private Dictionary<int, string> _macroSignatures = new Dictionary<int, string>();
            private int _i;
            private bool _pendingTemplate;
            private bool _pendingTypedef;
            private bool _pendingExported;

            public ExtractionRun(string filePath, string content, string projectName, string projectPath, string language)
            {
                _filePath = filePath;
                _content = content;
                _projectName = projectName;
                _projectPath = projectPath;
                _language = language;
                _isHeaderFile = CppFileTypes.IsHeaderPath(filePath);
            }

            public IReadOnlyList<NavigationItem> Execute()
            {
                try
                {
                    TokenizedFile tokenized = CppTokenizer.Tokenize(_content);
                    _tokens = tokenized.Tokens;
                    _macroSignatures = tokenized.MacroSignatures;
                    _scope.Add(new ScopeFrame { Kind = FrameKind.Global });

                    EmitMacroItems();
                    _i = 0;
                    ParseTopLevel();
                }
                catch
                {
                    // The indexer must survive anything. Partial results are better than a failed build.
                }

                return _items;
            }

            // -----------------------------------------------------------------------------------------
            // Token helpers
            // -----------------------------------------------------------------------------------------

            private CppToken Tok
            {
                get { return _i >= 0 && _i < _tokens.Count ? _tokens[_i] : EofToken; }
            }

            private CppToken Peek(int offset = 1)
            {
                int index = _i + offset;
                return index >= 0 && index < _tokens.Count ? _tokens[index] : EofToken;
            }

            private bool AtEnd
            {
                get { return _i >= _tokens.Count || Tok.Kind == CppTokenKind.EndOfFile; }
            }

            private bool IsPunct(CppToken token, char value)
            {
                return token.Kind == CppTokenKind.Punctuation && token.Length == 1 && token.Start >= 0 &&
                       token.Start < _content.Length && _content[token.Start] == value;
            }

            private bool IsPunct(CppToken token, string value)
            {
                if (token.Kind != CppTokenKind.Punctuation || token.Length != value.Length || token.Start < 0)
                {
                    return false;
                }

                if (token.Start + token.Length > _content.Length)
                {
                    return false;
                }

                return string.CompareOrdinal(_content, token.Start, value, 0, value.Length) == 0;
            }

            private bool IsWord(CppToken token, string value)
            {
                return token.Kind == CppTokenKind.Identifier && token.Length == value.Length && token.Start >= 0 &&
                       token.Start + token.Length <= _content.Length &&
                       string.CompareOrdinal(_content, token.Start, value, 0, value.Length) == 0;
            }

            private bool IsAngleClose(CppToken token)
            {
                return IsPunct(token, ">") || IsPunct(token, ">>");
            }

            private bool IsTerminatedStringLiteral(CppToken token)
            {
                int end = token.Start + token.Length;
                return token.Length >= 2 && end <= _content.Length && _content[end - 1] == '"';
            }

            private string GetText(CppToken token)
            {
                if (token.Length <= 0 || token.Start < 0 || token.Start + token.Length > _content.Length)
                {
                    return string.Empty;
                }

                return _content.Substring(token.Start, token.Length);
            }

            private string GetText(int tokenIndex)
            {
                return tokenIndex >= 0 && tokenIndex < _tokens.Count ? GetText(_tokens[tokenIndex]) : string.Empty;
            }

            private static bool IsMacroLikeToken(string text)
            {
                if (text.Length < 2)
                {
                    return false;
                }

                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    bool upper = c >= 'A' && c <= 'Z';
                    bool digit = c >= '0' && c <= '9';
                    if (!upper && !digit && c != '_')
                    {
                        return false;
                    }
                }

                return true;
            }

            private static bool IsMacroLike(CppToken token, string text)
            {
                return token.Kind == CppTokenKind.Identifier && IsMacroLikeToken(text);
            }

            private static bool IsReservedWord(string word)
            {
                return CppKeywords.Contains(word);
            }

            private static readonly HashSet<string> CppKeywords = new HashSet<string>(StringComparer.Ordinal)
            {
                "alignas", "alignof", "and", "and_eq", "asm", "auto", "bitand", "bitor", "bool", "break",
                "case", "catch", "char", "char8_t", "char16_t", "char32_t", "class", "co_await", "co_return",
                "co_yield", "compl", "concept", "const", "consteval", "constexpr", "constinit", "const_cast",
                "continue", "decltype", "default", "delete", "do", "double", "dynamic_cast", "else", "enum",
                "explicit", "export", "extern", "false", "final", "float", "for", "friend", "goto", "if",
                "import", "inline", "int", "long", "module", "mutable", "namespace", "new", "noexcept", "not",
                "not_eq", "nullptr", "operator", "or", "or_eq", "override", "private", "protected", "public",
                "register", "reinterpret_cast", "requires", "return", "sealed", "short", "signed", "sizeof",
                "static", "static_assert", "static_cast", "struct", "switch", "template", "this", "thread_local",
                "throw", "true", "try", "typedef", "typeid", "typename", "union", "unsigned", "using", "virtual",
                "void", "volatile", "wchar_t", "while", "xor", "xor_eq",
                "__interface", "__declspec", "__attribute__", "__pragma", "_Pragma", "__based", "__cdecl",
                "__stdcall", "__fastcall", "__thiscall", "__vectorcall", "__forceinline", "__inline", "__restrict",
                "__asm", "abstract", "super", "slots", "signals", "Q_SIGNALS", "Q_SLOTS",
            };

            // -----------------------------------------------------------------------------------------
            // Scope helpers
            // -----------------------------------------------------------------------------------------

            private ScopeFrame CurrentFrame
            {
                get { return _scope[_scope.Count - 1]; }
            }

            private string CurrentContainer
            {
                get
                {
                    for (int i = _scope.Count - 1; i >= 0; i--)
                    {
                        if (_scope[i].FullName.Length > 0)
                        {
                            return _scope[i].FullName;
                        }
                    }

                    return string.Empty;
                }
            }

            private bool InAnyClassScope
            {
                get
                {
                    for (int i = _scope.Count - 1; i >= 0; i--)
                    {
                        if (_scope[i].Kind == FrameKind.Class)
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }

            private ItemFlags CurrentAccess
            {
                get
                {
                    for (int i = _scope.Count - 1; i >= 0; i--)
                    {
                        if (_scope[i].Kind == FrameKind.Class)
                        {
                            return _scope[i].Access;
                        }
                    }

                    return ItemFlags.Public;
                }
            }

            private string CurrentClassSimpleName
            {
                get
                {
                    for (int i = _scope.Count - 1; i >= 0; i--)
                    {
                        if (_scope[i].Kind == FrameKind.Class)
                        {
                            return _scope[i].Name;
                        }
                    }

                    return string.Empty;
                }
            }

            private ScopeFrame PushFrame(FrameKind kind, string name, string fullName)
            {
                var frame = new ScopeFrame { Kind = kind, Name = name, FullName = fullName };
                _scope.Add(frame);
                return frame;
            }

            private void PopFrame()
            {
                if (_scope.Count > 1)
                {
                    _scope.RemoveAt(_scope.Count - 1);
                }
            }

            private static string MakeFullName(string container, string name)
            {
                return container.Length > 0 ? container + "::" + name : name;
            }

            private string ResolveContainer(string qualifier)
            {
                string current = CurrentContainer;
                if (qualifier.Length == 0)
                {
                    return current;
                }

                if (current.Length == 0 || qualifier == current)
                {
                    return qualifier;
                }

                if (qualifier.StartsWith(current + "::", StringComparison.Ordinal))
                {
                    return qualifier;
                }

                return current + "::" + qualifier;
            }

            private void ResetPending()
            {
                _pendingTemplate = false;
                _pendingTypedef = false;
                _pendingExported = false;
            }

            /// <summary>Flags contributed by the <c>template&lt;...&gt;</c> / <c>__declspec</c> prefixes.</summary>
            private ItemFlags PendingModifiers()
            {
                ItemFlags flags = ItemFlags.None;
                if (_pendingTemplate)
                {
                    flags |= ItemFlags.Template;
                }

                if (_pendingExported)
                {
                    flags |= ItemFlags.Exported;
                }

                return flags;
            }

            // -----------------------------------------------------------------------------------------
            // Item emission
            // -----------------------------------------------------------------------------------------

            private void AddSymbol(
                ItemKind kind,
                CppToken position,
                string name,
                string container,
                string? fullNameOverride,
                ItemFlags extraFlags,
                string? signature)
            {
                if (string.IsNullOrEmpty(name) || _items.Count >= MaxItems)
                {
                    return;
                }

                ItemFlags flags = extraFlags;
                if (_isHeaderFile)
                {
                    flags |= ItemFlags.HeaderFile;
                }

                if (InAnyClassScope)
                {
                    flags |= ItemFlags.ClassScope;
                    flags |= CurrentAccess;
                }
                else
                {
                    flags |= ItemFlags.NamespaceScope;
                    flags |= ItemFlags.Public;
                }

                string fullName = fullNameOverride ?? MakeFullName(container, name);
                var item = new NavigationItem(name, kind, _filePath, position.Line, position.Column)
                {
                    FullName = fullName,
                    ProjectName = _projectName,
                    ProjectPath = _projectPath,
                    Flags = flags,
                    ContainerName = container,
                    Signature = signature ?? string.Empty,
                    Language = _language,
                };
                _items.Add(item);
            }

            private void EmitMacroItems()
            {
                for (int index = 0; index < _tokens.Count; index++)
                {
                    CppToken token = _tokens[index];
                    if (token.Kind != CppTokenKind.Macro)
                    {
                        continue;
                    }

                    string? signature;
                    if (!_macroSignatures.TryGetValue(token.Start, out signature))
                    {
                        signature = string.Empty;
                    }

                    AddSymbol(
                        ItemKind.Macro,
                        token,
                        GetText(token),
                        string.Empty,
                        null,
                        ItemFlags.None,
                        signature);
                }
            }

            /// <summary>Re-parents items created inside an anonymous type body once an alias/name is known.</summary>
            private void ReparentItems(int startIndex, int endIndex, string oldContainer, string newContainer)
            {
                for (int index = startIndex; index < endIndex && index < _items.Count; index++)
                {
                    NavigationItem item = _items[index];
                    string container = item.ContainerName;
                    string suffix;
                    if (container.Length == 0)
                    {
                        suffix = string.Empty;
                    }
                    else if (oldContainer.Length == 0)
                    {
                        suffix = "::" + container;
                    }
                    else if (string.Equals(container, oldContainer, StringComparison.Ordinal))
                    {
                        suffix = string.Empty;
                    }
                    else if (container.StartsWith(oldContainer + "::", StringComparison.Ordinal))
                    {
                        suffix = container.Substring(oldContainer.Length);
                    }
                    else
                    {
                        continue;
                    }

                    item.ContainerName = newContainer + suffix;
                    item.FullName = item.ContainerName + "::" + item.Name;
                }
            }

            // -----------------------------------------------------------------------------------------
            // Scope body parsing
            // -----------------------------------------------------------------------------------------

            private void ParseTopLevel()
            {
                while (_i < _tokens.Count)
                {
                    CppToken token = Tok;
                    if (token.Kind == CppTokenKind.EndOfFile)
                    {
                        _i++;
                        return;
                    }

                    if (IsPunct(token, '}'))
                    {
                        // Stray closing brace at file scope.
                        _i++;
                        continue;
                    }

                    if (IsPunct(token, ';'))
                    {
                        _i++;
                        ResetPending();
                        continue;
                    }

                    int before = _i;
                    Step();
                    if (_i <= before)
                    {
                        _i = before + 1;
                    }
                }
            }

            private void ParseScopeBody()
            {
                while (_i < _tokens.Count)
                {
                    CppToken token = Tok;
                    if (token.Kind == CppTokenKind.EndOfFile)
                    {
                        return;
                    }

                    if (IsPunct(token, '}'))
                    {
                        _i++;
                        return;
                    }

                    if (IsPunct(token, ';'))
                    {
                        _i++;
                        ResetPending();
                        continue;
                    }

                    if (_items.Count >= MaxItems)
                    {
                        SkipToScopeEnd();
                        return;
                    }

                    int before = _i;
                    Step();
                    if (_i <= before)
                    {
                        _i = before + 1;
                    }
                }
            }

            private void Step()
            {
                CppToken token = Tok;
                if (token.Kind == CppTokenKind.EndOfFile)
                {
                    _i++;
                    return;
                }

                if (token.Kind == CppTokenKind.Macro)
                {
                    _i++;
                    return;
                }

                if (token.Kind == CppTokenKind.Punctuation)
                {
                    if (IsPunct(token, '{'))
                    {
                        // Stray block at declaration scope: skip it entirely.
                        _i++;
                        SkipBalancedBracesFromConsumed();
                        return;
                    }

                    if (IsPunct(token, '~') && Peek(1).Kind == CppTokenKind.Identifier)
                    {
                        // A leading destructor tilde must stay part of the declaration so that
                        // FindFunctionCandidate can classify it as a destructor.
                        ParseGeneralDeclaration();
                        return;
                    }

                    if (TrySkipAttribute())
                    {
                        return;
                    }

                    _i++;
                    return;
                }

                if (token.Kind != CppTokenKind.Identifier)
                {
                    _i++;
                    return;
                }

                string word = GetText(token);
                switch (word)
                {
                    case "namespace":
                        ParseNamespace();
                        return;
                    case "using":
                        ParseUsing();
                        return;
                    case "typedef":
                        _pendingTypedef = true;
                        _i++;
                        return;
                    case "template":
                        ParseTemplatePrefix();
                        return;
                    case "class":
                        ParseTypeDeclaration(ItemKind.Class);
                        return;
                    case "struct":
                        ParseTypeDeclaration(ItemKind.Struct);
                        return;
                    case "union":
                        ParseTypeDeclaration(ItemKind.Union);
                        return;
                    case "enum":
                        ParseTypeDeclaration(ItemKind.Enum);
                        return;
                    case "__interface":
                        ParseTypeDeclaration(ItemKind.Interface);
                        return;
                    case "concept":
                        ParseConcept();
                        return;
                    case "extern":
                        if (!TryParseExternCBlock())
                        {
                            ParseGeneralDeclaration();
                        }

                        return;
                    case "public":
                    case "private":
                    case "protected":
                        if (!TryParseAccessSpecifier())
                        {
                            ParseGeneralDeclaration();
                        }

                        return;
                    case "signals":
                    case "slots":
                    case "Q_SIGNALS":
                    case "Q_SLOTS":
                        if (!TryConsumeAccessLabel())
                        {
                            ParseGeneralDeclaration();
                        }

                        return;
                    case "friend":
                        SkipStatement();
                        return;
                    case "static_assert":
                        SkipStatement();
                        return;
                    case "asm":
                    case "__asm":
                        SkipStatement();
                        return;
                    case "inline":
                        if (IsWord(Peek(1), "namespace"))
                        {
                            _i++;
                            return;
                        }

                        ParseGeneralDeclaration();
                        return;
                    case "export":
                        if (IsWord(Peek(1), "namespace") || IsWord(Peek(1), "template"))
                        {
                            _i++;
                            return;
                        }

                        if (IsPunct(Peek(1), '{'))
                        {
                            _i++;
                            _i++;
                            SkipBalancedBracesFromConsumed();
                            return;
                        }

                        SkipStatement();
                        return;
                    case "module":
                    case "import":
                        SkipStatement();
                        return;
                    case "alignas":
                    case "__declspec":
                    case "__attribute__":
                    case "__pragma":
                    case "_Pragma":
                    case "__based":
                        SkipModifier(word);
                        return;
                    case "catch":
                        SkipCatchClause();
                        return;
                    case "try":
                        SkipStatement();
                        return;
                    default:
                        // A typedef's leading type may look like a macro (all caps, e.g. HRESULT);
                        // it must reach the declaration parser so the alias is extracted.
                        if (!_pendingTypedef && TryHandleMacroLikeIdentifier())
                        {
                            return;
                        }

                        ParseGeneralDeclaration();
                        return;
                }
            }

            /// <summary>Consumes tokens until the end of the current scope (used when the item budget is exhausted).</summary>
            private void SkipToScopeEnd()
            {
                int depth = 1;
                while (_i < _tokens.Count)
                {
                    CppToken token = Tok;
                    if (token.Kind == CppTokenKind.EndOfFile)
                    {
                        return;
                    }

                    if (IsPunct(token, '{'))
                    {
                        depth++;
                    }
                    else if (IsPunct(token, '}'))
                    {
                        depth--;
                        if (depth == 0)
                        {
                            _i++;
                            return;
                        }
                    }

                    _i++;
                }
            }

            // -----------------------------------------------------------------------------------------
            // Namespace / using / concept / access
            // -----------------------------------------------------------------------------------------

            private void ParseNamespace()
            {
                _i++;
                SkipAttributes();

                var tokens = new List<CppToken>();
                var names = new List<string>();
                while (Tok.Kind == CppTokenKind.Identifier)
                {
                    tokens.Add(Tok);
                    names.Add(GetText(Tok));
                    _i++;
                    if (IsPunct(Tok, "::"))
                    {
                        _i++;
                        continue;
                    }

                    break;
                }

                if (IsPunct(Tok, '='))
                {
                    // namespace alias = target;
                    if (names.Count > 0)
                    {
                        AddSymbol(
                            ItemKind.Namespace,
                            tokens[tokens.Count - 1],
                            names[names.Count - 1],
                            CurrentContainer,
                            null,
                            ItemFlags.Declaration,
                            string.Empty);
                    }

                    SkipStatement();
                    ResetPending();
                    return;
                }

                if (IsPunct(Tok, '{'))
                {
                    if (names.Count > 0)
                    {
                        string container = CurrentContainer;
                        string fullName = container.Length > 0
                            ? container + "::" + string.Join("::", names)
                            : string.Join("::", names);
                        AddSymbol(
                            ItemKind.Namespace,
                            tokens[tokens.Count - 1],
                            names[names.Count - 1],
                            container,
                            fullName,
                            ItemFlags.Definition,
                            string.Empty);
                        PushFrame(FrameKind.Namespace, names[names.Count - 1], fullName);
                    }
                    else
                    {
                        PushFrame(FrameKind.Namespace, string.Empty, string.Empty);
                    }

                    ResetPending();
                    _i++;
                    ParseScopeBody();
                    PopFrame();
                    return;
                }

                SkipStatement();
                ResetPending();
            }

            private void ParseUsing()
            {
                _i++; // using

                if (IsWord(Tok, "namespace") || IsWord(Tok, "enum"))
                {
                    SkipStatement();
                    ResetPending();
                    return;
                }

                if (Tok.Kind == CppTokenKind.Identifier && IsPunct(Peek(1), '='))
                {
                    CppToken nameToken = Tok;
                    string name = GetText(nameToken);
                    _i += 2; // identifier + '='
                    AddSymbol(
                        ItemKind.TypeAlias,
                        nameToken,
                        name,
                        CurrentContainer,
                        null,
                        ItemFlags.Declaration | PendingModifiers(),
                        string.Empty);

                    SkipStatement();
                    ResetPending();
                    return;
                }

                // using Base::member; / using Base::Base; / using Foo<T>; / malformed.
                SkipStatement();
                ResetPending();
            }

            private void ParseConcept()
            {
                _i++; // concept
                SkipAttributes();
                if (Tok.Kind == CppTokenKind.Identifier)
                {
                    CppToken nameToken = Tok;
                    string name = GetText(nameToken);
                    _i++;
                    AddSymbol(
                        ItemKind.Concept,
                        nameToken,
                        name,
                        CurrentContainer,
                        null,
                        ItemFlags.Declaration | PendingModifiers(),
                        string.Empty);
                }

                SkipStatement();
                ResetPending();
            }

            private bool TryParseAccessSpecifier()
            {
                ItemFlags flag;
                string word = GetText(Tok);
                if (word == "public")
                {
                    flag = ItemFlags.Public;
                }
                else if (word == "private")
                {
                    flag = ItemFlags.Private;
                }
                else
                {
                    flag = ItemFlags.Protected;
                }

                CppToken next = Peek(1);
                if (IsPunct(next, ':'))
                {
                    SetAccess(flag);
                    _i += 2;
                    return true;
                }

                // Qt-style "public slots:" / "protected Q_SLOTS:".
                CppToken next2 = Peek(2);
                if (next.Kind == CppTokenKind.Identifier && IsPunct(next2, ':'))
                {
                    SetAccess(flag);
                    _i += 3;
                    return true;
                }

                return false;
            }

            private bool TryConsumeAccessLabel()
            {
                CppToken next = Peek(1);
                if (IsPunct(next, '('))
                {
                    // Q_SIGNALS(...) style macro invocation, not a label.
                    return false;
                }

                if (IsPunct(next, ':'))
                {
                    _i += 2;
                    return true;
                }

                return false;
            }

            private void SetAccess(ItemFlags access)
            {
                for (int i = _scope.Count - 1; i >= 0; i--)
                {
                    if (_scope[i].Kind == FrameKind.Class)
                    {
                        _scope[i].Access = access;
                        return;
                    }
                }
            }

            // -----------------------------------------------------------------------------------------
            // extern "C" and modifiers
            // -----------------------------------------------------------------------------------------

            private bool TryParseExternCBlock()
            {
                CppToken next = Peek(1);
                if (IsWord(next, "template"))
                {
                    // extern template class Foo<int>;
                    SkipStatement();
                    return true;
                }

                if (next.Kind == CppTokenKind.StringLiteral)
                {
                    _i += 2;
                    if (IsPunct(Tok, '{'))
                    {
                        _i++;
                        PushFrame(FrameKind.ExternC, string.Empty, string.Empty);
                        ParseScopeBody();
                        PopFrame();
                    }

                    return true;
                }

                if (IsPunct(next, '{'))
                {
                    _i += 2;
                    PushFrame(FrameKind.ExternC, string.Empty, string.Empty);
                    ParseScopeBody();
                    PopFrame();
                    return true;
                }

                return false;
            }

            private void SkipModifier(string word)
            {
                _i++; // modifier keyword
                SkipAttributes();
                if (!IsPunct(Tok, '('))
                {
                    return;
                }

                int startIndex = _i;
                SkipBalancedParentheses();
                if (word != "__declspec" && word != "__attribute__")
                {
                    return;
                }

                for (int index = startIndex; index < _i && index < _tokens.Count; index++)
                {
                    CppToken token = _tokens[index];
                    if (IsWord(token, "dllexport") || IsWord(token, "visibility"))
                    {
                        _pendingExported = true;
                        return;
                    }
                }
            }

            private bool TrySkipAttribute()
            {
                if (!IsPunct(Tok, '[') || !IsPunct(Peek(1), '['))
                {
                    return false;
                }

                _i += 2;
                int depth = 2;
                while (_i < _tokens.Count)
                {
                    CppToken token = Tok;
                    if (token.Kind == CppTokenKind.EndOfFile)
                    {
                        return true;
                    }

                    if (IsPunct(token, '['))
                    {
                        depth++;
                    }
                    else if (IsPunct(token, ']'))
                    {
                        depth--;
                        if (depth == 0)
                        {
                            _i++;
                            return true;
                        }
                    }

                    _i++;
                }

                return true;
            }

            private void SkipAttributes()
            {
                while (TrySkipAttribute())
                {
                }
            }

            // -----------------------------------------------------------------------------------------
            // template<...> prefix
            // -----------------------------------------------------------------------------------------

            private void ParseTemplatePrefix()
            {
                _i++; // template
                if (!IsPunct(Tok, '<'))
                {
                    // "template" used as a macro name: do not carry the flag.
                    _pendingTemplate = false;
                    return;
                }

                _pendingTemplate = true;
                SkipBalancedAngles();
            }

            private void SkipBalancedAngles()
            {
                if (!IsPunct(Tok, '<'))
                {
                    return;
                }

                int depth = 0;
                while (_i < _tokens.Count)
                {
                    CppToken token = Tok;
                    if (token.Kind == CppTokenKind.EndOfFile)
                    {
                        return;
                    }

                    if (IsPunct(token, ';') || IsPunct(token, '{') || IsPunct(token, '}'))
                    {
                        return;
                    }

                    string text = GetText(token);
                    for (int character = 0; character < text.Length; character++)
                    {
                        if (text[character] == '<')
                        {
                            depth++;
                        }
                        else if (text[character] == '>')
                        {
                            depth--;
                        }
                    }

                    _i++;
                    if (depth <= 0)
                    {
                        return;
                    }
                }
            }

            // -----------------------------------------------------------------------------------------
            // Type declarations (class / struct / union / enum / interface)
            // -----------------------------------------------------------------------------------------

            private void ParseTypeDeclaration(ItemKind kind)
            {
                _i++;
                bool isEnum = kind == ItemKind.Enum;

                if (isEnum && (IsWord(Tok, "class") || IsWord(Tok, "struct")))
                {
                    _i++;
                }

                var identifiers = new List<CppToken>();
                var qualifierParts = new List<string>();

                while (_i < _tokens.Count)
                {
                    CppToken token = Tok;
                    if (token.Kind == CppTokenKind.EndOfFile)
                    {
                        break;
                    }

                    if (IsPunct(token, '{') || IsPunct(token, ';') || IsPunct(token, ':'))
                    {
                        break;
                    }

                    if (token.Kind == CppTokenKind.Macro)
                    {
                        _i++;
                        continue;
                    }

                    if (IsPunct(token, '[') && IsPunct(Peek(1), '['))
                    {
                        SkipAttributes();
                        continue;
                    }

                    if (token.Kind == CppTokenKind.Identifier)
                    {
                        string word = GetText(token);
                        if (word == "alignas" || word == "__declspec" || word == "__attribute__" ||
                            word == "__pragma" || word == "_Pragma")
                        {
                            SkipModifier(word);
                            continue;
                        }

                        if (word == "final" || word == "sealed" || word == "abstract" || word == "requires")
                        {
                            _i++;
                            continue;
                        }

                        if (IsPunct(Peek(1), '('))
                        {
                            // attribute macro: CLASS_API(...)
                            _i++;
                            SkipBalancedParentheses();
                            continue;
                        }

                        if (IsPunct(Peek(1), "::"))
                        {
                            qualifierParts.Add(word);
                            _i += 2;
                            continue;
                        }

                        identifiers.Add(token);
                        _i++;
                        continue;
                    }

                    if (IsPunct(token, '<'))
                    {
                        // Specialization arguments: struct hash<Foo>.
                        SkipBalancedAngles();
                        continue;
                    }

                    _i++;
                }

                bool hasBaseClause = IsPunct(Tok, ':');
                if (hasBaseClause)
                {
                    SkipUntilBodyOrSemicolon();
                }

                bool hasBody = IsPunct(Tok, '{');
                bool isForward = IsPunct(Tok, ';');
                bool pendingTypedef = _pendingTypedef;

                int nameIndex = DetermineTypeNameIndex(identifiers, hasBody || hasBaseClause, isForward, pendingTypedef);
                var declarators = new List<CppToken>();
                if (nameIndex >= 0 && isForward)
                {
                    for (int index = nameIndex + 1; index < identifiers.Count; index++)
                    {
                        declarators.Add(identifiers[index]);
                    }
                }

                string container = ResolveContainer(string.Join("::", qualifierParts));
                string name = nameIndex >= 0 ? GetText(identifiers[nameIndex]) : string.Empty;
                string fullName = nameIndex >= 0 ? MakeFullName(container, name) : string.Empty;
                ItemFlags modifiers = PendingModifiers();

                if (hasBody)
                {
                    EmitTypeBody(
                        kind,
                        isEnum,
                        identifiers,
                        nameIndex,
                        name,
                        container,
                        fullName,
                        modifiers,
                        pendingTypedef);
                    return;
                }

                ResetPending();

                if (nameIndex >= 0)
                {
                    AddSymbol(
                        kind,
                        identifiers[nameIndex],
                        name,
                        container,
                        fullName,
                        ItemFlags.Declaration | modifiers,
                        string.Empty);

                    if (pendingTypedef)
                    {
                        for (int index = nameIndex + 1; index < identifiers.Count; index++)
                        {
                            CppToken aliasToken = identifiers[index];
                            AddSymbol(
                                ItemKind.TypeAlias,
                                aliasToken,
                                GetText(aliasToken),
                                CurrentContainer,
                                null,
                                ItemFlags.Declaration,
                                string.Empty);
                        }
                    }
                    else
                    {
                        for (int index = 0; index < declarators.Count; index++)
                        {
                            AddVariableItem(declarators[index], ItemFlags.Declaration, false);
                        }
                    }
                }
                else if (pendingTypedef)
                {
                    for (int index = 0; index < identifiers.Count; index++)
                    {
                        CppToken aliasToken = identifiers[index];
                        AddSymbol(
                            ItemKind.TypeAlias,
                            aliasToken,
                            GetText(aliasToken),
                            CurrentContainer,
                            null,
                            ItemFlags.Declaration,
                            string.Empty);
                    }
                }

                if (isForward)
                {
                    _i++;
                }
            }

            private int DetermineTypeNameIndex(
                List<CppToken> identifiers,
                bool hasBodyOrBase,
                bool isForward,
                bool pendingTypedef)
            {
                if (identifiers.Count == 0)
                {
                    return -1;
                }

                if (hasBodyOrBase || !isForward)
                {
                    return identifiers.Count - 1;
                }

                if (pendingTypedef)
                {
                    // typedef struct Tag Alias; -> Tag is the type name, remaining identifiers are aliases.
                    if (identifiers.Count == 1)
                    {
                        return 0;
                    }

                    for (int index = 0; index < identifiers.Count - 1; index++)
                    {
                        if (!IsMacroLike(identifiers[index], GetText(identifiers[index])))
                        {
                            return index;
                        }
                    }

                    return 0;
                }

                if (identifiers.Count == 1)
                {
                    return 0;
                }

                if (identifiers.Count == 2)
                {
                    bool firstMacro = IsMacroLike(identifiers[0], GetText(identifiers[0]));
                    bool secondMacro = IsMacroLike(identifiers[1], GetText(identifiers[1]));
                    return firstMacro && !secondMacro ? 1 : 0;
                }

                for (int index = 0; index < identifiers.Count; index++)
                {
                    if (!IsMacroLike(identifiers[index], GetText(identifiers[index])))
                    {
                        return index;
                    }
                }

                return identifiers.Count - 1;
            }

            private void EmitTypeBody(
                ItemKind kind,
                bool isEnum,
                List<CppToken> identifiers,
                int nameIndex,
                string name,
                string container,
                string fullName,
                ItemFlags modifiers,
                bool pendingTypedef)
            {
                bool hasName = nameIndex >= 0;
                string parentContainer = CurrentContainer;
                if (hasName)
                {
                    AddSymbol(
                        kind,
                        identifiers[nameIndex],
                        name,
                        container,
                        fullName,
                        ItemFlags.Definition | modifiers,
                        string.Empty);
                }

                // Template/export apply to the type itself, not to its members.
                _pendingTemplate = false;
                _pendingExported = false;
                _pendingTypedef = false;
                _i++; // '{'
                int bodyItemStart = _items.Count;

                if (isEnum)
                {
                    PushFrame(FrameKind.Enum, hasName ? name : string.Empty, hasName ? fullName : string.Empty);
                    ParseEnumBody();
                    PopFrame();
                }
                else
                {
                    ScopeFrame frame = PushFrame(
                        FrameKind.Class,
                        hasName ? name : string.Empty,
                        hasName ? fullName : string.Empty);
                    frame.Access = kind == ItemKind.Struct || kind == ItemKind.Union || kind == ItemKind.Interface
                        ? ItemFlags.Public
                        : ItemFlags.Private;
                    ParseScopeBody();
                    PopFrame();
                }

                int bodyItemEnd = _items.Count;

                var declaratorTokens = new List<CppToken>();
                CollectPostBodyDeclarators(declaratorTokens);
                ResetPending();

                if (pendingTypedef)
                {
                    if (declaratorTokens.Count > 0)
                    {
                        if (!hasName)
                        {
                            string aliasContainer = MakeFullName(parentContainer, GetText(declaratorTokens[0]));
                            ReparentItems(bodyItemStart, bodyItemEnd, parentContainer, aliasContainer);
                        }

                        for (int index = 0; index < declaratorTokens.Count; index++)
                        {
                            CppToken aliasToken = declaratorTokens[index];
                            AddSymbol(
                                ItemKind.TypeAlias,
                                aliasToken,
                                GetText(aliasToken),
                                parentContainer,
                                null,
                                ItemFlags.Declaration,
                                string.Empty);
                        }
                    }

                    return;
                }

                if (declaratorTokens.Count > 0)
                {
                    if (!hasName)
                    {
                        string variableContainer = MakeFullName(parentContainer, GetText(declaratorTokens[0]));
                        ReparentItems(bodyItemStart, bodyItemEnd, parentContainer, variableContainer);
                    }

                    for (int index = 0; index < declaratorTokens.Count; index++)
                    {
                        AddVariableItem(declaratorTokens[index], ItemFlags.Definition, false);
                    }
                }
            }

            private void CollectPostBodyDeclarators(List<CppToken> tokens)
            {
                int paren = 0;
                int bracket = 0;
                int brace = 0;
                bool inInitializer = false;

                while (_i < _tokens.Count)
                {
                    CppToken token = Tok;
                    if (token.Kind == CppTokenKind.EndOfFile)
                    {
                        return;
                    }

                    bool atTop = paren == 0 && bracket == 0 && brace == 0;
                    if (atTop)
                    {
                        if (IsPunct(token, ';'))
                        {
                            _i++;
                            return;
                        }

                        if (IsPunct(token, '}'))
                        {
                            return;
                        }

                        if (IsPunct(token, '='))
                        {
                            inInitializer = true;
                            _i++;
                            continue;
                        }

                        if (IsPunct(token, ','))
                        {
                            inInitializer = false;
                            _i++;
                            continue;
                        }

                        if (!inInitializer && IsPunct(token, '(') && IsPunct(Peek(1), '*') &&
                            Peek(2).Kind == CppTokenKind.Identifier && IsPunct(Peek(3), ')'))
                        {
                            tokens.Add(Peek(2));
                            _i += 4;
                            continue;
                        }

                        if (!inInitializer && token.Kind == CppTokenKind.Identifier)
                        {
                            string word = GetText(token);
                            if (!IsReservedWord(word))
                            {
                                if (IsPunct(Peek(1), '('))
                                {
                                    _i++;
                                    SkipBalancedParentheses();
                                    continue;
                                }

                                if (!IsMacroLike(token, word))
                                {
                                    tokens.Add(token);
                                }
                            }

                            _i++;
                            continue;
                        }
                    }

                    if (IsPunct(token, '('))
                    {
                        paren++;
                    }
                    else if (IsPunct(token, ')'))
                    {
                        if (paren > 0)
                        {
                            paren--;
                        }
                    }
                    else if (IsPunct(token, '['))
                    {
                        bracket++;
                    }
                    else if (IsPunct(token, ']'))
                    {
                        if (bracket > 0)
                        {
                            bracket--;
                        }
                    }
                    else if (IsPunct(token, '{'))
                    {
                        brace++;
                    }
                    else if (IsPunct(token, '}'))
                    {
                        if (brace > 0)
                        {
                            brace--;
                        }
                    }

                    _i++;
                }
            }

            private void ParseEnumBody()
            {
                while (_i < _tokens.Count)
                {
                    CppToken token = Tok;
                    if (token.Kind == CppTokenKind.EndOfFile)
                    {
                        return;
                    }

                    if (IsPunct(token, '}'))
                    {
                        _i++;
                        return;
                    }

                    if (IsPunct(token, ';'))
                    {
                        _i++;
                        continue;
                    }

                    if (IsPunct(token, ','))
                    {
                        _i++;
                        continue;
                    }

                    if (TrySkipAttribute())
                    {
                        continue;
                    }

                    if (token.Kind == CppTokenKind.Identifier)
                    {
                        CppToken nameToken = token;
                        _i++;
                        AddSymbol(
                            ItemKind.EnumMember,
                            nameToken,
                            GetText(nameToken),
                            CurrentContainer,
                            null,
                            ItemFlags.Definition,
                            string.Empty);
                    }

                    SkipUntilEnumeratorSeparator();
                }
            }

            private void SkipUntilEnumeratorSeparator()
            {
                int paren = 0;
                int bracket = 0;
                int brace = 0;
                int angle = 0;
                CppToken previous = EofToken;

                while (_i < _tokens.Count)
                {
                    CppToken token = Tok;
                    if (token.Kind == CppTokenKind.EndOfFile)
                    {
                        return;
                    }

                    if (paren == 0 && bracket == 0 && brace == 0 && angle == 0)
                    {
                        if (IsPunct(token, ',') || IsPunct(token, '}'))
                        {
                            return;
                        }

                        if (IsPunct(token, ';'))
                        {
                            _i++;
                            return;
                        }
                    }

                    if (IsPunct(token, '('))
                    {
                        paren++;
                    }
                    else if (IsPunct(token, ')'))
                    {
                        if (paren > 0)
                        {
                            paren--;
                        }
                    }
                    else if (IsPunct(token, '['))
                    {
                        bracket++;
                    }
                    else if (IsPunct(token, ']'))
                    {
                        if (bracket > 0)
                        {
                            bracket--;
                        }
                    }
                    else if (IsPunct(token, '{'))
                    {
                        brace++;
                    }
                    else if (IsPunct(token, '}'))
                    {
                        if (brace > 0)
                        {
                            brace--;
                        }
                    }
                    else if (IsPunct(token, '<') && previous.Kind == CppTokenKind.Identifier)
                    {
                        angle++;
                    }
                    else if (IsAngleClose(token) && angle > 0)
                    {
                        angle--;
                    }

                    previous = token;
                    _i++;
                }
            }

            // -----------------------------------------------------------------------------------------
            // General declarations: functions and variables
            // -----------------------------------------------------------------------------------------

            private void ParseGeneralDeclaration()
            {
                int endKind = CollectDeclaration(out List<int> indices, out int topLevelEqualsListPos);
                bool pendingTypedef = _pendingTypedef;
                ItemFlags modifiers = PendingModifiers();
                FunctionCandidate candidate = FindFunctionCandidate(indices, topLevelEqualsListPos);

                if (pendingTypedef)
                {
                    // typedef declarations never create functions/variables, only type aliases.
                    if (candidate.Found)
                    {
                        AddSymbol(
                            ItemKind.TypeAlias,
                            _tokens[candidate.NameTokenIndex],
                            candidate.Name,
                            ResolveContainer(candidate.Qualifier),
                            null,
                            ItemFlags.Declaration | modifiers,
                            BuildSignature(candidate.ParamsOpenTokenIndex));
                    }
                    else
                    {
                        EmitTypedefAliases(indices, modifiers);
                    }

                    SkipFunctionBodyIfAny(endKind);
                    ResetPending();
                    return;
                }

                if (candidate.Found)
                {
                    bool macroInvocation = candidate.NameListPos == 0 && !InAnyClassScope &&
                                           !IsProbablyFunctionName(candidate.Name);
                    if (macroInvocation)
                    {
                        SkipFunctionBodyIfAny(endKind);
                        ResetPending();
                        return;
                    }

                    string simpleName = candidate.Name;
                    bool isDestructor = candidate.IsDestructor ||
                                        simpleName.StartsWith("~", StringComparison.Ordinal);
                    if (simpleName.StartsWith("~", StringComparison.Ordinal))
                    {
                        simpleName = simpleName.Substring(1);
                    }
                    bool isOperator = candidate.IsOperator;
                    bool inClass = InAnyClassScope;
                    string container = ResolveContainer(candidate.Qualifier);
                    string currentClass = CurrentClassSimpleName;

                    bool isConstructor = false;
                    if (!isDestructor && !isOperator)
                    {
                        if (candidate.Qualifier.Length == 0)
                        {
                            isConstructor = inClass && currentClass.Length > 0 &&
                                            string.Equals(simpleName, currentClass, StringComparison.Ordinal);
                        }
                        else
                        {
                            string qualifierTail = candidate.Qualifier;
                            int separator = qualifierTail.LastIndexOf("::", StringComparison.Ordinal);
                            if (separator >= 0)
                            {
                                qualifierTail = qualifierTail.Substring(separator + 2);
                            }

                            isConstructor = string.Equals(qualifierTail, simpleName, StringComparison.Ordinal);
                        }
                    }

                    ItemKind functionKind;
                    if (isOperator)
                    {
                        functionKind = ItemKind.Operator;
                    }
                    else if (isDestructor)
                    {
                        functionKind = ItemKind.Destructor;
                    }
                    else if (isConstructor)
                    {
                        functionKind = ItemKind.Constructor;
                    }
                    else if (inClass || candidate.Qualifier.Length > 0)
                    {
                        functionKind = ItemKind.Method;
                    }
                    else
                    {
                        functionKind = ItemKind.Function;
                    }

                    ItemFlags flags = endKind == 2 ? ItemFlags.Definition : ItemFlags.Declaration;
                    if (ListContainsWord(indices, "static"))
                    {
                        flags |= ItemFlags.Static;
                    }

                    if (ListContainsWord(indices, "dllexport"))
                    {
                        flags |= ItemFlags.Exported;
                    }

                    AddSymbol(
                        functionKind,
                        _tokens[candidate.NameTokenIndex],
                        simpleName,
                        container,
                        null,
                        flags | modifiers,
                        BuildSignature(candidate.ParamsOpenTokenIndex));

                    SkipFunctionBodyIfAny(endKind);
                    ResetPending();
                    return;
                }

                // Reject obvious macro calls: lower-case foo(...) at declaration scope with no return type
                // is almost always a macro invocation or a function-style variable initializer.
                if (indices.Count >= 2 && _tokens[indices[0]].Kind == CppTokenKind.Identifier &&
                    IsPunct(_tokens[indices[1]], '(') &&
                    !IsFunctionPointerDeclarator(indices, 1) &&
                    !(InAnyClassScope &&
                      string.Equals(GetText(_tokens[indices[0]]), CurrentClassSimpleName, StringComparison.Ordinal)))
                {
                    SkipFunctionBodyIfAny(endKind);
                    ResetPending();
                    return;
                }

                EmitVariables(indices, endKind, modifiers);
                SkipFunctionBodyIfAny(endKind);
                ResetPending();
            }

            private void SkipFunctionBodyIfAny(int endKind)
            {
                if (endKind != 2)
                {
                    return;
                }

                if (IsPunct(Tok, '{'))
                {
                    _i++;
                    SkipBalancedBracesFromConsumed();
                }

                SkipTrailingFunctionClauses();
            }

            /// <summary>
            /// Collects the tokens of one declaration statement.
            /// </summary>
            /// <returns>
            /// 1 when the statement ended with <c>;</c>, 2 when a function body <c>{</c> starts at
            /// <c>_i</c>, 0 when EOF/scope end was reached.
            /// </returns>
            private int CollectDeclaration(out List<int> indices, out int topLevelEqualsListPos)
            {
                indices = new List<int>(16);
                topLevelEqualsListPos = -1;
                int j = _i;
                int paren = 0;
                int bracket = 0;
                int endKind = 0;

                while (j < _tokens.Count)
                {
                    CppToken token = _tokens[j];
                    if (token.Kind == CppTokenKind.EndOfFile)
                    {
                        break;
                    }

                    bool atTop = paren == 0 && bracket == 0;
                    if (atTop)
                    {
                        if (token.Kind == CppTokenKind.StringLiteral && !IsTerminatedStringLiteral(token))
                        {
                            // The lexer stopped a broken string at the end of its line. End the
                            // declaration here as well so following lines are parsed normally.
                            endKind = 1;
                            break;
                        }

                        if (IsPunct(token, ';'))
                        {
                            endKind = 1;
                            break;
                        }

                        if (IsPunct(token, '}'))
                        {
                            break;
                        }

                        if (IsPunct(token, '{'))
                        {
                            bool functionLike = QuickHasFunctionCandidate(indices);
                            bool equalsSeen = topLevelEqualsListPos >= 0;
                            bool ctorInitializerList = HasTopLevelColon(indices);
                            if (!functionLike || equalsSeen)
                            {
                                j = SkipBracesAt(j);
                                continue;
                            }

                            if (ctorInitializerList && !IsCtorBodyBrace(j))
                            {
                                j = SkipBracesAt(j);
                                continue;
                            }

                            endKind = 2;
                            break;
                        }
                    }

                    if (atTop && IsPunct(token, '='))
                    {
                        if (topLevelEqualsListPos < 0)
                        {
                            topLevelEqualsListPos = indices.Count;
                        }
                    }

                    if (IsPunct(token, '('))
                    {
                        paren++;
                    }
                    else if (IsPunct(token, ')'))
                    {
                        if (paren > 0)
                        {
                            paren--;
                        }
                    }
                    else if (IsPunct(token, '['))
                    {
                        bracket++;
                    }
                    else if (IsPunct(token, ']'))
                    {
                        if (bracket > 0)
                        {
                            bracket--;
                        }
                    }

                    indices.Add(j);
                    j++;
                }

                if (endKind == 1)
                {
                    _i = j + 1;
                }
                else if (endKind == 2)
                {
                    _i = j;
                }
                else
                {
                    _i = j;
                }

                return endKind;
            }

            /// <summary>True when the brace at <paramref name="braceIndex"/> starts a function body in a ctor-init list.</summary>
            private bool IsCtorBodyBrace(int braceIndex)
            {
                int afterBody = SkipBracesAt(braceIndex);
                if (afterBody >= _tokens.Count)
                {
                    return true;
                }

                CppToken token = _tokens[afterBody];
                if (IsPunct(token, ',') || IsPunct(token, '{'))
                {
                    return false;
                }

                return true;
            }

            private bool HasTopLevelColon(List<int> indices)
            {
                int paren = 0;
                int bracket = 0;
                for (int index = 0; index < indices.Count; index++)
                {
                    CppToken token = _tokens[indices[index]];
                    if (paren == 0 && bracket == 0 && IsPunct(token, ':'))
                    {
                        return true;
                    }

                    if (IsPunct(token, '('))
                    {
                        paren++;
                    }
                    else if (IsPunct(token, ')'))
                    {
                        if (paren > 0)
                        {
                            paren--;
                        }
                    }
                    else if (IsPunct(token, '['))
                    {
                        bracket++;
                    }
                    else if (IsPunct(token, ']'))
                    {
                        if (bracket > 0)
                        {
                            bracket--;
                        }
                    }
                }

                return false;
            }

            /// <summary>
            /// Detects a parenthesized declarator such as <c>(MESSY_CALL *CALLBACK)</c> or
            /// <c>(Foo::*member)</c>, which must not be mistaken for a parameter list.
            /// </summary>
            private bool IsFunctionPointerDeclarator(List<int> indices, int openListPos)
            {
                int depth = 0;
                int closeListPos = -1;
                for (int pos = openListPos; pos < indices.Count; pos++)
                {
                    CppToken token = _tokens[indices[pos]];
                    if (IsPunct(token, '('))
                    {
                        depth++;
                    }
                    else if (IsPunct(token, ')'))
                    {
                        depth--;
                        if (depth == 0)
                        {
                            closeListPos = pos;
                            break;
                        }
                    }
                }

                if (closeListPos < 0 || closeListPos + 1 >= indices.Count)
                {
                    return false;
                }

                CppToken after = _tokens[indices[closeListPos + 1]];
                if (!IsPunct(after, '(') && !IsPunct(after, '['))
                {
                    return false;
                }

                for (int pos = openListPos + 1; pos < closeListPos; pos++)
                {
                    if (IsPunct(_tokens[indices[pos]], '*'))
                    {
                        return true;
                    }
                }

                return false;
            }

            private bool QuickHasFunctionCandidate(List<int> indices)
            {
                int depth = 0;
                for (int pos = 0; pos < indices.Count; pos++)
                {
                    CppToken token = _tokens[indices[pos]];
                    if (depth == 0 && token.Kind == CppTokenKind.Identifier && IsWord(token, "operator"))
                    {
                        return true;
                    }

                    if (IsPunct(token, '('))
                    {
                        if (depth == 0 && pos > 0)
                        {
                            CppToken previous = _tokens[indices[pos - 1]];
                            if (previous.Kind == CppTokenKind.Identifier && !IsReservedWord(GetText(previous)) &&
                                !IsFunctionPointerDeclarator(indices, pos))
                            {
                                CppToken next = pos + 1 < indices.Count ? _tokens[indices[pos + 1]] : EofToken;
                                if (!IsPunct(next, '*') && !IsPunct(next, '&'))
                                {
                                    return true;
                                }
                            }
                        }

                        depth++;
                    }
                    else if (IsPunct(token, ')'))
                    {
                        if (depth > 0)
                        {
                            depth--;
                        }
                    }
                }

                return false;
            }

            private FunctionCandidate FindFunctionCandidate(List<int> indices, int topLevelEqualsListPos)
            {
                var result = default(FunctionCandidate);
                int depth = 0;

                for (int pos = 0; pos < indices.Count; pos++)
                {
                    CppToken token = _tokens[indices[pos]];
                    if (depth == 0 && token.Kind == CppTokenKind.Identifier && IsWord(token, "operator"))
                    {
                        if (TryResolveOperator(indices, pos, topLevelEqualsListPos, out result))
                        {
                            return result;
                        }

                        continue;
                    }

                    if (IsPunct(token, '('))
                    {
                        if (depth == 0 && pos > 0 && (topLevelEqualsListPos < 0 || pos < topLevelEqualsListPos))
                        {
                            CppToken previous = _tokens[indices[pos - 1]];
                            if (previous.Kind == CppTokenKind.Identifier && !IsReservedWord(GetText(previous)) &&
                                !IsFunctionPointerDeclarator(indices, pos))
                            {
                                CppToken firstParameter = pos + 1 < indices.Count ? _tokens[indices[pos + 1]] : EofToken;
                                if (!IsPunct(firstParameter, '*') && !IsPunct(firstParameter, '&'))
                                {
                                    int nameListPos = pos - 1;
                                    result.Found = true;
                                    result.NameListPos = nameListPos;
                                    result.NameTokenIndex = indices[nameListPos];
                                    result.ParamsOpenTokenIndex = indices[pos];
                                    result.Name = GetText(previous);
                                    result.IsDestructor = nameListPos > 0 &&
                                                          IsPunct(_tokens[indices[nameListPos - 1]], '~');
                                    result.Qualifier = CollectQualifier(indices, nameListPos);
                                    return result;
                                }
                            }
                        }

                        depth++;
                    }
                    else if (IsPunct(token, ')'))
                    {
                        if (depth > 0)
                        {
                            depth--;
                        }
                    }
                }

                return result;
            }

            private bool TryResolveOperator(
                List<int> indices,
                int operatorListPos,
                int topLevelEqualsListPos,
                out FunctionCandidate result)
            {
                result = default(FunctionCandidate);
                if (topLevelEqualsListPos >= 0 && operatorListPos > topLevelEqualsListPos)
                {
                    return false;
                }

                int pos = operatorListPos + 1;
                var nameBuilder = new System.Text.StringBuilder("operator");
                int paramsOpenListPos = -1;
                string name;

                if (pos < indices.Count && IsPunct(_tokens[indices[pos]], '(') && pos + 1 < indices.Count &&
                    IsPunct(_tokens[indices[pos + 1]], ')'))
                {
                    nameBuilder.Append("()");
                    paramsOpenListPos = pos + 2;
                    name = nameBuilder.ToString();
                }
                else if (pos < indices.Count && IsPunct(_tokens[indices[pos]], '[') && pos + 1 < indices.Count &&
                         IsPunct(_tokens[indices[pos + 1]], ']'))
                {
                    nameBuilder.Append("[]");
                    paramsOpenListPos = pos + 2;
                    name = nameBuilder.ToString();
                }
                else
                {
                    int scan = pos;
                    int guard = 0;
                    bool foundParameterList = false;
                    while (scan < indices.Count && guard < 8)
                    {
                        CppToken token = _tokens[indices[scan]];
                        if (IsPunct(token, '('))
                        {
                            foundParameterList = true;
                            break;
                        }

                        if (IsPunct(token, ';') || IsPunct(token, '{') || IsPunct(token, '}'))
                        {
                            return false;
                        }

                        string text = GetText(token);
                        if (text.Length == 0)
                        {
                            return false;
                        }

                        if (token.Kind == CppTokenKind.Identifier || token.Kind == CppTokenKind.Number)
                        {
                            char previous = nameBuilder.Length > 0 ? nameBuilder[nameBuilder.Length - 1] : ' ';
                            if ((previous >= 'a' && previous <= 'z') || (previous >= 'A' && previous <= 'Z') ||
                                (previous >= '0' && previous <= '9') || previous == '_')
                            {
                                nameBuilder.Append(' ');
                            }

                            nameBuilder.Append(text);
                        }
                        else
                        {
                            nameBuilder.Append(text);
                        }

                        scan++;
                        guard++;
                    }

                    if (!foundParameterList || scan >= indices.Count)
                    {
                        return false;
                    }

                    paramsOpenListPos = scan;
                    name = nameBuilder.ToString();
                }

                int nameListPos = operatorListPos;
                var qualifier = CollectQualifier(indices, nameListPos);

                int paramsTokenIndex = -1;
                if (paramsOpenListPos >= 0 && paramsOpenListPos < indices.Count &&
                    IsPunct(_tokens[indices[paramsOpenListPos]], '('))
                {
                    paramsTokenIndex = indices[paramsOpenListPos];
                }

                result.Found = true;
                result.NameListPos = nameListPos;
                result.NameTokenIndex = indices[operatorListPos];
                result.ParamsOpenTokenIndex = paramsTokenIndex;
                result.Name = name;
                result.Qualifier = qualifier;
                result.IsOperator = true;
                result.IsDestructor = false;
                return true;
            }

            private string CollectQualifier(List<int> indices, int nameListPos)
            {
                var parts = new List<string>();
                int pos = nameListPos - 1;
                if (pos >= 0 && IsPunct(_tokens[indices[pos]], '~'))
                {
                    pos--;
                }

                while (pos >= 1)
                {
                    if (!IsPunct(_tokens[indices[pos]], "::"))
                    {
                        break;
                    }

                    int identifierPos = pos - 1;
                    CppToken identifier = _tokens[indices[identifierPos]];
                    if (identifier.Kind == CppTokenKind.Identifier && !IsReservedWord(GetText(identifier)))
                    {
                        parts.Insert(0, GetText(identifier));
                        pos = identifierPos - 1;
                        continue;
                    }

                    if (IsAngleClose(identifier))
                    {
                        int openPos = FindMatchingAngleOpen(indices, identifierPos);
                        if (openPos < 0)
                        {
                            break;
                        }

                        int partStartPos = openPos;
                        if (partStartPos > 0 && _tokens[indices[partStartPos - 1]].Kind == CppTokenKind.Identifier)
                        {
                            partStartPos--;
                        }

                        int startOffset = _tokens[indices[partStartPos]].Start;
                        int endOffset = _tokens[indices[identifierPos]].End;
                        if (startOffset >= 0 && endOffset <= _content.Length && endOffset > startOffset)
                        {
                            parts.Insert(0, NormalizeWhitespace(_content.Substring(startOffset, endOffset - startOffset)));
                        }

                        pos = partStartPos - 1;
                        continue;
                    }

                    break;
                }

                return parts.Count == 0 ? string.Empty : string.Join("::", parts);
            }

            private int FindMatchingAngleOpen(List<int> indices, int closePos)
            {
                int closes = 0;
                for (int pos = closePos; pos >= 0; pos--)
                {
                    string text = GetText(_tokens[indices[pos]]);
                    for (int character = text.Length - 1; character >= 0; character--)
                    {
                        if (text[character] == '>')
                        {
                            closes++;
                        }
                        else if (text[character] == '<')
                        {
                            closes--;
                            if (closes == 0)
                            {
                                return pos;
                            }
                        }
                    }
                }

                return -1;
            }

            private static bool IsProbablyFunctionName(string name)
            {
                if (string.IsNullOrEmpty(name))
                {
                    return false;
                }

                for (int index = 0; index < name.Length; index++)
                {
                    char c = name[index];
                    if (c >= 'A' && c <= 'Z')
                    {
                        return true;
                    }
                }

                return false;
            }

            private string BuildSignature(int paramsOpenTokenIndex)
            {
                if (paramsOpenTokenIndex < 0 || paramsOpenTokenIndex >= _tokens.Count)
                {
                    return string.Empty;
                }

                int depth = 0;
                int endOffset = -1;
                for (int index = paramsOpenTokenIndex; index < _tokens.Count; index++)
                {
                    CppToken token = _tokens[index];
                    if (token.Kind == CppTokenKind.EndOfFile)
                    {
                        break;
                    }

                    if (IsPunct(token, '('))
                    {
                        depth++;
                    }
                    else if (IsPunct(token, ')'))
                    {
                        depth--;
                        if (depth == 0)
                        {
                            endOffset = token.End;
                            break;
                        }
                    }
                }

                int startOffset = _tokens[paramsOpenTokenIndex].Start;
                if (startOffset < 0 || startOffset >= _content.Length)
                {
                    return string.Empty;
                }

                if (endOffset < 0)
                {
                    endOffset = Math.Min(_content.Length, startOffset + MaxSignatureLength);
                }

                int length = Math.Min(endOffset - startOffset, MaxSignatureLength * 2);
                if (length <= 0)
                {
                    return string.Empty;
                }

                return NormalizeWhitespace(_content.Substring(startOffset, length));
            }

            private static string NormalizeWhitespace(string value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return string.Empty;
                }

                var builder = new System.Text.StringBuilder(Math.Min(value.Length, MaxSignatureLength));
                bool pendingSpace = false;
                for (int index = 0; index < value.Length; index++)
                {
                    char c = value[index];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                    {
                        pendingSpace = builder.Length > 0;
                        continue;
                    }

                    if (pendingSpace)
                    {
                        builder.Append(' ');
                        pendingSpace = false;
                    }

                    builder.Append(c);
                    if (builder.Length >= MaxSignatureLength)
                    {
                        break;
                    }
                }

                return builder.ToString();
            }

            private bool ListContainsWord(List<int> indices, string word)
            {
                for (int index = 0; index < indices.Count; index++)
                {
                    if (IsWord(_tokens[indices[index]], word))
                    {
                        return true;
                    }
                }

                return false;
            }

            private void EmitTypedefAliases(List<int> indices, ItemFlags modifiers)
            {
                var segments = SplitDeclarationSegments(indices);
                for (int segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
                {
                    int tokenIndex;
                    string name;
                    if (!TryFindVariableName(segments[segmentIndex], out tokenIndex, out name))
                    {
                        continue;
                    }

                    AddSymbol(
                        ItemKind.TypeAlias,
                        _tokens[tokenIndex],
                        name,
                        CurrentContainer,
                        null,
                        ItemFlags.Declaration | modifiers,
                        string.Empty);
                }
            }

            private void EmitVariables(List<int> indices, int endKind, ItemFlags modifiers)
            {
                var segments = SplitDeclarationSegments(indices);
                bool isStatic = ListContainsWord(indices, "static");
                bool isExtern = ListContainsWord(indices, "extern");
                ItemKind variableKind = InAnyClassScope ? ItemKind.Field : ItemKind.GlobalVariable;

                for (int segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
                {
                    int tokenIndex;
                    string name;
                    if (!TryFindVariableName(segments[segmentIndex], out tokenIndex, out name))
                    {
                        continue;
                    }

                    CppToken nameToken = _tokens[tokenIndex];
                    bool hasInitializer = SegmentHasTopLevelEquals(segments[segmentIndex]);
                    ItemFlags flags = isExtern && !hasInitializer ? ItemFlags.Declaration : ItemFlags.Definition;
                    if (isStatic)
                    {
                        flags |= ItemFlags.Static;
                    }

                    if (ListContainsWord(indices, "dllexport"))
                    {
                        flags |= ItemFlags.Exported;
                    }

                    AddSymbol(variableKind, nameToken, name, CurrentContainer, null, flags | modifiers, string.Empty);
                }
            }

            private void AddVariableItem(CppToken nameToken, ItemFlags flags, bool isStatic)
            {
                string name = GetText(nameToken);
                if (string.IsNullOrEmpty(name) || IsReservedWord(name))
                {
                    return;
                }

                ItemKind kind = InAnyClassScope ? ItemKind.Field : ItemKind.GlobalVariable;
                ItemFlags effective = flags;
                if (isStatic)
                {
                    effective |= ItemFlags.Static;
                }

                AddSymbol(kind, nameToken, name, CurrentContainer, null, effective, string.Empty);
            }

            private bool SegmentHasTopLevelEquals(List<int> segment)
            {
                int paren = 0;
                int bracket = 0;
                for (int index = 0; index < segment.Count; index++)
                {
                    CppToken token = _tokens[segment[index]];
                    if (paren == 0 && bracket == 0 && IsPunct(token, '='))
                    {
                        return true;
                    }

                    if (IsPunct(token, '('))
                    {
                        paren++;
                    }
                    else if (IsPunct(token, ')'))
                    {
                        if (paren > 0)
                        {
                            paren--;
                        }
                    }
                    else if (IsPunct(token, '['))
                    {
                        bracket++;
                    }
                    else if (IsPunct(token, ']'))
                    {
                        if (bracket > 0)
                        {
                            bracket--;
                        }
                    }
                }

                return false;
            }

            private List<List<int>> SplitDeclarationSegments(List<int> indices)
            {
                var result = new List<List<int>>();
                var current = new List<int>(8);
                int paren = 0;
                int bracket = 0;
                int angle = 0;
                CppToken previous = EofToken;

                for (int index = 0; index < indices.Count; index++)
                {
                    CppToken token = _tokens[indices[index]];
                    if (paren == 0 && bracket == 0 && angle == 0 && IsPunct(token, ','))
                    {
                        result.Add(current);
                        current = new List<int>(8);
                        previous = token;
                        continue;
                    }

                    current.Add(indices[index]);

                    if (IsPunct(token, '('))
                    {
                        paren++;
                    }
                    else if (IsPunct(token, ')'))
                    {
                        if (paren > 0)
                        {
                            paren--;
                        }
                    }
                    else if (IsPunct(token, '['))
                    {
                        bracket++;
                    }
                    else if (IsPunct(token, ']'))
                    {
                        if (bracket > 0)
                        {
                            bracket--;
                        }
                    }
                    else if (IsPunct(token, '<') && previous.Kind == CppTokenKind.Identifier)
                    {
                        angle++;
                    }
                    else if (IsAngleClose(token))
                    {
                        if (token.Length == 2)
                        {
                            angle -= 2;
                        }
                        else
                        {
                            angle--;
                        }

                        if (angle < 0)
                        {
                            angle = 0;
                        }
                    }

                    previous = token;
                }

                result.Add(current);
                return result;
            }

            private bool TryFindVariableName(List<int> segment, out int tokenIndex, out string name)
            {
                tokenIndex = -1;
                name = string.Empty;

                // Function / member pointer declarator: (*name)(...) or (Type::*name)(...)
                for (int index = 0; index + 2 < segment.Count; index++)
                {
                    if (!IsPunct(_tokens[segment[index]], '*'))
                    {
                        continue;
                    }

                    CppToken identifier = _tokens[segment[index + 1]];
                    if (identifier.Kind == CppTokenKind.Identifier && !IsReservedWord(GetText(identifier)) &&
                        IsPunct(_tokens[segment[index + 2]], ')'))
                    {
                        tokenIndex = segment[index + 1];
                        name = GetText(identifier);
                        return true;
                    }
                }

                int paren = 0;
                int bracket = 0;
                int angle = 0;
                int lastCandidate = -1;
                CppToken previous = EofToken;

                for (int index = 0; index < segment.Count; index++)
                {
                    CppToken token = _tokens[segment[index]];
                    if (paren == 0 && bracket == 0 && angle == 0)
                    {
                        if (IsPunct(token, '='))
                        {
                            break;
                        }

                        if (token.Kind == CppTokenKind.Identifier && !IsReservedWord(GetText(token)))
                        {
                            CppToken next = index + 1 < segment.Count ? _tokens[segment[index + 1]] : EofToken;
                            if (!IsPunct(next, "::"))
                            {
                                lastCandidate = index;
                            }
                        }
                    }

                    if (IsPunct(token, '('))
                    {
                        paren++;
                    }
                    else if (IsPunct(token, ')'))
                    {
                        if (paren > 0)
                        {
                            paren--;
                        }
                    }
                    else if (IsPunct(token, '['))
                    {
                        bracket++;
                    }
                    else if (IsPunct(token, ']'))
                    {
                        if (bracket > 0)
                        {
                            bracket--;
                        }
                    }
                    else if (IsPunct(token, '<') && previous.Kind == CppTokenKind.Identifier)
                    {
                        angle++;
                    }
                    else if (IsAngleClose(token))
                    {
                        if (token.Length == 2)
                        {
                            angle -= 2;
                        }
                        else
                        {
                            angle--;
                        }

                        if (angle < 0)
                        {
                            angle = 0;
                        }
                    }

                    previous = token;
                }

                if (lastCandidate < 0)
                {
                    return false;
                }

                tokenIndex = segment[lastCandidate];
                name = GetText(_tokens[tokenIndex]);
                if (lastCandidate + 1 < segment.Count)
                {
                    CppToken next = _tokens[segment[lastCandidate + 1]];
                    if (IsPunct(next, "::") || IsPunct(next, '<'))
                    {
                        return false;
                    }
                }

                return !string.IsNullOrEmpty(name);
            }

            // -----------------------------------------------------------------------------------------
            // Skipping helpers
            // -----------------------------------------------------------------------------------------

            private void SkipStatement()
            {
                int paren = 0;
                int bracket = 0;
                while (_i < _tokens.Count)
                {
                    CppToken token = Tok;
                    if (token.Kind == CppTokenKind.EndOfFile)
                    {
                        return;
                    }

                    if (paren == 0 && bracket == 0)
                    {
                        if (IsPunct(token, ';'))
                        {
                            _i++;
                            return;
                        }

                        if (IsPunct(token, '}'))
                        {
                            return;
                        }

                        if (IsPunct(token, '{'))
                        {
                            _i++;
                            SkipBalancedBracesFromConsumed();
                            return;
                        }
                    }

                    if (IsPunct(token, '('))
                    {
                        paren++;
                    }
                    else if (IsPunct(token, ')'))
                    {
                        if (paren > 0)
                        {
                            paren--;
                        }
                    }
                    else if (IsPunct(token, '['))
                    {
                        bracket++;
                    }
                    else if (IsPunct(token, ']'))
                    {
                        if (bracket > 0)
                        {
                            bracket--;
                        }
                    }

                    _i++;
                }
            }

            private void SkipCatchClause()
            {
                _i++; // catch
                if (IsPunct(Tok, '('))
                {
                    SkipBalancedParentheses();
                }

                if (IsPunct(Tok, '{'))
                {
                    _i++;
                    SkipBalancedBracesFromConsumed();
                }
            }

            private void SkipTrailingFunctionClauses()
            {
                for (int guard = 0; guard < 8; guard++)
                {
                    if (IsWord(Tok, "catch"))
                    {
                        SkipCatchClause();
                        continue;
                    }

                    if (IsPunct(Tok, ';'))
                    {
                        _i++;
                        return;
                    }

                    return;
                }
            }

            private void SkipBalancedParentheses()
            {
                if (!IsPunct(Tok, '('))
                {
                    return;
                }

                int depth = 0;
                while (_i < _tokens.Count)
                {
                    CppToken token = Tok;
                    if (token.Kind == CppTokenKind.EndOfFile)
                    {
                        return;
                    }

                    if (IsPunct(token, '('))
                    {
                        depth++;
                    }
                    else if (IsPunct(token, ')'))
                    {
                        depth--;
                        if (depth == 0)
                        {
                            _i++;
                            return;
                        }
                    }

                    _i++;
                }
            }

            private void SkipBalancedBracesFromConsumed()
            {
                int depth = 1;
                while (_i < _tokens.Count)
                {
                    CppToken token = Tok;
                    if (token.Kind == CppTokenKind.EndOfFile)
                    {
                        return;
                    }

                    if (IsPunct(token, '{'))
                    {
                        depth++;
                    }
                    else if (IsPunct(token, '}'))
                    {
                        depth--;
                        if (depth == 0)
                        {
                            _i++;
                            return;
                        }
                    }

                    _i++;
                }
            }

            private int SkipBracesAt(int braceIndex)
            {
                int depth = 0;
                int j = braceIndex;
                while (j < _tokens.Count)
                {
                    CppToken token = _tokens[j];
                    if (token.Kind == CppTokenKind.EndOfFile)
                    {
                        return j;
                    }

                    if (IsPunct(token, '{'))
                    {
                        depth++;
                    }
                    else if (IsPunct(token, '}'))
                    {
                        depth--;
                        if (depth == 0)
                        {
                            return j + 1;
                        }
                    }

                    j++;
                }

                return j;
            }

            private void SkipUntilBodyOrSemicolon()
            {
                int paren = 0;
                int bracket = 0;
                int angle = 0;
                CppToken previous = EofToken;

                while (_i < _tokens.Count)
                {
                    CppToken token = Tok;
                    if (token.Kind == CppTokenKind.EndOfFile)
                    {
                        return;
                    }

                    if (paren == 0 && bracket == 0 && angle == 0)
                    {
                        if (IsPunct(token, '{') || IsPunct(token, ';'))
                        {
                            return;
                        }

                        if (IsPunct(token, '}'))
                        {
                            return;
                        }
                    }

                    if (IsPunct(token, '('))
                    {
                        paren++;
                    }
                    else if (IsPunct(token, ')'))
                    {
                        if (paren > 0)
                        {
                            paren--;
                        }
                    }
                    else if (IsPunct(token, '['))
                    {
                        bracket++;
                    }
                    else if (IsPunct(token, ']'))
                    {
                        if (bracket > 0)
                        {
                            bracket--;
                        }
                    }
                    else if (IsPunct(token, '<') && previous.Kind == CppTokenKind.Identifier)
                    {
                        angle++;
                    }
                    else if (IsAngleClose(token) && angle > 0)
                    {
                        angle--;
                    }

                    previous = token;
                    _i++;
                }
            }

            // -----------------------------------------------------------------------------------------
            // Heuristic macro annotation handling
            // -----------------------------------------------------------------------------------------

            private bool TryHandleMacroLikeIdentifier()
            {
                CppToken token = Tok;
                string word = GetText(token);
                if (!IsMacroLike(token, word))
                {
                    return false;
                }

                CppToken next = Peek(1);
                if (next.Kind == CppTokenKind.Identifier)
                {
                    // Standalone annotation macro before a declaration: Q_OBJECT, MY_API, DWORD, WINAPI.
                    _i++;
                    return true;
                }

                if (next.Kind == CppTokenKind.StringLiteral)
                {
                    _i++;
                    return true;
                }

                if (IsPunct(next, '('))
                {
                    _i++;
                    SkipBalancedParentheses();
                    if (IsPunct(Tok, ';'))
                    {
                        _i++;
                    }

                    return true;
                }

                if (IsPunct(next, ';'))
                {
                    _i += 2;
                    ResetPending();
                    return true;
                }

                if (IsPunct(next, ':'))
                {
                    _i += 2;
                    return true;
                }

                if (IsPunct(next, '{'))
                {
                    _i++;
                    return true;
                }

                if (next.Kind == CppTokenKind.EndOfFile || IsPunct(next, '}'))
                {
                    _i++;
                    return true;
                }

                return false;
            }
        }
    }
}
