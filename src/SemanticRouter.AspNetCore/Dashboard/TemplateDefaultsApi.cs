using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// The settings an upgrade to a newer built-in template kept as someone had them (<see cref="TemplateDefaults"/>):
/// <c>GET api/config/template-defaults</c> lists them with your value, the template's new one and what runs now;
/// <c>POST …/use</c> takes the new default for one, <c>POST …/keep</c> keeps yours. Both admin only and audited.
/// </summary>
internal static class TemplateDefaultsApi
{
    public static void Map(RouteGroupBuilder api, JsonSerializerOptions json, Func<HttpContext, string> actor)
    {
        api.MapGet("/config/template-defaults", (TemplateDefaults defaults, RouterRuntimeProvider provider) =>
        {
            RouterConfiguration running = provider.Current.Configuration;
            return Results.Json(defaults.Pending is { } pending
                ? new Response(pending.From, pending.To, pending.At, [.. pending.Settings.Select(s => Setting.Of(s, running))])
                : new Response(null, null, null, []), json);
        });

        api.MapPost("/config/template-defaults/use", (HttpContext context, TemplateDefaults defaults, RouterRuntimeProvider provider, Choice choice) =>
        {
            try
            {
                if (defaults.UseDefault(choice.Setting ?? string.Empty, actor(context)) is not { } used)
                {
                    return Results.Problem($"No setting '{choice.Setting}' is waiting for a decision.", statusCode: StatusCodes.Status404NotFound);
                }

                DashboardAudit.Detail(context, $"{used.Setting.Setting}{(used.Saved || provider.Store is null ? string.Empty : ", not saved")}");
                return Results.Json(new Used(used.Setting.Setting, used.Saved), json);
            }
            catch (RouterConfigurationConflictException)
            {
                return Results.Problem("The configuration kept changing while this was applied. Try again.", statusCode: StatusCodes.Status409Conflict);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        }).WithMetadata(DashboardWriteMetadata.Instance).Audited("took a template's new default");

        api.MapPost("/config/template-defaults/keep", (HttpContext context, TemplateDefaults defaults, Choice choice) =>
        {
            if (!defaults.Keep(choice.Setting ?? string.Empty))
            {
                return Results.Problem($"No setting '{choice.Setting}' is waiting for a decision.", statusCode: StatusCodes.Status404NotFound);
            }

            DashboardAudit.Detail(context, choice.Setting!);
            return Results.NoContent();
        }).WithMetadata(DashboardWriteMetadata.Instance).Audited("kept a setting instead of a template's new default");
    }

    private sealed record Choice(string? Setting);

    private sealed record Used(string Setting, bool Saved);

    private sealed record Response(int? From, int? To, DateTimeOffset? At, IReadOnlyList<Setting> Settings);

    /// <summary>One setting, its values as JSON text: yours, the template's new one, and what runs now.</summary>
    private sealed record Setting(string Name, string? Yours, string? Default, string? Current)
    {
        public static Setting Of(TemplateDefault setting, RouterConfiguration running) =>
            new(setting.Setting, Text(setting.Yours), Text(setting.Default), Text(TemplateDefaults.ValueAt(running, setting.Path)));

        private static string? Text(JsonNode? value) => value?.ToJsonString();
    }
}
