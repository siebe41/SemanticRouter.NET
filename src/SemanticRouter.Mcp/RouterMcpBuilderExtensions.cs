using System.Reflection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Protocol;
using SemanticRouter;
using SemanticRouter.AspNetCore;
using SemanticRouter.Mcp;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the router's MCP server.</summary>
public static class RouterMcpBuilderExtensions
{
    /// <summary>
    /// Registers the router's MCP server: tools that let an agent classify a prompt, explain a routed request, read usage
    /// and connector health, and (with <see cref="RouterMcpOptions.AllowProposals"/>) propose tuning for a person to apply.
    /// Binds <see cref="RouterMcpOptions"/> from <c>SemanticRouter:Mcp</c>, then runs <paramref name="configure"/>. Map the
    /// endpoint with <c>MapRouterMcp</c>; it serves nothing until <see cref="RouterMcpOptions.Enabled"/> is set.
    /// </summary>
    /// <remarks>
    /// The server is the application's MCP server (<c>AddMcpServer</c>, Streamable HTTP, stateless): an application that
    /// serves MCP tools of its own adds them to the same server, and its own <c>MapMcp</c> endpoints offer none of the
    /// router's tools.
    /// </remarks>
    public static SemanticRouterBuilder AddRouterMcpServer(this SemanticRouterBuilder builder, Action<RouterMcpOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        IServiceCollection services = builder.Services;
        if (configure is not null)
        {
            services.Configure(configure);
        }

        if (services.Any(s => s.ServiceType == typeof(RouterMcpServerMarker)))
        {
            return builder;
        }

        services.AddSingleton<RouterMcpServerMarker>();
        services.AddOptions<RouterMcpOptions>().BindConfiguration(SemanticRouterOptions.SectionName + ":Mcp");
        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "semantic-router", Title = "Semantic Router", Version = Version };
                options.ServerInstructions = RouterMcpTools.Instructions;
            })
            .WithHttpTransport(transport =>
            {
                // Each request stands alone: nothing is kept between calls, and no session can outlive the caller's sign-in.
                transport.Stateless = true;
                transport.ConfigureSessionOptions = RouterMcpTools.ForCallerAsync;
            })
            .WithTools<RouterMcpTools>(RouterMcpTools.Json);
        return builder;
    }

    private static string Version =>
        (typeof(RouterMcpBuilderExtensions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev").Split('+')[0];
}
