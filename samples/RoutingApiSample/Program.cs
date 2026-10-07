using RoutingApiSample;
using SemanticRouter;
using SemanticRouter.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();

// Routes, the model catalog, connectors, embeddings and the middleware mode all come from the
// "SemanticRouter" section of appsettings.json (plus appsettings.{Environment}.json overrides).
builder.Services.AddSemanticPromptRouting(builder.Configuration.GetSection(SemanticRouterOptions.SectionName));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Annotate mode (default): classifies the prompt, ranks the catalog and lets the endpoint below decide.
// Dispatch mode (appsettings.Dispatch.json): serves /v1/chat/completions itself through the connectors
// (LM Studio, Claude Code CLI, …) with failover, so the endpoint below is never reached.
app.UsePromptRouter();

app.MapPost("/v1/chat/completions", (ChatCompletionBody request, HttpContext context) =>
    {
        RoutingPlan? plan = context.GetPromptRouting();
        return TypedResults.Ok(SimulatedCompletion.Create(request, plan));
    })
    .WithName("ChatCompletions")
    .WithSummary("Annotate-mode demo: reports which model the router would use instead of calling it.");

// GET /v1/models lists the routing aliases (auto, auto:cheapest, auto:best, auto:priority) and the catalog,
// including any model that is cooling down after hitting a quota.
app.MapRouterModels();

// POST { "prompt": "..." } explains a decision: tier, reason, route candidates and the ranked models.
app.MapPromptClassification();

app.MapGet("/", () => TypedResults.Ok(new
{
    service = "SemanticRouter sample",
    mode = app.Configuration["SemanticRouter:Middleware:Mode"] ?? "Annotate",
    endpoints = new[] { "POST /v1/chat/completions", "GET /v1/models", "POST /semantic-router/classify" },
}));

app.Run();
