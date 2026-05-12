using System.Reflection;
using System.Text;
using CodeIntel.Server.Models;

namespace CodeIntel.Server.Services;

public record PresetInfo(string Key, string Name, string Description, string Icon);

public record ConversationTurn(string Role, string Content);

public interface IPromptTemplateService
{
    IReadOnlyList<PresetInfo> GetPresets();
    string BuildPrompt(AnalysisRequest request, CodeContext context);
    string BuildAgentPrompt(AnalysisRequest request, CodeContext context);
    string BuildContinuationPrompt(
        AnalysisRequest request,
        CodeContext initialContext,
        IReadOnlyList<ConversationTurn> history,
        IReadOnlyList<ContextFulfillment> fulfillments);
}

public class PromptTemplateService : IPromptTemplateService
{
    private readonly Dictionary<string, string> _templates = new();

    private readonly List<PresetInfo> _presets = new()
    {
        new("find-dead-code", "Find Dead Code",
            "Identify methods, classes, or variables that appear unused.", "skull"),
        new("find-bugs", "Find Bugs",
            "Look for null risks, missing error handling, race conditions, and edge cases.", "bug"),
        new("find-business-rules", "Find Business Rules",
            "Extract validation, calculation, and workflow rules from the code.", "scale"),
        new("summarize", "Summarize",
            "Produce a plain-English summary of what the code does.", "book"),
    };

    public PromptTemplateService()
    {
        LoadEmbeddedTemplates();
    }

    public IReadOnlyList<PresetInfo> GetPresets() => _presets;

    public string BuildPrompt(AnalysisRequest request, CodeContext context)
    {
        var systemPrompt = BuildSystemPrompt(context.Language);
        var taskBlock = request.Mode switch
        {
            AnalysisMode.Preset => GetTemplate(request.PresetKey ?? throw new InvalidOperationException("Preset key required")),
            AnalysisMode.FreeText => BuildFreeTextTask(request.FreeTextPrompt ?? ""),
            _ => throw new InvalidOperationException($"Unknown mode {request.Mode}")
        };

        var contextBlock = BuildContextBlock(context);

        // Qwen2.5 uses ChatML format
        var sb = new StringBuilder();
        sb.Append("<|im_start|>system\n");
        sb.Append(systemPrompt);
        sb.Append("\n<|im_end|>\n");
        sb.Append("<|im_start|>user\n");
        sb.Append(taskBlock);
        if (request.PinnedSnippet is not null)
        {
            sb.Append("\n\n");
            sb.Append(BuildSnippetBlock(request.PinnedSnippet));
        }
        sb.Append("\n\n");
        sb.Append(contextBlock);
        sb.Append("\n<|im_end|>\n");
        sb.Append("<|im_start|>assistant\n");
        return sb.ToString();
    }

    private string GetTemplate(string presetKey)
    {
        if (_templates.TryGetValue(presetKey, out var template))
            return template;
        throw new InvalidOperationException($"Unknown preset: {presetKey}");
    }

    public string BuildAgentPrompt(AnalysisRequest request, CodeContext context)
    {
        var systemPrompt = BuildAgentSystemPrompt(context.Language);
        var taskBlock = request.Mode switch
        {
            AnalysisMode.Preset => GetTemplate(request.PresetKey ?? throw new InvalidOperationException("Preset key required")),
            AnalysisMode.FreeText => BuildFreeTextTask(request.FreeTextPrompt ?? ""),
            _ => throw new InvalidOperationException($"Unknown mode {request.Mode}")
        };
        var contextBlock = BuildContextBlock(context);

        var sb = new StringBuilder();
        sb.Append("<|im_start|>system\n");
        sb.Append(systemPrompt);
        sb.Append("\n<|im_end|>\n");
        sb.Append("<|im_start|>user\n");
        sb.Append(taskBlock);
        if (request.PinnedSnippet is not null)
        {
            sb.Append("\n\n");
            sb.Append(BuildSnippetBlock(request.PinnedSnippet));
        }
        sb.Append("\n\n");
        sb.Append(contextBlock);
        sb.Append("\n<|im_end|>\n");
        sb.Append("<|im_start|>assistant\n");
        return sb.ToString();
    }

    public string BuildContinuationPrompt(
        AnalysisRequest request,
        CodeContext initialContext,
        IReadOnlyList<ConversationTurn> history,
        IReadOnlyList<ContextFulfillment> fulfillments)
    {
        var systemPrompt = BuildAgentSystemPrompt(initialContext.Language);
        var taskBlock = request.Mode switch
        {
            AnalysisMode.Preset => GetTemplate(request.PresetKey ?? throw new InvalidOperationException("Preset key required")),
            AnalysisMode.FreeText => BuildFreeTextTask(request.FreeTextPrompt ?? ""),
            _ => throw new InvalidOperationException($"Unknown mode {request.Mode}")
        };
        var contextBlock = BuildContextBlock(initialContext);

        var sb = new StringBuilder();
        sb.Append("<|im_start|>system\n");
        sb.Append(systemPrompt);
        sb.Append("\n<|im_end|>\n");
        sb.Append("<|im_start|>user\n");
        sb.Append(taskBlock);
        if (request.PinnedSnippet is not null)
        {
            sb.Append("\n\n");
            sb.Append(BuildSnippetBlock(request.PinnedSnippet));
        }
        sb.Append("\n\n");
        sb.Append(contextBlock);
        sb.Append("\n<|im_end|>\n");

        foreach (var turn in history)
        {
            sb.Append($"<|im_start|>{turn.Role}\n");
            sb.Append(turn.Content);
            sb.Append("\n<|im_end|>\n");
        }

        // new user message with fulfilled context
        sb.Append("<|im_start|>user\n");
        sb.AppendLine("Here is the additional context you requested:");
        sb.AppendLine();
        foreach (var f in fulfillments)
        {
            sb.AppendLine($"// Requested: {f.Request.Type} '{f.Request.Target}'");
            sb.AppendLine(f.Content);
        }
        sb.AppendLine();
        sb.AppendLine("Continue your analysis from where you left off. Do not repeat findings you have already emitted.");
        sb.Append("\n<|im_end|>\n");
        sb.Append("<|im_start|>assistant\n");
        return sb.ToString();
    }

    private static string BuildSystemPrompt(Language language)
    {
        var persona = language switch
        {
            Language.TypeScript => "senior TypeScript / JavaScript developer",
            Language.Java => "senior Java developer",
            Language.Sql => "senior database developer and SQL / PL/SQL expert",
            _ => "senior C# / .NET code reviewer",
        };
        return $$"""
        You are a {{persona}} analyzing source code for a developer tool.

        For structured findings, emit each finding as a single-line JSON object wrapped in <finding>...</finding> tags:
        <finding>{"severity":"bug|warning|suggestion|info|deadcode","title":"...","description":"...","filePath":"...","lineNumber":42,"codeSnippet":"..."}</finding>

        Rules:
        - Output one JSON object per finding, each on its own line, wrapped in <finding> tags.
        - Outside the tags, you may write a brief plain-text intro and conclusion.
        - Use null (not omission) for missing optional fields.
        - Be specific. "Maybe a problem" is not a finding. Either you see it or you don't.
        - When you are done, write <done /> on its own line.
        """;
    }

    private static string BuildAgentSystemPrompt(Language language)
    {
        var persona = language switch
        {
            Language.TypeScript => "senior TypeScript / JavaScript developer",
            Language.Java => "senior Java developer",
            Language.Sql => "senior database developer and SQL / PL/SQL expert",
            _ => "senior C# / .NET code reviewer",
        };
        return $$"""
        You are a {{persona}} performing an in-depth investigation of a codebase using a developer tool.

        For structured findings, emit each finding as a single-line JSON object wrapped in <finding>...</finding> tags:
        <finding>{"severity":"bug|warning|suggestion|info|deadcode","title":"...","description":"...","filePath":"...","lineNumber":42,"codeSnippet":"..."}</finding>

        If you need to see additional code to complete your analysis, emit a context request:
        <request_context type="file">path/to/File.cs</request_context>
        <request_context type="class">ClassName</request_context>
        <request_context type="method">MethodName</request_context>
        <request_context type="search_code">search term or pattern</request_context>
        <request_context type="callers_of">MethodName</request_context>

        Rules:
        - Emit findings as you discover them; don't wait until the end.
        - Request context only when you cannot confidently answer from the code you already have.
        - You may request up to 3 items per turn. The tool will provide the code and continue.
        - When you are done and have no more requests, write <done /> on its own line.
        - Be specific. "Maybe a problem" is not a finding. Either you see it or you don't.
        """;
    }

    private static string BuildFreeTextTask(string userQuestion) =>
        $$"""
        The developer is asking the following question about the code below.

        Question:
        {{userQuestion}}

        Answer the question directly and concisely. If you identify specific issues or
        observations worth highlighting, emit them as <finding> JSON objects as described
        in the system prompt. Otherwise, plain prose is fine.
        """;

    private static string BuildContextBlock(CodeContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- BEGIN CODE CONTEXT ---");
        foreach (var file in context.Files)
        {
            sb.AppendLine();
            sb.AppendLine($"// FILE: {file.RelativePath}");
            sb.AppendLine($"```{FileLang(file.RelativePath)}");
            sb.AppendLine(file.Content);
            sb.AppendLine("```");
        }
        sb.AppendLine();
        sb.AppendLine("--- END CODE CONTEXT ---");
        return sb.ToString();
    }

    private static string FileLang(string? path) =>
        Path.GetExtension(path)?.ToLowerInvariant() switch
        {
            ".cs"                                   => "csharp",
            ".sql" or ".pkb" or ".pkg" or ".pks" or ".pls" => "sql",
            ".ts" or ".tsx"                         => "typescript",
            ".js" or ".jsx"                         => "javascript",
            ".py"                                   => "python",
            ".java"                                 => "java",
            _                                       => ""
        };

    private static string BuildSnippetBlock(PinnedSnippet snippet)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- FOCUS AREA ---");
        sb.AppendLine("The developer has pinned this code region for priority analysis.");
        sb.AppendLine("Pay close attention to this specific section:");
        sb.AppendLine();
        sb.AppendLine($"// FILE: {snippet.AbsolutePath}, lines {snippet.StartLine}–{snippet.EndLine}");
        sb.AppendLine("```");
        sb.AppendLine(snippet.Text);
        sb.AppendLine("```");
        sb.Append("--- END FOCUS AREA ---");
        return sb.ToString();
    }

    private void LoadEmbeddedTemplates()
    {
        var asm = Assembly.GetExecutingAssembly();
        var resourceNames = asm.GetManifestResourceNames();
        var prefix = "CodeIntel.Server.Prompts.";

        foreach (var name in resourceNames.Where(n => n.StartsWith(prefix) && n.EndsWith(".md")))
        {
            using var stream = asm.GetManifestResourceStream(name);
            if (stream == null) continue;
            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();

            var key = name.Substring(prefix.Length, name.Length - prefix.Length - 3); // strip ".md"
            _templates[key] = content;
        }

        // Fallback: if embedded resources aren't found (dev scenarios), load from disk
        if (_templates.Count == 0)
        {
            var promptsDir = Path.Combine(AppContext.BaseDirectory, "Prompts");
            if (Directory.Exists(promptsDir))
            {
                foreach (var file in Directory.GetFiles(promptsDir, "*.md"))
                {
                    var key = Path.GetFileNameWithoutExtension(file);
                    _templates[key] = File.ReadAllText(file);
                }
            }
        }
    }
}
