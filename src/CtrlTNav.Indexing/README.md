# CtrlTNav.Indexing

C/C++ indexing engine for the Ctrl+T Navigator extension. It builds the in-memory navigation graph
without clang or tree-sitter: a tolerant hand-written lexer plus a forgiving declaration scanner.

```
src/CtrlTNav.Indexing/
  Cpp/CppToken.cs              token model (kind, offsets, 1-based line/column)
  Cpp/CppTokenizer.cs          tolerant lexer + preprocessor handling
  Cpp/CppSymbolExtractor.cs    declaration scanner -> NavigationItem list
  CppIndex.cs                  parallel background index (ICppIndex)
  SolutionFileParser.cs        .sln + .vcxproj enumeration
  SourceFileScanner.cs         tolerant recursive directory scan
  GlobMatcher.cs               ** / * / ? exclude-pattern matching
  IndexCache.cs                versioned binary cache (load/save/freshness)
  IndexCacheEntry.cs           one file's cached items
```

## Public behaviour

### `CppSymbolExtractor`

`new CppSymbolExtractor().Extract(filePath, content, projectName, projectPath, language)` returns every
navigation-relevant symbol in the file. The extractor is **stateless and thread-safe**; it never throws,
even for truncated or binary-ish input.

Extracted constructs:

* namespaces (`namespace a`, nested, `namespace a::b`, anonymous, `namespace alias = target;`, `inline`)
* `class` / `struct` / `union` / `__interface`: definitions, forward declarations, nested types,
  template classes, `final`, base clauses, export/annotation macros before the name
* `enum` / `enum class`: enum item plus one `EnumMember` item per enumerator (also anonymous enums)
* `typedef` (simple, function-pointer, `typedef struct {...} Alias;`), `using X = Y;`,
  `template<...> using X = Y;`
* `concept Name = ...;`
* functions and methods: declarations and definitions, `static`, trailing return types,
  `const`/`noexcept`/`override`/`final`, default arguments, multi-line signatures, function-try-blocks,
  constructor initializer lists (including brace-initialised members)
* constructors, destructors, operators (`operator+`, `operator()`, `operator[]`, `operator=`,
  conversion operators like `operator bool`, `new`/`delete`, user-defined literals)
* out-of-class member definitions (`void ns::Type::Method() {}`) with the qualified container inferred
* global variables (`static`, `extern`, arrays, multiple declarators, function-pointer variables)
* class fields with `public:` / `protected:` / `private:` access, bitfields, `static` members
* `#define` object-like and function-like macros (the signature is stored in `NavigationItem.Signature`)
* `extern "C"` blocks and single declarations; friend/static_assert/attributes/`__declspec` are handled

Ignored: local variables and everything inside function bodies, include directives, `#if 0` branches,
macro invocations, `using namespace` declarations.

Each item gets 1-based `Line`/`Column` of the *name token*, `ContainerName` (enclosing namespace/type),
`FullName` (`container::name`), `ItemFlags` (access, static, exported, template, declaration/definition,
namespace/class scope, header file) and the project metadata supplied by the caller.

For destructors `ItemKind.Destructor` is set and `Name` is the class name **without** the leading `~`
(e.g. `Name = "Widget"`, source token `~Widget`); `Line`/`Column` point at the identifier. Keeping the
tilde out of `Name` makes prefix/camel searches for the class name rank constructors and destructors
together; the UI renders the kind badge separately.

`SourceFileScanner.ScanDirectory` matches exclude patterns against paths **relative to the scan root**
and always applies the built-in defaults (`**/bin/**`, `**/obj/**`, `**/build/**`, `**/.git/**`, …) in
addition to user-configured patterns. Single-file checks (`IsSupportedFile`) match against the full path.

### `CppIndex`

`new CppIndex(options, log)` implements `ICppIndex`:

* `BuildAsync(files, ct)` — parallel (max 8 workers) full rebuild. Progress events are throttled to about
  one every 50 ms. Cancelling keeps the previous index content and does **not** throw.
* `UpdateFileAsync(file, ct)` — re-parses one tracked file; untracked files are ignored (no-op).
* `RemoveFile(path)`, `Clear()`, `Snapshot()`.
* The snapshot returned by `Snapshot()` is an immutable array swapped atomically; readers never block.
  It contains one `ItemKind.File` pseudo item per indexed file (Name = file name, FullName = full path)
  followed by that file's symbols, files ordered by path.
* `ItemsChanged` is raised with `IsFullRebuild = true` after a build, and with the changed file's items
  after an update/remove.
* Files are skipped when missing, unreadable, binary, larger than `MaxFileSizeKb`, of an unsupported
  extension, or matching an exclude glob. Nothing in the build path throws to the caller; subscriber
  exceptions are logged and swallowed.

### Solution / scanner / cache

* `SolutionFileParser.ParseSolution` parses `.sln` project entries, skips solution folders and non-C++
  projects, tolerates missing/broken projects, resolves relative paths and `$(ProjectDir)` /
  `$(MSBuildProjectDirectory)` variables. `ParseProject` reads `ClCompile`, `ClInclude` and `None` items
  with C/C++ extensions; wildcards and unresolvable `$(...)` variables are skipped.
* `SourceFileScanner.ScanDirectory` walks a directory tolerantly (access errors, long paths, junction
  loops) and applies the extension/exclude settings. `IsSupportedFile` is the cheap single-file check.
* `GlobMatcher` supports `**/`, `*`, `?`, is case-insensitive, and treats `regex` characters literally.
* `IndexCache` is a versioned binary cache (`CTNIDX1` magic + format version). `Load` returns `null` for
  missing/corrupt/foreign files; `Save` writes atomically (temp file + replace) and swallows IO errors.
  `IndexCache.IsUpToDate(entry)` compares recorded length + last-write-time against the file so unchanged
  files can be restored without re-parsing.

## Known limitations

* No semantic analysis: `Foo f(1);` at namespace scope can be classified as a function (the most vexing
  parse). `Kind` is a heuristic, not compiler truth.
* A function defined out of line (`void ns::Type::Method()`) is classified as a method even when the
  qualifier is a namespace (`void detail::Helper()`), because the extractor has no symbol table.
* Conditional branches with unknown conditions are all indexed, so a symbol can be reported twice when
  both branches declare it.
* Macro-generated declarations are only visible if the declared name is not hidden by the macro call.
  All-caps annotation macros (`MY_API`, `Q_OBJECT`, `DWORD`) are treated as annotations.
* `.slnx` solutions are not parsed by `ParseSolution` (the VS integration uses DTE enumeration anyway).

## Tests

```
tests/CtrlTNav.Tests/Indexing/          xUnit tests (extractor, tokenizer, index, sln, scanner, glob, cache)
tests/CtrlTNav.Tests/TestData/indexing/ realistic sample.h/sample.cpp/broken.cpp/messy header + .sln/.vcxproj
```

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Filter "FullyQualifiedName~CtrlTNav.Tests.Indexing"`.
