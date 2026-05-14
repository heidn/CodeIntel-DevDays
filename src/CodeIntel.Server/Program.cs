using CodeIntel.Server.Data;
using CodeIntel.Server.Hubs;
using CodeIntel.Server.Models;
using CodeIntel.Server.Services;
using CodeIntel.Server.Services.LanguageBackends;
using CodeIntel.Server.Services.LanguageBackends.Lsp;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// --- Logging ---
builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .WriteTo.Console()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning));

// --- Options ---
builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection("Llm"));
builder.Services.Configure<AnalysisOptions>(builder.Configuration.GetSection("Analysis"));
builder.Services.Configure<DataOptions>(builder.Configuration.GetSection("Data"));
builder.Services.Configure<LspOptions>(builder.Configuration.GetSection("Lsp"));

// --- Services ---
builder.Services.AddSingleton<CodeIntelDb>();
builder.Services.AddSingleton<IWorkspaceService, WorkspaceService>();
builder.Services.AddSingleton<ILlmService, LlamaSharpService>();
builder.Services.AddSingleton<IAnalysisResultStore, SqliteAnalysisResultStore>();
builder.Services.AddSingleton<ISkillRouter, SkillRouter>();
builder.Services.AddSingleton<IPromptTemplateService, PromptTemplateService>();
builder.Services.AddSingleton<IReportGenerator, ReportGenerator>();
builder.Services.AddSingleton<IReportWriter, ReportWriter>();
builder.Services.AddSingleton<IAnalysisCancellationRegistry, AnalysisCancellationRegistry>();
builder.Services.AddScoped<IAnalysisEstimator, AnalysisEstimator>();
builder.Services.AddScoped<IResultCache, ResultCache>();
builder.Services.AddScoped<IIgnoredFindingsStore, IgnoredFindingsStore>();
builder.Services.AddSingleton<IPlSqlObjectParser, PlSqlObjectParser>();
builder.Services.AddSingleton<IPlSqlRepoResolver, PlSqlRepoResolver>();
builder.Services.AddScoped<IContextBuilder, ContextBuilder>();
builder.Services.AddScoped<IContextRequestHandler, ContextRequestHandler>();
builder.Services.AddScoped<IAnalysisOrchestrator, InvestigationOrchestrator>();
builder.Services.AddSingleton<ITraceResultStore, SqliteTraceResultStore>();
builder.Services.AddScoped<ITraceWalker, TraceWalker>();
builder.Services.AddScoped<ITraceOrchestrator, TraceOrchestrator>();
builder.Services.AddSingleton<ICSharpMetricsAnalyzer, CSharpMetricsAnalyzer>();
builder.Services.AddSingleton<IPlSqlMetricsAnalyzer, PlSqlMetricsAnalyzer>();
builder.Services.AddScoped<IMetricsService, MetricsService>();

// --- B1: language backend abstraction ---
// Each backend is a singleton, registered both under its concrete type (so
// WorkspaceService/ContextRequestHandler can grab the C#/PL-SQL ones directly
// for legacy paths) and under ILanguageBackend (so the registry can enumerate
// them). The registry is the dispatch layer that picks a backend per workspace.
builder.Services.AddSingleton<CSharpRoslynBackend>();
builder.Services.AddSingleton<PlSqlBackend>();
builder.Services.AddSingleton<JavaBackend>();
builder.Services.AddSingleton<TypeScriptLspBackend>();
builder.Services.AddSingleton<ILanguageBackend>(sp => sp.GetRequiredService<CSharpRoslynBackend>());
builder.Services.AddSingleton<ILanguageBackend>(sp => sp.GetRequiredService<PlSqlBackend>());
builder.Services.AddSingleton<ILanguageBackend>(sp => sp.GetRequiredService<JavaBackend>());
builder.Services.AddSingleton<ILanguageBackend>(sp => sp.GetRequiredService<TypeScriptLspBackend>());
builder.Services.AddSingleton<ILanguageBackendRegistry, LanguageBackendRegistry>();

// LSP session manager. Picks the real LspSessionManager (spawns child processes
// per-workspace) when Lsp:Enabled=true (default). Falls back to the no-op stub
// when LSP is disabled, in which case TS workspaces still load and the file
// tree + raw analysis still work, but trace/callers/definition for TS are no-ops.
{
    var lspEnabled = builder.Configuration.GetSection("Lsp").GetValue("Enabled", defaultValue: true);
    if (lspEnabled)
        builder.Services.AddSingleton<ILspSessionManager, LspSessionManager>();
    else
        builder.Services.AddSingleton<ILspSessionManager, NullLspSessionManager>();
}

// --- Web ---
builder.Services.AddControllers()
    .AddJsonOptions(opts =>
    {
        opts.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        opts.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(
            System.Text.Json.JsonNamingPolicy.CamelCase));
    });
builder.Services.AddSignalR()
    .AddJsonProtocol(opts =>
    {
        opts.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        opts.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(
            System.Text.Json.JsonNamingPolicy.CamelCase));
    });

builder.Services.AddCors(opts =>
{
    opts.AddDefaultPolicy(p => p
        .WithOrigins("http://localhost:5173", "http://localhost:5174")
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

// C2: rate limiting on POST /api/analysis/run and POST /api/trace/run so a buggy
// retry loop can't OOM the result store or pile inference jobs on the LLM lock.
builder.Services.AddRateLimiter(opts =>
{
    var analysisOpts = builder.Configuration.GetSection("Analysis").Get<AnalysisOptions>() ?? new AnalysisOptions();
    var perMinute = Math.Max(1, analysisOpts.RateLimitRunsPerMinute);

    opts.AddPolicy("analysis-run", ctx =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
            factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = perMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    opts.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.StatusCode = 429;
        await ctx.HttpContext.Response.WriteAsJsonAsync(new
        {
            error = "Too many runs in the last minute. Slow down or raise Analysis.RateLimitRunsPerMinute.",
        }, ct);
    };
});

var app = builder.Build();

// --- Eager-load LLM model on startup (fire-and-forget, doesn't block startup) ---
_ = Task.Run(async () =>
{
    using var scope = app.Services.CreateScope();
    var llm = scope.ServiceProvider.GetRequiredService<ILlmService>();
    try
    {
        await llm.InitializeAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Failed to initialize LLM on startup");
    }
});

// --- Middleware ---
app.UseSerilogRequestLogging();
app.UseCors();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

app.MapControllers();
app.MapHub<AnalysisHub>("/hubs/analysis");

// SPA fallback for production
if (!app.Environment.IsDevelopment())
{
    app.MapFallbackToFile("index.html");
}

app.Run();
