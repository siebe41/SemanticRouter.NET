using RoutingApiSample;
using SemanticRouter;
using SemanticRouter.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();

// Routes, thresholds, embedding provider, middleware mode and upstream endpoints all come from
// the "SemanticRouter" section of appsettings.json (and environment-specific overrides).
builder.Services.AddSemanticPromptRouting(builder.Configuration.GetSection(SemanticRouterOptions.SectionName));

// In Annotate mode the middleware only classifies; endpoints pick a backend themselves.
// Keyed services make "one client per RouteTarget" explicit. Swap these simulators for real clients.
builder.Services.AddKeyedSingleton<IChatBackend>(RouteTarget.Local, new SimulatedChatBackend("local-slm"));
builder.Services.AddKeyedSingleton<IChatBackend>(RouteTarget.Cloud, new SimulatedChatBackend("cloud-frontier"));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Classifies POSTed prompts. In Dispatch mode (see appsettings.Dispatch.json) it proxies matching
// requests straight to Ollama / the cloud provider and the endpoint below is never reached.
app.UsePromptRouter();

app.MapPost("/v1/chat/completions", async (
        ChatCompletionRequest request,
        HttpContext context,
        IServiceProvider services,
        CancellationToken cancellationToken) =>
    {
        PromptClassificationResult? routing = context.GetPromptRouting();
        RouteTarget target = routing?.Target ?? RouteTarget.Cloud;
        IChatBackend backend = services.GetRequiredKeyedService<IChatBackend>(target);

        return TypedResults.Ok(await backend.CompleteAsync(request, routing, cancellationToken));
    })
    .WithName("ChatCompletions")
    .WithSummary("OpenAI-compatible chat endpoint served by the backend the router selected.");

// POST { "prompt": "..." } to see the full decision: candidates, similarity and complexity signals.
app.MapPromptClassification();

app.MapGet("/", () => TypedResults.Ok(new
{
    service = "SemanticRouter sample",
    mode = app.Configuration["SemanticRouter:Middleware:Mode"] ?? "Annotate",
    endpoints = new[] { "POST /v1/chat/completions", "POST /semantic-router/classify" },
}));

app.Run();
