# The router as a container: API-key connectors and local model servers only. Subscription CLIs (claude, codex,
# copilot) are not in the image and cannot use your machine's login from inside a container; run the dotnet tool
# on the machine itself for those.
#
#   docker build -f src/SemanticRouter.Host/Dockerfile -t semantic-router .
#   docker run -p 127.0.0.1:5080:8080 -v semantic-router:/data -e ROUTER_ADMIN_TOKEN=... -e ROUTER_GATEWAY_KEY=... semantic-router
#
# Clients send ROUTER_GATEWAY_KEY as their API key (ANTHROPIC_AUTH_TOKEN, OPENAI_API_KEY). Publish the port beyond
# 127.0.0.1 only on a network you trust, and see docs/host.md for HTTPS.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json nuget.config Directory.Build.props Directory.Build.targets Directory.Packages.props .editorconfig ./
COPY src/ src/
RUN dotnet publish src/SemanticRouter.Host/SemanticRouter.Host.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# Configuration, the encrypted secrets file and the data-protection keys that decrypt it live on one volume.
# Port-mapped requests arrive from the container network, so the router treats them as remote: the dashboard needs a
# token (ROUTER_ADMIN_TOKEN) and the gateway a key (ROUTER_GATEWAY_KEY), both resolved at start-up through ${env:...}
# references. Without them, everyone is refused.
ENV SEMANTIC_ROUTER_HOME=/data \
    SEMANTIC_ROUTER_KEYS_DIR=/data/keys \
    ASPNETCORE_URLS=http://0.0.0.0:8080 \
    SemanticRouter__Dashboard__RequireLocalClient=false \
    SemanticRouter__Dashboard__AdminToken=\${env:ROUTER_ADMIN_TOKEN} \
    SemanticRouter__Middleware__GatewayKeys__default=\${env:ROUTER_GATEWAY_KEY}
RUN mkdir -p /data && chown "$APP_UID" /data
VOLUME /data
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "semantic-router.dll"]
