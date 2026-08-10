# syntax=docker/dockerfile:1

# ---- build: SDK + Node (SPA build) + publish ------------------------------
# Pinned to the BUILD platform on purpose: the publish output is portable IL, and the
# arm64 `protoc` bundled with Grpc.Tools segfaults (exit 139) on the dashboard proto — both
# under QEMU and on native arm64. Building on the host arch keeps multi-arch images working.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build

# Node is only needed here to build the web/ SPA (csproj's BuildSpa target
# runs `npm install && npm run build` for Release configuration).
RUN curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src
COPY . .
# -p:IsPublishable=true: the AspireUI.Server project references Aspire.Hosting.AppHost,
# whose build props default IsPublishable to false for AppHost-style projects. That's
# wrong here (AspireUI.Server is the actual web server we deploy), so force it back on.
RUN dotnet publish src/AspireUI.Server -c Release -o /app -p:IsPublishable=true

# ---- runtime: keep the FULL SDK (not just aspnet) -------------------------
# "Run a stack" shells `dotnet run` on generated AppHost projects, so the
# runtime image needs the SDK, not just the ASP.NET runtime. It also needs the
# Docker CLI + Compose v2 plugin (Hosting runs `docker compose …`) and the
# `aspire` CLI (Hosting publishes compose via `aspire publish`) so those
# features work INSIDE the image — not just on a dev box that has them.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS runtime

ARG COMPOSE_VERSION=v2.32.4
ARG TARGETARCH
RUN apt-get update \
    && apt-get install -y --no-install-recommends docker.io unzip \
    && rm -rf /var/lib/apt/lists/* \
    # docker.io ships the CLI but NOT the compose v2 plugin — drop the plugin binary in.
    && mkdir -p /usr/local/lib/docker/cli-plugins \
    && case "${TARGETARCH:-amd64}" in \
         amd64) COMPOSE_ARCH=x86_64 ;; \
         arm64) COMPOSE_ARCH=aarch64 ;; \
         *) echo "unsupported TARGETARCH: ${TARGETARCH}" >&2; exit 1 ;; \
       esac \
    && curl -fSL "https://github.com/docker/compose/releases/download/${COMPOSE_VERSION}/docker-compose-linux-${COMPOSE_ARCH}" \
         -o /usr/local/lib/docker/cli-plugins/docker-compose \
    && chmod +x /usr/local/lib/docker/cli-plugins/docker-compose

# The Aspire CLI (keep in sync with the Aspire.Hosting.* version in Directory.Packages.props) —
# Hosting's compose publish shells it. Taken from the RID-specific NuGet package (a single
# self-contained binary) instead of `dotnet tool install`, because the SDK's tool installer runs
# emulated on the arm64 leg and aborts there (QEMU + .NET TLS), while curl/unzip do not.
ARG ASPIRE_CLI_VERSION=13.4.6
RUN case "${TARGETARCH:-amd64}" in \
      amd64) CLI_RID=linux-x64 ;; \
      arm64) CLI_RID=linux-arm64 ;; \
      *) echo "unsupported TARGETARCH: ${TARGETARCH}" >&2; exit 1 ;; \
    esac \
    && curl -fSL "https://api.nuget.org/v3-flatcontainer/aspire.cli.${CLI_RID}/${ASPIRE_CLI_VERSION}/aspire.cli.${CLI_RID}.${ASPIRE_CLI_VERSION}.nupkg" \
         -o /tmp/aspire-cli.nupkg \
    && unzip -j -o /tmp/aspire-cli.nupkg "tools/*/${CLI_RID}/aspire" -d /usr/local/bin \
    && chmod +x /usr/local/bin/aspire \
    && rm /tmp/aspire-cli.nupkg \
    && aspire --version

WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DB_PATH=/data/aspireui.db \
    WORKSPACE_DIR=/data/workspace

# Published apps read ASPNETCORE_URLS from the environment; launchSettings.json
# (which has the dev port 5158) only applies to `dotnet run`.
EXPOSE 8080
VOLUME ["/data"]

ENTRYPOINT ["dotnet", "AspireUI.Server.dll"]
