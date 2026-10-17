# The router as a container: API-key connectors and local model servers only. Subscription CLIs (claude, codex,
# copilot) are not in the image and cannot use your machine's login from inside a container; run the dotnet tool
# on the machine itself for those.
#
#   docker build -f src/SemanticRouter.Host/Dockerfile -t semantic-router .
#   docker run -p 127.0.0.1:5080:8080 -v semantic-router:/data -e ROUTER_ADMIN_TOKEN=... -e ROUTER_GATEWAY_KEY=... semantic-router
#
# Clients send ROUTER_GATEWAY_KEY as their API key (ANTHROPIC_AUTH_TOKEN, OPENAI_API_KEY). Publish the port beyond
# 127.0.0.1 only on a network you trust, and see docs/host.md for HTTPS.
#
# Base images are pinned by digest (Dependabot updates them); the runtime is Ubuntu chiseled: no shell or package
# manager, only what .NET needs. The -extra variant keeps ICU and time zone data, for budgets in a local time zone.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
WORKDIR /src
COPY global.json nuget.config Directory.Build.props Directory.Build.targets Directory.Packages.props .editorconfig ./
COPY src/ src/
COPY deploy/third-party-notices.cs deploy/
# Locked restore: exactly the packages in the projects' packages.lock.json files.
RUN dotnet publish src/SemanticRouter.Host/SemanticRouter.Host.csproj -c Release -o /app /p:UseAppHost=false /p:RestoreLockedMode=true
# The licences and notices of the NuGet packages the image ships, from the packages restore just fetched (no network).
RUN dotnet run --file deploy/third-party-notices.cs -- /app/THIRD-PARTY-NOTICES.txt /app
# The runtime image has no shell to create the data directory, so it is made here and copied over.
RUN mkdir -p /data

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:3ed6558056b54d5c9dfefb7ff473728e022ff2bf1d976cbf8ee07b05ec594374
WORKDIR /app
COPY --from=build /app .
COPY --from=build --chown=$APP_UID:$APP_UID /data /data

# Configuration, the encrypted secrets file and the data-protection keys that decrypt it live on one volume.
# Port-mapped requests arrive from the container network, so the router treats them as remote: the dashboard needs a
# token (ROUTER_ADMIN_TOKEN) and the gateway a key (ROUTER_GATEWAY_KEY), both resolved at start-up through ${env:...}
# references. Without them, everyone is refused. While the configuration is the untouched template, an OPENAI_API_KEY or
# ANTHROPIC_API_KEY in the environment gets a connector that refers to it (SEMANTIC_ROUTER_SEED_API_KEYS).
# ORT_DISABLE_TELEMETRY: ONNX Runtime (the in-process embeddings) sends no usage data to Microsoft (docs/network.md).
ENV SEMANTIC_ROUTER_HOME=/data \
    ORT_DISABLE_TELEMETRY=1 \
    SEMANTIC_ROUTER_KEYS_DIR=/data/keys \
    SEMANTIC_ROUTER_SEED_API_KEYS=true \
    ASPNETCORE_URLS=http://0.0.0.0:8080 \
    SemanticRouter__Dashboard__RequireLocalClient=false \
    SemanticRouter__Dashboard__AdminToken=\${env:ROUTER_ADMIN_TOKEN} \
    SemanticRouter__Middleware__GatewayKeys__default=\${env:ROUTER_GATEWAY_KEY}
VOLUME /data
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "semantic-router.dll"]
