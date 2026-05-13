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
            await _rpc.InvokeWithCancellationAsync<LspProtocol.InitializeResult>(
                "initialize", new object[] { initParams }, timeout.Token);
            await _rpc.NotifyAsync("initialized", new { });
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
            await _rpc.NotifyAsync("textDocument/didOpen",
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
            var symbols = await _rpc.InvokeWithCancellationAsync<LspProtocol.SymbolInformation[]?>(
                "workspace/symbol",
                new object[] { new LspProtocol.WorkspaceSymbolParams(name) },
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
            var symbols = await _rpc.InvokeWithCancellationAsync<LspProtocol.SymbolInformation[]?>(
                "workspace/symbol",
                new object[] { new LspProtocol.WorkspaceSymbolParams(methodName) },
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

            var refs = await _rpc.InvokeWithCancellationAsync<LspProtocol.Location[]?>(
                "textDocument/references", new object[] { refParams }, cts.Token);

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
            var defs = await _rpc.InvokeWithCancellationAsync<LspProtocol.Location[]?>(
                "textDocument/definition",
                new object[]
                {
                    new LspProtocol.TextDocumentPositionParams(
                        TextDocument: new LspProtocol.TextDocumentIdentifier(LspProtocol.PathToUri(filePath)),
                        Position: new LspProtocol.Position(line - 1, character)),
                },
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

                var symbols = await _rpc.InvokeWithCancellationAsync<LspProtocol.SymbolInformation[]?>(
                    "workspace/symbol",
                    new object[] { new LspProtocol.WorkspaceSymbolParams(methodPart) },
                    cts.Token);

                if (symbols is null) return Array.Empty<MethodHandle>();

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
            var calls = await _rpc.InvokeWithCancellationAsync<LspProtocol.CallHierarchyIncomingCall[]?>(
                "callHierarchy/incomingCalls",
                new object[] { new LspProtocol.CallHierarchyIncomingCallsParams(item) },
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
            var calls = await _rpc.InvokeWithCancellationAsync<LspProtocol.CallHierarchyOutgoingCall[]?>(
                "callHierarchy/outgoingCalls",
                new object[] { new LspProtocol.CallHierarchyOutgoingCallsParams(item) },
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
            var items = await _rpc.InvokeWithCancellationAsync<LspProtocol.CallHierarchyItem[]?>(
                "textDocument/prepareCallHierarchy",
                new object[]
                {
                    new LspProtocol.CallHierarchyPrepareParams(
                        TextDocument: new LspProtocol.TextDocumentIdentifier(LspProtocol.PathToUri(filePath)),
                        Position: new LspProtocol.Position(line, character)),
                },
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
