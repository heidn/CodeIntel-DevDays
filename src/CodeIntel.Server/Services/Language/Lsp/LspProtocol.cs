using System.Text.Json.Serialization;

namespace CodeIntel.Server.Services.LanguageBackends.Lsp;

/// <summary>
/// LSP message contracts. Only the subset we actually use is defined here —
/// see https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/
/// for the full spec. Property names match the LSP wire format exactly via
/// JsonPropertyName attributes.
/// </summary>
public static class LspProtocol
{
    public record Position(
        [property: JsonPropertyName("line")] int Line,
        [property: JsonPropertyName("character")] int Character);

    public record Range(
        [property: JsonPropertyName("start")] Position Start,
        [property: JsonPropertyName("end")] Position End);

    public record Location(
        [property: JsonPropertyName("uri")] string Uri,
        [property: JsonPropertyName("range")] Range Range);

    public record TextDocumentIdentifier(
        [property: JsonPropertyName("uri")] string Uri);

    public record TextDocumentItem(
        [property: JsonPropertyName("uri")] string Uri,
        [property: JsonPropertyName("languageId")] string LanguageId,
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("text")] string Text);

    public record DidOpenTextDocumentParams(
        [property: JsonPropertyName("textDocument")] TextDocumentItem TextDocument);

    public record TextDocumentPositionParams(
        [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
        [property: JsonPropertyName("position")] Position Position);

    public record ReferenceContext(
        [property: JsonPropertyName("includeDeclaration")] bool IncludeDeclaration);

    public record ReferenceParams(
        [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
        [property: JsonPropertyName("position")] Position Position,
        [property: JsonPropertyName("context")] ReferenceContext Context);

    public record WorkspaceSymbolParams(
        [property: JsonPropertyName("query")] string Query);

    public record SymbolInformation(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("kind")] int Kind,
        [property: JsonPropertyName("location")] Location Location,
        [property: JsonPropertyName("containerName")] string? ContainerName);

    public record DocumentSymbol(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("detail")] string? Detail,
        [property: JsonPropertyName("kind")] int Kind,
        [property: JsonPropertyName("range")] Range Range,
        [property: JsonPropertyName("selectionRange")] Range SelectionRange,
        [property: JsonPropertyName("children")] DocumentSymbol[]? Children);

    public record DocumentSymbolParams(
        [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument);

    public record WorkspaceFolder(
        [property: JsonPropertyName("uri")] string Uri,
        [property: JsonPropertyName("name")] string Name);

    public record InitializeParams(
        [property: JsonPropertyName("processId")] int? ProcessId,
        [property: JsonPropertyName("rootUri")] string? RootUri,
        [property: JsonPropertyName("workspaceFolders")] WorkspaceFolder[]? WorkspaceFolders,
        [property: JsonPropertyName("capabilities")] object Capabilities,
        [property: JsonPropertyName("clientInfo")] ClientInfo ClientInfo);

    public record ClientInfo(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("version")] string Version);

    public record InitializeResult(
        [property: JsonPropertyName("capabilities")] object? Capabilities,
        [property: JsonPropertyName("serverInfo")] ServerInfo? ServerInfo);

    public record ServerInfo(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("version")] string? Version);

    public record CallHierarchyItem(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("kind")] int Kind,
        [property: JsonPropertyName("uri")] string Uri,
        [property: JsonPropertyName("range")] Range Range,
        [property: JsonPropertyName("selectionRange")] Range SelectionRange,
        [property: JsonPropertyName("detail")] string? Detail,
        [property: JsonPropertyName("tags")] int[]? Tags,
        [property: JsonPropertyName("data")] object? Data);

    public record CallHierarchyIncomingCall(
        [property: JsonPropertyName("from")] CallHierarchyItem From,
        [property: JsonPropertyName("fromRanges")] Range[] FromRanges);

    public record CallHierarchyOutgoingCall(
        [property: JsonPropertyName("to")] CallHierarchyItem To,
        [property: JsonPropertyName("fromRanges")] Range[] FromRanges);

    public record CallHierarchyPrepareParams(
        [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
        [property: JsonPropertyName("position")] Position Position);

    public record CallHierarchyIncomingCallsParams(
        [property: JsonPropertyName("item")] CallHierarchyItem Item);

    public record CallHierarchyOutgoingCallsParams(
        [property: JsonPropertyName("item")] CallHierarchyItem Item);

    /// <summary>
    /// LSP SymbolKind values we care about. Full enum has 26 values — these are
    /// the ones the TS backend needs for class/method/function disambiguation.
    /// </summary>
    public static class SymbolKind
    {
        public const int Class       = 5;
        public const int Method      = 6;
        public const int Property    = 7;
        public const int Constructor = 9;
        public const int Interface   = 11;
        public const int Function    = 12;
        public const int Variable    = 13;
        public const int Constant    = 14;
    }

    /// <summary>
    /// Converts an absolute file path to an LSP file:// URI. LSP servers reject
    /// raw paths even on Windows; the scheme is required.
    /// </summary>
    public static string PathToUri(string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath).Replace('\\', '/');
        if (!full.StartsWith('/')) full = "/" + full;
        return "file://" + Uri.EscapeDataString(full).Replace("%2F", "/").Replace("%3A", ":");
    }

    public static string UriToPath(string uri)
    {
        if (!uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return uri;
        var stripped = uri["file://".Length..];
        var decoded = Uri.UnescapeDataString(stripped);
        // Windows: file:///C:/... → /C:/foo → C:\foo
        if (decoded.Length >= 3 && decoded[0] == '/' && decoded[2] == ':')
            decoded = decoded[1..];
        return decoded.Replace('/', Path.DirectorySeparatorChar);
    }
}
