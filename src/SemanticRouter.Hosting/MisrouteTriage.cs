using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SemanticRouter.AspNetCore;
using SemanticRouter.Embeddings.Onnx;

namespace SemanticRouter.Host;

/// <summary>Whether re-running the paraphrase on the template gives the decision the report shows.</summary>
internal enum MisrouteReproduction
{
    /// <summary>No paraphrase, or no tier in the reported decision, so nothing was compared.</summary>
    NotChecked = 0,

    /// <summary>Same tier, and the same route (or none on both).</summary>
    Reproduced = 1,

    /// <summary>Same tier, another route.</summary>
    SameTier = 2,

    /// <summary>Another tier.</summary>
    Different = 3,
}

/// <summary>Where the tier the evaluation line asks for came from.</summary>
internal enum ExpectedTierSource
{
    /// <summary>No tier could be worked out.</summary>
    None = 0,

    /// <summary>The report names it (<c>Tier 2</c>).</summary>
    Stated = 1,

    /// <summary>The report names the tier's name (<c>Local coder / mini</c>).</summary>
    TierName = 2,

    /// <summary>One tier below (too high) or above (too low) the reported one, since the report names none.</summary>
    Direction = 3,
}

/// <summary>One of the closest routes.</summary>
internal sealed record TriageRouteScore(string Route, int Tier, float Similarity);

/// <summary>What the template decides for the paraphrase.</summary>
internal sealed record TriageDecision
{
    public required int Tier { get; init; }
    public required RoutingReason Reason { get; init; }
    public string? Route { get; init; }
    public float? Similarity { get; init; }
    public required double Complexity { get; init; }
    public required IReadOnlyList<TriageRouteScore> Closest { get; init; }
}

/// <summary>How an evaluation set fared.</summary>
internal sealed record TriageAccuracy
{
    public required int Total { get; init; }
    public required int Correct { get; init; }
    public double Accuracy => Total == 0 ? 0 : (double)Correct / Total;
}

/// <summary>The best default threshold for the set with the new line, and what it does to that line.</summary>
internal sealed record TriageCalibration
{
    public required float Threshold { get; init; }
    public required TriageAccuracy Result { get; init; }
    public required int NewLineTier { get; init; }
}

/// <summary>Everything the triage found, for the comment and for <c>--json</c>.</summary>
internal sealed record MisrouteTriageResult
{
    public int? Issue { get; init; }
    public string? ReportedVersion { get; init; }
    public required int Template { get; init; }
    public required string EmbeddingProvider { get; init; }
    public string? EmbeddingModel { get; init; }
    public required float Threshold { get; init; }
    public int? ReportedTier { get; init; }
    public RoutingReason? ReportedReason { get; init; }
    public string? ReportedRoute { get; init; }
    public float? ReportedSimilarity { get; init; }
    public MisrouteDirection Direction { get; init; }
    public bool HasPrompt { get; init; }
    public TriageDecision? Rerun { get; init; }
    public required MisrouteReproduction Reproduction { get; init; }
    public int? ExpectedTier { get; init; }
    public required ExpectedTierSource ExpectedTierSource { get; init; }
    public string? ExpectedRoute { get; init; }
    public string? EvaluationLine { get; init; }
    public required TriageAccuracy Without { get; init; }
    public TriageAccuracy? With { get; init; }
    public bool? NewLineCorrect { get; init; }
    public TriageCalibration? Calibration { get; init; }
    public IReadOnlyList<string> Missing { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>What the command line asks of the triage, besides the report.</summary>
internal sealed record MisrouteTriageOptions
{
    /// <summary>Use the template of this release instead of the reported one.</summary>
    public string? RouterVersion { get; init; }

    /// <summary><c>Hashing</c>, <c>Onnx</c> or <c>Onnx:&lt;model&gt;</c> instead of the reported embeddings.</summary>
    public string? Embeddings { get; init; }

    /// <summary>The issue number, for the evaluation line's note.</summary>
    public int? Issue { get; init; }

    /// <summary>Where ONNX models are kept (and downloaded to, the first time).</summary>
    public required string ModelsDirectory { get; init; }
}

/// <summary>
/// <c>semantic-router triage-misroute &lt;issue-body | -&gt;</c>: reads a <b>Routed to the wrong tier</b> report, re-runs its
/// paraphrased prompt through classification on the built-in template of the reported release, proposes an evaluation
/// line with the tier the reporter says it needed, and runs the template's evaluation set with and without that line. It
/// prints a Markdown comment for the issue (or JSON). Deterministic, and it calls no model: the report is someone else's
/// text, so it is only read as data (<see cref="MisrouteReport"/>) and only written back inside code spans and blocks
/// (<see cref="UntrustedText"/>). The only network use is the first download of an ONNX embedding model.
/// </summary>
internal static partial class MisrouteTriage
{
    /// <summary>The first line of every comment, so a workflow can tell it already commented.</summary>
    public const string Marker = "<!-- semantic-router:misroute-triage -->";

    /// <summary>
    /// The built-in template each release shipped, by the first release that shipped it (see
    /// <see cref="TemplateMigration.Template"/>). A new template adds a line here with the release that ships it.
    /// </summary>
    internal static readonly IReadOnlyList<(Version From, int Template)> Releases = [(new Version(0, 1, 0), 1), (new Version(0, 3, 0), 2)];

    public static async Task<int> RunAsync(HostSettings settings, TextReader standardInput, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? body = await ReadAsync(settings.TriageInput!, standardInput, error).ConfigureAwait(false);
        if (body is null)
        {
            return 1;
        }

        MisrouteReport report = MisrouteReport.Parse(body);
        if (report.Sections == 0)
        {
            await error.WriteLineAsync($"This is not a \"Routed to the wrong tier\" report: it has none of the form's headings (### {MisrouteReport.DecisionLabel}, …).").ConfigureAwait(false);
            return 1;
        }

        MisrouteTriageResult result = await TriageAsync(report, new MisrouteTriageOptions
        {
            RouterVersion = settings.TriageRouterVersion,
            Embeddings = settings.TriageEmbeddings,
            Issue = settings.TriageIssue,
            ModelsDirectory = LocalData.ModelsDirectory(settings),
        }, cancellationToken).ConfigureAwait(false);

        await output.WriteAsync(settings.Json ? ToJson(result) : ToMarkdown(result)).ConfigureAwait(false);
        return 0;
    }

    /// <summary>The body from a file or standard input (<c>-</c>), at most a little more than the longest one read.</summary>
    private static async Task<string?> ReadAsync(string input, TextReader standardInput, TextWriter error)
    {
        try
        {
            using TextReader? file = input == "-" ? null : new StreamReader(input, Encoding.UTF8);
            TextReader reader = file ?? standardInput;
            char[] buffer = new char[MisrouteReport.MaxBodyLength + 1];
            int read = 0, count;
            while (read < buffer.Length && (count = await reader.ReadAsync(buffer.AsMemory(read)).ConfigureAwait(false)) > 0)
            {
                read += count;
            }

            return new string(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await error.WriteLineAsync($"Cannot read the issue body: {ex.Message}").ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>Re-runs the report on the template and proposes its evaluation line.</summary>
    public static async Task<MisrouteTriageResult> TriageAsync(MisrouteReport report, MisrouteTriageOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(options);
        var notes = new List<string>();
        string? version = options.RouterVersion ?? report.Version;
        int templateNumber = TemplateFor(MisrouteReport.Release(version), notes);
        RouterConfiguration template = RouterConfiguration.FromJson(TemplateMigration.Template(templateNumber));
        IReadOnlyList<EvaluationCase> set = EvaluationSet.Parse(TemplateMigration.EvaluationTemplate(templateNumber));

        (string provider, string? model) = ChooseEmbeddings(report, options.Embeddings, notes);
        float threshold = ApplyThreshold(report, template, provider, model, notes);
        NoteWhatTheTemplateCannotShow(report, template, notes);

        (int? expectedTier, ExpectedTierSource source) = ExpectedTier(report, template);
        string? expectedRoute = expectedTier is { } tier ? ExpectedRoute(report, template, tier, notes) : null;
        EvaluationCase? line = report.Prompt is { } prompt && expectedTier is { } expected
            ? new EvaluationCase
            {
                Prompt = prompt,
                ExpectedTier = expected,
                ExpectedRoute = expectedRoute,
                Note = options.Issue is { } issue ? $"misroute report #{issue.ToString(CultureInfo.InvariantCulture)}" : "misroute report",
            }
            : null;

        Runs runs;
        try
        {
            runs = await RunAsync(provider, model, template, set, report, line, options.ModelsDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (provider == OnnxEmbeddings.Provider && ex is InvalidOperationException or HttpRequestException or IOException)
        {
            notes.Add($"The ONNX model could not be loaded ({UntrustedText.Code(ex.Message, 200)}), so the Hashing embeddings were used, at the template's threshold.");
            (provider, model) = (EmbeddingProviderKinds.Hashing, null);
            template = RouterConfiguration.FromJson(TemplateMigration.Template(templateNumber));
            threshold = template.DefaultSimilarityThreshold;
            runs = await RunAsync(provider, model, template, set, report, line, options.ModelsDirectory, cancellationToken).ConfigureAwait(false);
        }

        if (report.Prompt is null)
        {
            notes.Add("The report has no prompt or paraphrase, so there was nothing to re-run and no evaluation line to propose.");
        }
        else if (expectedTier is null)
        {
            notes.Add("The report names no tier the prompt needed, and none could be worked out from the decision, so no evaluation line is proposed.");
        }

        return new MisrouteTriageResult
        {
            Issue = options.Issue,
            ReportedVersion = version,
            Template = templateNumber,
            EmbeddingProvider = provider,
            EmbeddingModel = model,
            Threshold = threshold,
            ReportedTier = report.ReportedTier,
            ReportedReason = report.ReportedReason,
            ReportedRoute = report.ReportedRoute,
            ReportedSimilarity = report.ReportedSimilarity,
            Direction = report.Direction,
            HasPrompt = report.Prompt is not null,
            Rerun = runs.Rerun,
            Reproduction = Reproduction(report, runs.Rerun),
            ExpectedTier = expectedTier,
            ExpectedTierSource = source,
            ExpectedRoute = expectedRoute,
            EvaluationLine = line is null ? null : EvaluationSet.ToJsonLines([line]).TrimEnd('\n'),
            Without = runs.Without,
            With = runs.With,
            NewLineCorrect = runs.NewLineCorrect,
            Calibration = runs.Calibration,
            Missing = report.Missing,
            Notes = notes,
        };
    }

    private sealed record Runs(TriageAccuracy Without, TriageDecision? Rerun, TriageAccuracy? With, bool? NewLineCorrect, TriageCalibration? Calibration);

    private static async Task<Runs> RunAsync(
        string provider, string? model, RouterConfiguration template, IReadOnlyList<EvaluationCase> set, MisrouteReport report, EvaluationCase? line,
        string modelsDirectory, CancellationToken cancellationToken)
    {
        ServiceProvider? services = null;
        try
        {
            IEmbeddingProvider embeddings;
            if (provider == OnnxEmbeddings.Provider)
            {
                services = OnnxServices(modelsDirectory);
                IEmbeddingProviderFactory factory = services.GetServices<IEmbeddingProviderFactory>().First(f => f.Kind == OnnxEmbeddings.Provider);
                if (factory.Unavailable is { } unavailable)
                {
                    throw new InvalidOperationException(unavailable);
                }

                embeddings = factory.Create(new EmbeddingProviderOptions { Provider = OnnxEmbeddings.Provider, Model = model });
            }
            else
            {
                embeddings = new HashingEmbeddingProvider(template.Embeddings.Dimensions);
            }

            var evaluator = new RoutingEvaluator(embeddings, template);
            TriageAccuracy without = Accuracy(evaluator.Evaluate(await evaluator.PrepareAsync(set, cancellationToken).ConfigureAwait(false)));
            if (report.Prompt is null)
            {
                return new Runs(without, null, null, null, null);
            }

            // The proposed line, or the bare prompt to re-run when there is no line to propose.
            EvaluationCase probe = line ?? new EvaluationCase { Prompt = report.Prompt, ExpectedTier = report.ReportedTier ?? 0 };
            PreparedEvaluation prepared = await evaluator.PrepareAsync([.. set, probe], cancellationToken).ConfigureAwait(false);
            EvaluationReport with = evaluator.Evaluate(prepared);
            EvaluationResult rerun = with.Results[^1];
            var decision = new TriageDecision
            {
                Tier = rerun.ActualTier,
                Reason = rerun.Reason,
                Route = rerun.ActualRoute,
                Similarity = rerun.Similarity,
                Complexity = rerun.Complexity,
                Closest = [.. rerun.TopScores.Take(3).Select(s => new TriageRouteScore(s.Route.Name, s.Route.Tier, s.Similarity))],
            };
            if (line is null)
            {
                return new Runs(without, decision, null, null, null);
            }

            CalibrationReport calibration = evaluator.Calibrate(prepared);
            EvaluationReport calibrated = evaluator.Evaluate(prepared, calibration.Recommended.Threshold);
            return new Runs(without, decision, Accuracy(with), rerun.Correct, new TriageCalibration
            {
                Threshold = calibration.Recommended.Threshold,
                Result = Accuracy(calibrated),
                NewLineTier = calibrated.Results[^1].ActualTier,
            });
        }
        finally
        {
            if (services is not null)
            {
                await services.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static ServiceProvider OnnxServices(string modelsDirectory)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSemanticPromptRouting(_ => { }).AddOnnxEmbeddings(o => o.ModelsDirectory = modelsDirectory);
        return services.BuildServiceProvider();
    }

    private static TriageAccuracy Accuracy(EvaluationReport report) => new() { Total = report.Total, Correct = report.Correct };

    /// <summary>The template of <paramref name="release"/>, or the current one (saying why) when it names none this build knows.</summary>
    internal static int TemplateFor(Version? release, List<string> notes)
    {
        if (release is null)
        {
            notes.Add($"The report names no release version (x.y.z), so the current template ({TemplateMigration.Current.ToString(CultureInfo.InvariantCulture)}) was used.");
            return TemplateMigration.Current;
        }

        if (release < Releases[0].From)
        {
            notes.Add($"No release {release} is known, so the current template ({TemplateMigration.Current.ToString(CultureInfo.InvariantCulture)}) was used.");
            return TemplateMigration.Current;
        }

        if (System.Version.TryParse(RouterHost.Version.Split('-')[0], out Version? self) && release > self)
        {
            notes.Add($"The report is from {release}, newer than this build ({self}), so this build's newest template ({TemplateMigration.Current.ToString(CultureInfo.InvariantCulture)}) was used; it may differ from {release}'s.");
        }

        return Releases.Last(r => r.From <= release).Template;
    }

    /// <summary>
    /// The embeddings to re-run with: <paramref name="choice"/> when given, else the reported ones when they run here
    /// (Hashing, or a known ONNX model), else the default ONNX model, saying so.
    /// </summary>
    internal static (string Provider, string? Model) ChooseEmbeddings(MisrouteReport report, string? choice, List<string> notes)
    {
        if (choice is not null)
        {
            string[] parts = choice.Split(':', 2, StringSplitOptions.TrimEntries);
            return string.Equals(parts[0], EmbeddingProviderKinds.Hashing, StringComparison.OrdinalIgnoreCase)
                ? (EmbeddingProviderKinds.Hashing, null)
                : (OnnxEmbeddings.Provider, OnnxEmbeddingModels.Find(parts.Length == 2 ? parts[1] : OnnxEmbeddingModels.DefaultModel)?.Name ?? OnnxEmbeddingModels.DefaultModel);
        }

        string? reported = report.EmbeddingProvider;
        if (string.Equals(reported, EmbeddingProviderKinds.Hashing, StringComparison.OrdinalIgnoreCase))
        {
            return (EmbeddingProviderKinds.Hashing, null);
        }

        if (string.Equals(reported, OnnxEmbeddings.Provider, StringComparison.OrdinalIgnoreCase))
        {
            if (report.EmbeddingModel is null)
            {
                return (OnnxEmbeddings.Provider, OnnxEmbeddingModels.DefaultModel);
            }

            if (OnnxEmbeddingModels.Find(report.EmbeddingModel) is { } known)
            {
                return (OnnxEmbeddings.Provider, known.Name);
            }
        }

        notes.Add(reported is null
            ? $"The report names no embedding provider, so the default ONNX model ({OnnxEmbeddingModels.DefaultModel}) was used."
            : $"The reported embeddings ({UntrustedText.Code(report.Embeddings)}) cannot run here (an embedding server, or a model this build does not know), so the default ONNX model ({OnnxEmbeddingModels.DefaultModel}) was used; similarities differ from the reporter's.");
        return (OnnxEmbeddings.Provider, OnnxEmbeddingModels.DefaultModel);
    }

    /// <summary>
    /// Sets the template's thresholds: the reported one when the embeddings are the reported ones (on the route it names,
    /// if it names one), else the starting threshold for these embeddings.
    /// </summary>
    private static float ApplyThreshold(MisrouteReport report, RouterConfiguration template, string provider, string? model, List<string> notes)
    {
        float starting = provider == OnnxEmbeddings.Provider ? OnnxEmbeddingModels.Find(model!)!.SuggestedThreshold : template.DefaultSimilarityThreshold;
        template.DefaultSimilarityThreshold = starting;
        bool same = string.Equals(report.EmbeddingProvider, provider, StringComparison.OrdinalIgnoreCase)
            && (provider == EmbeddingProviderKinds.Hashing || string.Equals(report.EmbeddingModel ?? OnnxEmbeddingModels.DefaultModel, model, StringComparison.OrdinalIgnoreCase));
        if (!same || report.Threshold is not { } reported)
        {
            return starting;
        }

        if (report.ThresholdRoute is null)
        {
            template.DefaultSimilarityThreshold = reported;
            return reported;
        }

        if (template.Routes.FirstOrDefault(r => string.Equals(r.Name, report.ThresholdRoute, StringComparison.OrdinalIgnoreCase)) is { } route)
        {
            route.SimilarityThreshold = reported;
            notes.Add($"The reported threshold is route {UntrustedText.Code(route.Name)}'s own; it was set on that route, and the default stayed at {Format(starting)}.");
        }

        return starting;
    }

    /// <summary>Notes for decisions a single prompt on the template cannot show.</summary>
    private static void NoteWhatTheTemplateCannotShow(MisrouteReport report, RouterConfiguration template, List<string> notes)
    {
        if (report.ReportedReason is RoutingReason.ConversationAffinity or RoutingReason.ModelOverride or RoutingReason.RetryEscalation
            or RoutingReason.ClassifierFailure or RoutingReason.EmptyPrompt)
        {
            notes.Add($"The reported reason, `{report.ReportedReason}`, comes from the request or the conversation, not from classifying the prompt, so the re-run (the prompt alone) can differ.");
        }

        if (report.ReportedRoute is { } route && !template.Routes.Any(r => string.Equals(r.Name, route, StringComparison.OrdinalIgnoreCase)))
        {
            notes.Add($"The reported route {UntrustedText.Code(route)} is not one of the template's: the reporter's configuration has its own routes, which the re-run cannot see.");
        }
    }

    /// <summary>The tier the prompt needed: as stated (<c>Tier 2</c>, <c>T2</c>), by a tier's name, or one step from the reported tier.</summary>
    internal static (int? Tier, ExpectedTierSource Source) ExpectedTier(MisrouteReport report, SemanticRouterOptions template)
    {
        IList<TierDefinition> tiers = template.Tiers ?? [.. SemanticRouterOptions.DefaultTiers];
        int lowest = tiers.Min(t => t.Level), highest = tiers.Max(t => t.Level);
        if (report.Expected is { } expected)
        {
            foreach (Match match in StatedTier().Matches(expected))
            {
                if (int.TryParse(match.Groups["tier"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int stated) && stated >= lowest && stated <= highest)
                {
                    return (stated, ExpectedTierSource.Stated);
                }
            }

            if (tiers.Where(t => t.Name.Length > 0).OrderByDescending(t => t.Name.Length)
                    .FirstOrDefault(t => expected.Contains(t.Name, StringComparison.OrdinalIgnoreCase)) is { } named)
            {
                return (named.Level, ExpectedTierSource.TierName);
            }
        }

        return (report.Direction, report.ReportedTier) switch
        {
            (MisrouteDirection.TooHigh, int t) when t > lowest => (Math.Min(t, highest + 1) - 1, ExpectedTierSource.Direction),
            (MisrouteDirection.TooLow, int t) when t < highest => (Math.Max(t, lowest - 1) + 1, ExpectedTierSource.Direction),
            (MisrouteDirection.WrongRoute, int t) when t >= lowest && t <= highest => (t, ExpectedTierSource.Direction),
            _ => (null, ExpectedTierSource.None),
        };
    }

    /// <summary>A template route the expected text names, when its tier is the expected one.</summary>
    private static string? ExpectedRoute(MisrouteReport report, SemanticRouterOptions template, int tier, List<string> notes)
    {
        if (report.Expected is not { } expected)
        {
            return null;
        }

        RouteDefinition? named = template.Routes
            .OrderByDescending(r => r.Name.Length)
            .FirstOrDefault(r => r.Name.Length > 0 && Regex.IsMatch(expected, $@"(?<![\w-]){Regex.Escape(r.Name)}(?![\w-])", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)));
        if (named is null)
        {
            return null;
        }

        if (named.Tier != tier)
        {
            notes.Add($"The report names route `{named.Name}`, which is tier {named.Tier.ToString(CultureInfo.InvariantCulture)} in the template, not {tier.ToString(CultureInfo.InvariantCulture)}; the line leaves the route out.");
            return null;
        }

        return named.Name;
    }

    private static MisrouteReproduction Reproduction(MisrouteReport report, TriageDecision? rerun) =>
        rerun is null || report.ReportedTier is not { } tier ? MisrouteReproduction.NotChecked
        : rerun.Tier != tier ? MisrouteReproduction.Different
        : string.Equals(rerun.Route, report.ReportedRoute, StringComparison.OrdinalIgnoreCase) ? MisrouteReproduction.Reproduced
        : MisrouteReproduction.SameTier;

    private static string Format(float value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Format(float? value) => value is { } v ? v.ToString("0.000", CultureInfo.InvariantCulture) : "-";

    private static string Percent(TriageAccuracy accuracy) => accuracy.Accuracy.ToString("0.0%", CultureInfo.InvariantCulture);

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Describe(int? tier, RoutingReason? reason, string? route, float? similarity) =>
        $"tier {(tier is { } t ? Count(t) : "?")}, {(reason is { } r ? $"`{r}`" : "reason not given")}, route {UntrustedText.Code(route)}, similarity {Format(similarity)}";

    /// <summary>The comment for the issue. Text from the report appears only inside code spans and code blocks.</summary>
    public static string ToMarkdown(MisrouteTriageResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var md = new StringBuilder();
        string embeddings = result.EmbeddingModel is null ? result.EmbeddingProvider : $"{result.EmbeddingProvider} `{result.EmbeddingModel}`";
        md.AppendLine(Marker);
        md.AppendLine("### Misroute triage");
        md.AppendLine();
        md.AppendLine("An automatic re-run of this report on the router's built-in template. It calls no model and works only from what the form says; the template's routes are not your configuration, so a difference can come from either. A maintainer reviews it and removes `needs-triage` once the report is accepted.");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"**Re-run** on template {Count(result.Template)} (release {UntrustedText.Code(result.ReportedVersion)}), {embeddings} at threshold {Format(result.Threshold)}:");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"- Reported: {Describe(result.ReportedTier, result.ReportedReason, result.ReportedRoute, result.ReportedSimilarity)}");
        if (result.Rerun is { } rerun)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"- Re-run: {Describe(rerun.Tier, rerun.Reason, rerun.Route, rerun.Similarity)}, complexity {rerun.Complexity.ToString("0.000", CultureInfo.InvariantCulture)}");
            if (rerun.Closest.Count > 0)
            {
                md.AppendLine(CultureInfo.InvariantCulture, $"- Closest routes: {string.Join(", ", rerun.Closest.Select(s => $"`{s.Route}` (tier {Count(s.Tier)}) {Format((float?)s.Similarity)}"))}");
            }
        }

        md.AppendLine(CultureInfo.InvariantCulture, $"- **{result.Reproduction switch
        {
            MisrouteReproduction.Reproduced => "Reproduced:** the template makes the same decision for the paraphrase.",
            MisrouteReproduction.SameTier => "Same tier, another route:** the template gives the reported tier through another route.",
            MisrouteReproduction.Different => $"Not reproduced:** the template gives tier {Count(result.Rerun!.Tier)}, not {Count(result.ReportedTier ?? 0)}. The reporter's routes, thresholds or embeddings, or the paraphrase, differ from what was routed.",
            _ => result.HasPrompt ? "Not compared:** the decision names no tier." : "Not re-run:** the report has no prompt or paraphrase.",
        }}");
        md.AppendLine();

        if (result.EvaluationLine is { } line)
        {
            string from = result.ExpectedTierSource switch
            {
                ExpectedTierSource.Stated => "the tier the report says it needed",
                ExpectedTierSource.TierName => "the tier the report names",
                _ => result.Direction == MisrouteDirection.WrongRoute
                    ? "the reported tier, since the report says the tier was right"
                    : $"one tier {(result.Direction == MisrouteDirection.TooHigh ? "below" : "above")} the reported one, since the report names no tier: check it",
            };
            md.AppendLine(CultureInfo.InvariantCulture, $"**Proposed evaluation line** for `semantic-router.evaluation.jsonl`, tier {Count(result.ExpectedTier!.Value)} ({from}):");
            md.AppendLine();
            md.AppendLine(UntrustedText.Block(line, "jsonl"));
            md.AppendLine();
        }

        md.AppendLine(CultureInfo.InvariantCulture, $"**Template {Count(result.Template)}'s evaluation set** with and without it:");
        md.AppendLine();
        md.AppendLine("| Set | Prompts | Routed to their tier | Accuracy |");
        md.AppendLine("|---|---:|---:|---:|");
        md.AppendLine(CultureInfo.InvariantCulture, $"| Without the line | {Count(result.Without.Total)} | {Count(result.Without.Correct)} | {Percent(result.Without)} |");
        if (result.With is { } with)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"| With the line | {Count(with.Total)} | {Count(with.Correct)} | {Percent(with)} |");
            if (result.Calibration is { } calibration)
            {
                md.AppendLine(CultureInfo.InvariantCulture, $"| With the line, best threshold ({Format(calibration.Threshold)}) | {Count(calibration.Result.Total)} | {Count(calibration.Result.Correct)} | {Percent(calibration.Result)} |");
            }

            md.AppendLine();
            md.AppendLine(result.NewLineCorrect == true
                ? "The template already routes the new line to its tier: the line guards against a regression, and the misroute is likely in the reporter's own routes or thresholds."
                : $"The template routes the new line to tier {Count(result.Rerun!.Tier)}, not {Count(result.ExpectedTier!.Value)}: with it, the set records this misroute.");
            if (result.Calibration is { } c && result.NewLineCorrect != true)
            {
                md.AppendLine(c.NewLineTier == result.ExpectedTier
                    ? $"At threshold {Format(c.Threshold)} the line routes to its tier, with {Count(c.Result.Correct)} of {Count(c.Result.Total)} right: a threshold change could fix it."
                    : $"No threshold fixes it on the whole set (at the best one, {Format(c.Threshold)}, it gets tier {Count(c.NewLineTier)}): it needs an utterance or a route change.");
            }
        }
        else
        {
            md.AppendLine();
        }

        if (result.Missing.Count > 0 || result.Notes.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("**Notes**");
            md.AppendLine();
            if (result.Missing.Count > 0)
            {
                md.AppendLine(CultureInfo.InvariantCulture, $"- The form leaves out: {string.Join(", ", result.Missing.Select(m => $"*{m}*"))}.");
            }

            foreach (string note in result.Notes)
            {
                md.AppendLine("- " + note);
            }
        }

        md.AppendLine();
        md.AppendLine("<sub>`semantic-router triage-misroute`: deterministic, no model and no secrets; see docs/routing-quality.md.</sub>");
        return md.ToString().ReplaceLineEndings("\n");
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.Default,
    };

    /// <summary>The result as JSON, for scripts.</summary>
    public static string ToJson(MisrouteTriageResult result) => JsonSerializer.Serialize(result, Json) + "\n";

    [GeneratedRegex(@"(?:\btier\s*#?\s*|\bT)(?<tier>\d{1,2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex StatedTier();
}
