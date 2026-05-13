using CodeIntel.Server.Hubs;
using CodeIntel.Server.Models;
using CodeIntel.Server.Services;
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

// --- Services ---
builder.Services.AddSingleton<IWorkspaceService, WorkspaceService>();
builder.Services.AddSingleton<ILlmService, LlamaSharpService>();
builder.Services.AddSingleton<IAnalysisResultStore, InMemoryAnalysisResultStore>();
builder.Services.AddSingleton<IPromptTemplateService, PromptTemplateService>();
builder.Services.AddSingleton<IReportGenerator, ReportGenerator>();
builder.Services.AddSingleton<IReportWriter, ReportWriter>();
builder.Services.AddSingleton<IAnalysisCancellationRegistry, AnalysisCancellationRegistry>();
builder.Services.AddSingleton<IPlSqlObjectParser, PlSqlObjectParser>();
builder.Services.AddSingleton<IPlSqlRepoResolver, PlSqlRepoResolver>();
builder.Services.AddScoped<IContextBuilder, ContextBuilder>();
builder.Services.AddScoped<IContextRequestHandler, ContextRequestHandler>();
builder.Services.AddScoped<IAnalysisOrchestrator, InvestigationOrchestrator>();
builder.Services.AddSingleton<ITraceResultStore, InMemoryTraceResultStore>();
builder.Services.AddScoped<ITraceWalker, TraceWalker>();
builder.Services.AddScoped<ITraceOrchestrator, TraceOrchestrator>();

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
