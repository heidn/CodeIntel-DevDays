using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodeIntel.Server.Models;
using CodeIntel.Server.Services.LanguageBackends;
using Microsoft.Extensions.Options;
using StreamJsonRpc;

namespace CodeIntel.Server.Services.LanguageBackends.Lsp;

/// <summary>
/// One running LSP session — wraps a typescript-language-server child process
/// and a <see cref="JsonRpc"/> client over its stdin/stdout pair. Operations are
/// thin wrappers over LSP JSON-RPC methods.
///
/// Concurrency: <see cref="JsonRpc"/> serializes requests internally, so multiple
/// callers from the agentic loop or trace BFS can fire operations in parallel.
/// </summary>
public sealed class LspSession : ILspSession, IAsyncDisposable
{
    private readonly Process _process;
    private readonly JsonRpc _rpc;
    private readonly string _rootFolder;
    private readonly LspOptions _options;
    private readonly ILogger _logger;
    private readonly HashSet<string> _openedDocs = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _docLock = new(1, 1);
    private volatile bool _ready;

    public string WorkspaceId { get; }
    public bool IsReady => _ready;

    private LspSession(
        string workspaceId,
        string rootFolder,
        Process process,
        JsonRpc rpc,
        LspOptions options,
        ILogger logger)
    {
        WorkspaceId = workspaceId;
        _rootFolder = rootFolder;
        _process = process;
        _rpc = rpc;
        _options = options;
        _logger = logger;
    }

    public static async Task<LspSession> StartAsync(
        string workspaceId,
        string rootFolder,
        LspOptions options,
        ILogger logger,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = options.TypeScriptCommand,
            WorkingDirectory = rootFolder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in options.TypeScriptArgs) psi.ArgumentList.Add(a);

        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Process.Start returned null for {options.TypeScriptCommand}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not launch '{options.TypeScriptCommand}'. " +
                "Install with: npm install -g typescript-language-server typescript", ex);
        }

        // Forward stderr to our logger so failures inside the child process are visible.
        _ = Task.Run(async () =>
        {
            try
            {
                while (!process.HasExited)
                {
                    var line = await process.StandardError.ReadLineAsync();
                    if (line is null) break;
                    logger.LogDebug("[lsp/{WorkspaceId}] stderr: {Line}", workspaceId, line);
                }
            }
            catch { /* process ended */ }
        });

        var handler = new HeaderDelimitedMessageHandler(
            sendingStream: process.StandardInput.BaseStream,
            receivingStream: process.StandardOutput.BaseStream,
            formatter: new SystemTextJsonFormatter
            {
                JsonSerializerOptions =
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                },
            });
        var rpc = new JsonRpc(handler);
        rpc.StartListening();

        var session = new LspSession(workspaceId, rootFolder, process, rpc, options, logger);
        await session.InitializeAsync(ct);
        return session;
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.InitializeTimeoutSeconds));

        var rootUri = LspProtocol.PathToUri(_rootFolder);
        var initParams = new LspProtocol.InitializeParams(
            ProcessId: Environment.ProcessId,
            RootUri: rootUri,
            WorkspaceFolders: [new LspProtocol.WorkspaceFolder(rootUri, Path.GetFileName(_rootFolder.TrimEnd(Path.DirectorySeparatorChar)))],
            Capabilities: new
            {
                workspace = new { workspaceFolders = true, symbol = new { dynamicRegistration = false } },
                textDocument = new
                {
                    references = new { dynamicRegistration = false },
                    definition = new { dynamicRegistration = false },
                    documentSymbol = new { dynamicRegistration = false, hierarchicalDocumentSymbolSupport = true },
                    callHierarchy = new { dynamicRegistration = false },
                },
            },
            ClientInfo: new LspProtocol.ClientInfo("CodeIntel", "1.0"));

        try
        {
            // LSP methods take a single by-name params object — NOT a positional
            // array. StreamJsonRpc's InvokeWithCancellationAsync(method, object[], ct)
            // sends `params: [obj]`; the LSP server rejects that with
            // "defines parameters by name but received parameters by position".
            // InvokeWithParameterObjectAsync sends `params: obj` as required.
            await _rpc.InvokeWithParameterObjectAsync<LspProtocol.InitializeResult>(
                "initialize", initParams, timeout.Token);
            await _rpc.NotifyWithParameterObjectAsync("initialized", new { });

            // typescript-language-server's `workspace/symbol` returns nothing until
            // tsserver has loaded a project — and tsserver only loads a project once
            // a file is opened. Open one source file and poll until the symbol index
            // is live, so the first trace / class-lookup query doesn't come back empty.
            // Warmup failures degrade the session (text-search fallback) but never
            // kill it — hence the broad catch and the original `ct`, not the init timeout.
            await WarmUpProjectAsync(ct);

            _ready = true;
            _logger.LogInformation("LSP session ready for workspace {Id} at {Root}", WorkspaceId, _rootFolder);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LSP initialize failed for workspace {Id}", WorkspaceId);
            await DisposeAsync();
            throw;
        }
    }

    private async Task WarmUpProjectAsync(CancellationToken ct)
    {
        try
        {
            var warmupFiles = FindWarmupFiles(_rootFolder);
            if (warmupFiles.Count == 0)
            {
                _logger.LogWarning(
                    "LSP warmup: no source file found under {Root} — workspace/symbol may return empty",
                    _rootFolder);
                return;
            }

            // typescript-language-server's `workspace/symbol` only returns symbols
            // from files tsserver has actually PARSED — opening one file (even one
            // in the main program) leaves the rest invisible. A code-intelligence
            // tool needs project-wide symbol search, so we open every source file
            // up front. Bounded by FindWarmupFiles' BFS cap; source files are
            // ordered first so a huge repo still gets the important ones.
            foreach (var f in warmupFiles)
                await EnsureDocumentOpenAsync(f, ct);
            _logger.LogInformation(
                "LSP warmup: opened {Count} source files to seed the symbol index", warmupFiles.Count);

            // tsserver loads/indexes the project asynchronously after didOpen. Poll
            // workspace/symbol with a short query derived from a warmup file's name
            // until it answers (index live) or we hit the init budget.
            var probe = Path.GetFileNameWithoutExtension(warmupFiles[0]);
            var query = probe.Length >= 2 ? probe[..2] : probe;
            var sw = Stopwatch.StartNew();
            var budget = TimeSpan.FromSeconds(_options.InitializeTimeoutSeconds);

            while (sw.Elapsed < budget)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var symbols = await _rpc.InvokeWithParameterObjectAsync<LspProtocol.SymbolInformation[]?>(
                        "workspace/symbol", new LspProtocol.WorkspaceSymbolParams(query), ct);
                    if (symbols is { Length: > 0 })
                    {
                        _logger.LogInformation(
                            "LSP warmup: project index live after {Ms}ms ({Count} symbols for '{Query}')",
                            sw.ElapsedMilliseconds, symbols.Length, query);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "LSP warmup probe failed (will retry)");
                }
                await Task.Delay(500, ct);
            }

            _logger.LogWarning(
                "LSP warmup: workspace/symbol still empty after {Sec}s — semantic lookups may be degraded",
                _options.InitializeTimeoutSeconds);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("LSP warmup cancelled for workspace {Id} — semantic lookups may be degraded", WorkspaceId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LSP warmup failed for workspace {Id} — semantic lookups may be degraded", WorkspaceId);
        }
    }

    /// <summary>
    /// Finds the workspace's source files to seed tsserver's symbol index. tsserver's
    /// <c>workspace/symbol</c> only covers files it has parsed, so for project-wide
    /// symbol search every source file must be opened. Bounded BFS that skips
    /// dependency / build dirs and root-level config files; source-dir files are
    /// ordered first so an oversized repo still gets the important ones before the cap.
    /// </summary>
    private static List<string> FindWarmupFiles(string root)
    {
        string[] excludeDirs = ["node_modules", "dist", ".next", "out", ".cache", ".git", "bin", "obj"];
        string[] sourceDirHints = ["src", "app", "lib", "components", "pages", "screens", "features"];
        const int MaxFiles = 400;

        var candidates = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        var dirsVisited = 0;

        while (stack.Count > 0 && dirsVisited < 2000 && candidates.Count < 400)
        {
            var dir = stack.Pop();
            dirsVisited++;
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(dir);
                dirs = Directory.GetDirectories(dir);
            }
            catch { continue; }

            foreach (var f in files)
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is not (".ts" or ".tsx" or ".js" or ".jsx")) continue;
                if (f.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase)) continue;
                // Skip config files — they load inferred projects, not the main program.
                var name = Path.GetFileName(f);
                if (name.Contains(".config.", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("babel.config.js", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("metro.config.js", StringComparison.OrdinalIgnoreCase)) continue;
                candidates.Add(f);
            }
            foreach (var d in dirs)
            {
                if (!excludeDirs.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                    stack.Push(d);
            }
        }

        // Prefer files under a recognized source directory; tiebreak deeper-first so
        // we land inside the program rather than on a top-level shim.
        bool UnderSourceDir(string p) => p
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(seg => sourceDirHints.Contains(seg, StringComparer.OrdinalIgnoreCase));

        return candidates
            .OrderByDescending(UnderSourceDir)
            .ThenByDescending(p => p.Count(c => c is '/' or '\\'))
            .Take(MaxFiles)
            .ToList();
    }

    /// <summary>
    /// LSP requires textDocument/didOpen before semantic operations work on a file.
    /// We open files lazily on first reference and keep track of what's open so
    /// repeat operations don't double-open.
    /// </summary>
    private async Task EnsureDocumentOpenAsync(string filePath, CancellationToken ct)
    {
        if (_openedDocs.Contains(filePath)) return;
        await _docLock.WaitAsync(ct);
        try
        {
            if (_openedDocs.Contains(filePath)) return;
            string text;
            try { text = await File.ReadAllTextAsync(filePath, ct); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read {Path} for LSP didOpen", filePath);
                return;
            }
            var langId = Path.GetExtension(filePath).ToLowerInvariant() switch
            {
                ".ts"  => "typescript",
                ".tsx" => "typescriptreact",
                ".jsx" => "javascriptreact",
                _      => "javascript",
            };
            await _rpc.NotifyWithParameterObjectAsync("textDocument/didOpen",
                new LspProtocol.DidOpenTextDocumentParams(
                    new LspProtocol.TextDocumentItem(
                        Uri: LspProtocol.PathToUri(filePath),
                        LanguageId: langId,
                        Version: 1,
                        Text: text)));
            _openedDocs.Add(filePath);
        }
        finally
        {
            _docLock.Release();
        }
    }

    private CancellationTokenSource WithTimeout(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
        return cts;
    }

    // ============================================================
    //  Symbol lookups via workspace/symbol
    // ============================================================

    public async Task<SymbolLookupResult?> FindClassAsync(string name, CancellationToken ct) =>
        await FindSymbolBlockAsync(name, ct, kinds: [
            LspProtocol.SymbolKind.Class,
            LspProtocol.SymbolKind.Interface]);

    public async Task<SymbolLookupResult?> FindMethodAsync(string name, CancellationToken ct) =>
        await FindSymbolBlockAsync(name, ct, kinds: [
            LspProtocol.SymbolKind.Method,
            LspProtocol.SymbolKind.Function,
            LspProtocol.SymbolKind.Constructor]);

    private async Task<SymbolLookupResult?> FindSymbolBlockAsync(
        string name, CancellationToken ct, int[] kinds)
    {
        if (!_ready) return null;
        try
        {
            using var cts = WithTimeout(ct);
            var symbols = await _rpc.InvokeWithParameterObjectAsync<LspProtocol.SymbolInformation[]?>(
                "workspace/symbol",
                new LspProtocol.WorkspaceSymbolParams(name),
                cts.Token);

            var match = symbols?.FirstOrDefault(s =>
                string.Equals(s.Name, name, StringComparison.Ordinal) && kinds.Contains(s.Kind));
            if (match is null) return null;

            var filePath = LspProtocol.UriToPath(match.Location.Uri);
            if (!File.Exists(filePath)) return null;
            var content = await File.ReadAllTextAsync(filePath, ct);

            // Return the surrounding code as the snippet — same shape the Roslyn
            // backend returns when it fulfils a class/method context request.
            var rel = TryMakeRelative(filePath);
            var sb = new StringBuilder();
            sb.AppendLine($"// SNIPPET from: {rel}");
            sb.AppendLine("```typescript");
            sb.AppendLine(content);
            sb.AppendLine("```");
            return new SymbolLookupResult(sb.ToString(), filePath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LSP workspace/symbol failed for '{Name}'", name);
            return null;
        }
    }

    public async Task<IReadOnlyList<CallerInfo>> FindCallersAsync(string methodName, CancellationToken ct)
    {
        if (!_ready) return Array.Empty<CallerInfo>();
        try
        {
            // Resolve the symbol, then ask for references with includeDeclaration=false.
            using var cts = WithTimeout(ct);
            var symbols = await _rpc.InvokeWithParameterObjectAsync<LspProtocol.SymbolInformation[]?>(
                "workspace/symbol",
                new LspProtocol.WorkspaceSymbolParams(methodName),
                cts.Token);

            var symbol = symbols?.FirstOrDefault(s =>
                string.Equals(s.Name, methodName, StringComparison.Ordinal));
            if (symbol is null) return Array.Empty<CallerInfo>();

            var filePath = LspProtocol.UriToPath(symbol.Location.Uri);
            await EnsureDocumentOpenAsync(filePath, ct);

            var refParams = new LspProtocol.ReferenceParams(
                TextDocument: new LspProtocol.TextDocumentIdentifier(symbol.Location.Uri),
                Position: symbol.Location.Range.Start,
                Context: new LspProtocol.ReferenceContext(IncludeDeclaration: false));

            var refs = await _rpc.InvokeWithParameterObjectAsync<LspProtocol.Location[]?>(
                "textDocument/references", refParams, cts.Token);

            return refs?.Take(20).Select(l =>
                new CallerInfo(
                    DisplayName: $"{Path.GetFileNameWithoutExtension(LspProtocol.UriToPath(l.Uri))}:{l.Range.Start.Line + 1}",
                    FilePath: LspProtocol.UriToPath(l.Uri))).ToList()
                ?? new List<CallerInfo>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LSP references failed for '{Name}'", methodName);
            return Array.Empty<CallerInfo>();
        }
    }

    public async Task<DefinitionLocation?> FindDefinitionAsync(
        string filePath, int line, int character, CancellationToken ct)
    {
        if (!_ready) return null;
        try
        {
            await EnsureDocumentOpenAsync(filePath, ct);
            using var cts = WithTimeout(ct);
            var defs = await _rpc.InvokeWithParameterObjectAsync<LspProtocol.Location[]?>(
                "textDocument/definition",
                new LspProtocol.TextDocumentPositionParams(
                    TextDocument: new LspProtocol.TextDocumentIdentifier(LspProtocol.PathToUri(filePath)),
                    Position: new LspProtocol.Position(line - 1, character)),
                cts.Token);

            var def = defs?.FirstOrDefault();
            if (def is null) return null;

            var defPath = LspProtocol.UriToPath(def.Uri);
            return new DefinitionLocation(
                FilePath: defPath,
                Line: def.Range.Start.Line + 1,
                Character: def.Range.Start.Character,
                SymbolName: Path.GetFileNameWithoutExtension(defPath));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LSP definition failed at {File}:{Line}", filePath, line);
            return null;
        }
    }

    // ============================================================
    //  Trace-mode operations via callHierarchy
    // ============================================================

    public async Task<IReadOnlyList<MethodHandle>> ResolveEntryPointAsync(
        TraceEntryPoint entryPoint, CancellationToken ct)
    {
        if (!_ready) return Array.Empty<MethodHandle>();

        try
        {
            using var cts = WithTimeout(ct);

            // Path A: method-name query → workspace/symbol → prepareCallHierarchy on hit.
            if (!string.IsNullOrWhiteSpace(entryPoint.MethodName))
            {
                var parts = entryPoint.MethodName.Trim().Split('.');
                var methodPart = parts[^1];
                var typeHint = parts.Length > 1 ? parts[^2] : null;

                var symbols = await _rpc.InvokeWithParameterObjectAsync<LspProtocol.SymbolInformation[]?>(
                    "workspace/symbol",
                    new LspProtocol.WorkspaceSymbolParams(methodPart),
                    cts.Token);

                if (symbols is null) return Array.Empty<MethodHandle>();

                // Diagnostic for the open LSP-#4 issue (workspace/symbol blind to
                // project symbols). Debug-level — bump the `…LanguageBackends.Lsp`
                // log category to Debug to see it. Remove once trace-on-TS is verified.
                _logger.LogDebug(
                    "LSP workspace/symbol('{Query}') → {Count} raw symbols: {Sample}",
                    methodPart, symbols.Length,
                    string.Join("; ", symbols.Take(10).Select(s => $"{s.Name}[kind={s.Kind},container={s.ContainerName}]")));

                var matching = symbols.Where(s =>
                    string.Equals(s.Name, methodPart, StringComparison.Ordinal)
                    && (s.Kind is LspProtocol.SymbolKind.Method
                                  or LspProtocol.SymbolKind.Function
                                  or LspProtocol.SymbolKind.Constructor)
                    && (typeHint is null
                        || string.Equals(s.ContainerName, typeHint, StringComparison.Ordinal)));

                var handles = new List<MethodHandle>();
                foreach (var s in matching)
                {
                    var item = await PrepareCallHierarchyAsync(
                        LspProtocol.UriToPath(s.Location.Uri),
                        s.Location.Range.Start.Line,
                        s.Location.Range.Start.Character,
                        cts.Token);
                    if (item is not null) handles.Add(BuildHandle(item));
                }
                return handles;
            }

            // Path B: file + line + character → prepareCallHierarchy.
            if (!string.IsNullOrWhiteSpace(entryPoint.FilePath)
                && entryPoint.Line is int line && entryPoint.Character is int chr)
            {
                var item = await PrepareCallHierarchyAsync(entryPoint.FilePath, line - 1, chr, cts.Token);
                if (item is not null)
                    return new[] { BuildHandle(item) };
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LSP resolveEntryPoint failed");
        }

        return Array.Empty<MethodHandle>();
    }

    public async Task<IReadOnlyList<MethodHandle>> FindCallersOfAsync(
        MethodHandle target, CancellationToken ct)
    {
        if (!_ready) return Array.Empty<MethodHandle>();
        var item = target.PayloadAs<LspProtocol.CallHierarchyItem>();
        try
        {
            using var cts = WithTimeout(ct);
            var calls = await _rpc.InvokeWithParameterObjectAsync<LspProtocol.CallHierarchyIncomingCall[]?>(
                "callHierarchy/incomingCalls",
                new LspProtocol.CallHierarchyIncomingCallsParams(item),
                cts.Token);
            return calls?.Select(c => BuildHandle(c.From)).ToList() ?? new List<MethodHandle>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LSP incomingCalls failed for {Name}", item.Name);
            return Array.Empty<MethodHandle>();
        }
    }

    public async Task<IReadOnlyList<MethodHandle>> FindCalleesOfAsync(
        MethodHandle source, CancellationToken ct)
    {
        if (!_ready) return Array.Empty<MethodHandle>();
        var item = source.PayloadAs<LspProtocol.CallHierarchyItem>();
        try
        {
            using var cts = WithTimeout(ct);
            var calls = await _rpc.InvokeWithParameterObjectAsync<LspProtocol.CallHierarchyOutgoingCall[]?>(
                "callHierarchy/outgoingCalls",
                new LspProtocol.CallHierarchyOutgoingCallsParams(item),
                cts.Token);
            return calls?.Select(c => BuildHandle(c.To)).ToList() ?? new List<MethodHandle>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LSP outgoingCalls failed for {Name}", item.Name);
            return Array.Empty<MethodHandle>();
        }
    }

    public async Task<string?> GetMethodBodyAsync(MethodHandle method, CancellationToken ct)
    {
        var item = method.PayloadAs<LspProtocol.CallHierarchyItem>();
        var filePath = LspProtocol.UriToPath(item.Uri);
        if (!File.Exists(filePath)) return null;
        try
        {
            var content = await File.ReadAllTextAsync(filePath, ct);
            var lines = content.Split('\n');
            var startLine = item.Range.Start.Line;
            var endLine = Math.Min(item.Range.End.Line, lines.Length - 1);
            if (startLine < 0 || startLine >= lines.Length) return null;
            var sb = new StringBuilder();
            for (var i = startLine; i <= endLine; i++)
                sb.AppendLine(lines[i]);
            var body = sb.ToString();
            const int MaxBodyChars = 2000;
            return body.Length <= MaxBodyChars
                ? body
                : body.Substring(0, MaxBodyChars) + "\n// ... [truncated]";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading method body failed for {Path}", filePath);
            return null;
        }
    }

    private async Task<LspProtocol.CallHierarchyItem?> PrepareCallHierarchyAsync(
        string filePath, int line, int character, CancellationToken ct)
    {
        await EnsureDocumentOpenAsync(filePath, ct);
        try
        {
            var items = await _rpc.InvokeWithParameterObjectAsync<LspProtocol.CallHierarchyItem[]?>(
                "textDocument/prepareCallHierarchy",
                new LspProtocol.CallHierarchyPrepareParams(
                    TextDocument: new LspProtocol.TextDocumentIdentifier(LspProtocol.PathToUri(filePath)),
                    Position: new LspProtocol.Position(line, character)),
                ct);
            return items?.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "prepareCallHierarchy failed at {File}:{Line}", filePath, line);
            return null;
        }
    }

    private MethodHandle BuildHandle(LspProtocol.CallHierarchyItem item)
    {
        var filePath = LspProtocol.UriToPath(item.Uri);
        var fqn = item.Detail is null ? item.Name : $"{item.Detail}.{item.Name}";
        var display = item.Detail is null ? item.Name : $"{Path.GetFileNameWithoutExtension(filePath)}.{item.Name}";
        return new MethodHandle(
            backendId: TypeScriptLspBackend.BackendId,
            fqn: fqn,
            displayName: display,
            filePath: filePath,
            line: item.Range.Start.Line + 1,
            payload: item);
    }

    private string TryMakeRelative(string absolutePath)
    {
        try { return Path.GetRelativePath(_rootFolder, absolutePath); }
        catch { return absolutePath; }
    }

    public async ValueTask DisposeAsync()
    {
        _ready = false;
        try
        {
            if (!_process.HasExited)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await _rpc.InvokeWithCancellationAsync<object?>("shutdown", Array.Empty<object>(), cts.Token);
                    await _rpc.NotifyAsync("exit");
                }
                catch { /* best effort */ }
            }
        }
        finally
        {
            try { _rpc.Dispose(); } catch { }
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync();
                }
            }
            catch { /* process already dead */ }
            _process.Dispose();
            _docLock.Dispose();
        }
    }
}
