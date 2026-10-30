using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>Built-in themes meet WCAG AA, and theme files dropped next to the configuration show up without a rebuild.</summary>
public sealed class DashboardThemeTests : RouterHostTest
{
    private const string Ocean = """
        /* comments are ignored, including --fake: #000; inside them */
        :root[data-theme="ocean"] {
          color-scheme: dark;
          --bg: #0b1726; --bg-elevated: #10213a; --bg-sunken: #081120;
          --text: #e6f0fa; --text-muted: #9fb6cc; --text-faint: #4a5a6a;
        }
        """;

    [Theory]
    [InlineData("machine-dark")]
    [InlineData("machine-light")]
    [InlineData("terminal")]
    public void Built_in_themes_meet_WCAG_AA_for_text(string id)
    {
        DashboardTheme theme = new ThemeCatalog(null).List().Single(t => t.Id == id);

        theme.ContrastIssues.Should().BeEmpty();
        theme.Problem.Should().BeNull();
    }

    [Fact]
    public void Contrast_is_computed_as_WCAG_defines_it()
    {
        double Ratio(string a, string b) => Math.Round(ThemeCatalog.Contrast(ThemeCatalog.ParseColor(a)!.Value, ThemeCatalog.ParseColor(b)!.Value), 2);

        Ratio("#000000", "#ffffff").Should().Be(21);
        Ratio("#777777", "#ffffff").Should().Be(4.48);
        Ratio("#fff", "rgb(255, 255, 255)").Should().Be(1);
        ThemeCatalog.ParseColor("var(--accent)").Should().BeNull();
    }

    [Fact]
    public void Required_tokens_are_the_default_themes_colours()
    {
        ThemeCatalog.RequiredTokens.Should().Contain(["--bg", "--text", "--accent", "--accent-ink", "--signal-ink", "--series-1", "color-scheme"])
            .And.NotContain(t => t.StartsWith("--font-", StringComparison.Ordinal) || t.StartsWith("--radius", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Theme_files_next_to_the_configuration_file_are_listed_checked_and_served()
    {
        string directory = Directory.CreateTempSubdirectory("router-themes-").FullName;
        try
        {
            string themes = Path.Combine(directory, "themes");
            Directory.CreateDirectory(themes);
            await File.WriteAllTextAsync(Path.Combine(themes, "ocean.css"), Ocean);
            await File.WriteAllTextAsync(Path.Combine(themes, "ocean.json"), """{ "name": "Deep Ocean" }""");
            await File.WriteAllTextAsync(Path.Combine(themes, "Bad Name.css"), Ocean);
            await File.WriteAllTextAsync(Path.Combine(themes, "machine-dark.css"), Ocean);
            HttpClient client = await StartAsync(services: s => s.AddSemanticPromptRouting(_ => { }).UseConfigurationFile(Path.Combine(directory, "router.json")));

            JsonElement listing = JsonDocument.Parse(await client.GetStringAsync("/router/api/themes")).RootElement;
            JsonElement[] all = listing.GetProperty("themes").EnumerateArray().ToArray();
            JsonElement ocean = all.Single(t => t.GetProperty("id").GetString() == "ocean");

            listing.GetProperty("directory").GetString().Should().Be(themes);
            all.Select(t => t.GetProperty("id").GetString()).Should().Equal("machine-dark", "machine-light", "terminal", "ocean");
            ocean.GetProperty("name").GetString().Should().Be("Deep Ocean");
            ocean.GetProperty("builtIn").GetBoolean().Should().BeFalse();
            ocean.GetProperty("missingTokens").EnumerateArray().Select(t => t.GetString()).Should().Contain("--accent").And.NotContain("--bg").And.NotContain("--fake");
            ocean.GetProperty("contrastIssues").EnumerateArray().Should().Contain(i =>
                i.GetProperty("foreground").GetString() == "--text-faint" && i.GetProperty("background").GetString() == "--bg");

            HttpResponseMessage css = await client.GetAsync("/router/themes/ocean.css");
            css.Content.Headers.ContentType!.MediaType.Should().Be("text/css");
            (await css.Content.ReadAsStringAsync()).Should().Contain("data-theme=\"ocean\"");
            foreach (string path in (string[])["/router/themes/missing.css", "/router/themes/..%2Frouter.json", "/router/themes/ocean.json", "/router/themes/machine-dark.css"])
            {
                (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.NotFound, path);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_theme_file_appears_without_a_restart_and_the_directory_can_be_set()
    {
        string themes = Directory.CreateTempSubdirectory("router-themes-").FullName;
        try
        {
            HttpClient client = await StartAsync(services: s => s.Configure<RouterDashboardOptions>(o => o.ThemesDirectory = themes));
            string Ids() => string.Join(",", JsonDocument.Parse(client.GetStringAsync("/router/api/themes").Result).RootElement
                .GetProperty("themes").EnumerateArray().Select(t => t.GetProperty("id").GetString()));

            Ids().Should().Be("machine-dark,machine-light,terminal");
            await File.WriteAllTextAsync(Path.Combine(themes, "half-done.css"), ":root[data-theme=\"half-done\"] { --bg: #222; }");
            Ids().Should().Be("machine-dark,machine-light,terminal,half-done");
        }
        finally
        {
            Directory.Delete(themes, recursive: true);
        }
    }

    [Fact]
    public void A_file_that_declares_another_theme_is_reported()
    {
        DashboardTheme theme = ThemeCatalog.Check("sunset", "Sunset", ":root[data-theme=\"sunrise\"] { --bg: #fff; }", builtIn: false);

        theme.Problem.Should().Contain(":root[data-theme=\"sunset\"]");
        theme.MissingTokens.Should().Contain("--bg");
    }

    [Fact]
    public void The_last_declaration_of_a_theme_may_leave_out_its_semicolon()
    {
        // #315: valid CSS may end a block without one, and that token used to be dropped.
        ThemeCatalog.Tokens(""":root[data-theme="x"] { --bg: #fff; --text: #000 }""", "x")
            .Should().BeEquivalentTo(new Dictionary<string, string> { ["--bg"] = "#fff", ["--text"] = "#000" });
    }
}
