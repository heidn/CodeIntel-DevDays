using CodeIntel.Server.Models;
using CodeIntel.Server.Services;
using CodeIntel.Server.Services.LanguageBackends;
using CodeIntel.Server.Services.LanguageBackends.Lsp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeIntel.Server.Tests;

/// <summary>
/// Builds a minimal <see cref="IServiceProvider"/> for unit tests that exercise
/// <see cref="WorkspaceService"/>. The full DI graph in Program.cs is too heavy
/// for unit tests, so this helper wires up just the language-backend pieces
/// they need: registry, all four backends, plus the PL/SQL resolver and metrics
/// analyzers behind them. No SQLite, no SignalR, no LLM.
/// </summary>
internal static class TestWorkspaceHarness
{
    public static (WorkspaceService Service, IServiceProvider Services) Build(
        AnalysisOptions? options = null)
    {
        var collection = new ServiceCollection();
        collection.AddSingleton(Options.Create(options ?? new AnalysisOptions()));
        collection.AddSingleton(NullLoggerFactory.Instance);
        collection.AddLogging();

        collection.AddSingleton<IPlSqlObjectParser, PlSqlObjectParser>();
        collection.AddSingleton<IPlSqlRepoResolver, PlSqlRepoResolver>();
        collection.AddSingleton<ICSharpMetricsAnalyzer, CSharpMetricsAnalyzer>();
        collection.AddSingleton<IPlSqlMetricsAnalyzer, PlSqlMetricsAnalyzer>();

        collection.AddSingleton<CSharpRoslynBackend>();
        collection.AddSingleton<PlSqlBackend>();
        collection.AddSingleton<JavaBackend>();
        collection.AddSingleton<TypeScriptLspBackend>();
        collection.AddSingleton<ILanguageBackend>(sp => sp.GetRequiredService<CSharpRoslynBackend>());
        collection.AddSingleton<ILanguageBackend>(sp => sp.GetRequiredService<PlSqlBackend>());
        collection.AddSingleton<ILanguageBackend>(sp => sp.GetRequiredService<JavaBackend>());
        collection.AddSingleton<ILanguageBackend>(sp => sp.GetRequiredService<TypeScriptLspBackend>());
        collection.AddSingleton<ILanguageBackendRegistry, LanguageBackendRegistry>();
        collection.AddSingleton<ILspSessionManager, NullLspSessionManager>();

        collection.AddSingleton<IWorkspaceService, WorkspaceService>();

        var sp = collection.BuildServiceProvider();
        return ((WorkspaceService)sp.GetRequiredService<IWorkspaceService>(), sp);
    }
}
