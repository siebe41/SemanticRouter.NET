using LmStudioClaudePoc;
using SemanticRouter;

// Proof of concept: route each prompt to LM Studio (easy, private) or the Claude Code CLI (hard).
//   dotnet run                → preflight, then serve http://localhost:5090/v1/chat/completions
//   dotnet run -- --demo      → preflight, start, run the self-test, exit (code 0 = everything worked)
bool demo = args.Contains("--demo");
bool skipPreflight = args.Contains("--skip-preflight");
string[] hostArgs = args.Where(a => a is not ("--demo" or "--skip-preflight")).ToArray();

Console.OutputEncoding = System.Text.Encoding.UTF8;
var builder = WebApplication.CreateBuilder(hostArgs);

if (skipPreflight)
{
    await Preflight.ResolveLmStudioModelAsync(builder.Configuration);
}
else if (!await Preflight.RunAsync(builder.Configuration) && demo)
{
    Console.WriteLine("Fix the [FAIL] items above, then run the demo again (or pass --skip-preflight).");
    return 2;
}

if (demo)
{
    // Keep the demo's table readable: only warnings (failovers, CLI errors) from the router and host.
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.Logging.AddFilter("SemanticRouter", LogLevel.Warning);
    builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
}

builder.Services
    .AddSemanticPromptRouting(builder.Configuration.GetSection(SemanticRouterOptions.SectionName))
    .UseConfigurationFile("semantic-router.json")    // dashboard edits are saved here and survive restarts
    .UseSecretFile();                                 // keys typed into the dashboard are encrypted into semantic-router.secrets.json

var app = builder.Build();
app.UsePromptRouter();                // serves POST /v1/chat/completions (OpenAI) and POST /v1/messages (Anthropic / Claude Code)
app.MapRouterModels();                // GET /v1/models
app.MapAnthropicTokenCount();         // POST /v1/messages/count_tokens
app.MapPromptClassification();        // POST /semantic-router/classify {"prompt": "..."}: explains a decision, calls no model
app.MapRouterDashboard("/router");    // the dashboard: status, activity and every setting, with hot reload

await app.StartAsync();
string baseUrl = app.Urls.First();

if (demo)
{
    int exitCode = await Demo.RunAsync(baseUrl);
    await app.StopAsync();
    return exitCode;
}

Console.WriteLine($"Router ready. Dashboard: {baseUrl}/router");
Console.WriteLine($"  OpenAI clients:  base URL {baseUrl}/v1, model \"auto\" (or \"lmstudio\" / \"claude\")");
Console.WriteLine($"  Claude Code:     ANTHROPIC_BASE_URL={baseUrl} ANTHROPIC_AUTH_TOKEN=semantic-router claude");
Console.WriteLine("Each request is logged with the tier, route and model chosen. Ctrl+C to stop.");
await app.WaitForShutdownAsync();
return 0;
