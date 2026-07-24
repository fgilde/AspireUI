# syntax=docker/dockerfile:1

# ---- build: SDK + Node (SPA build) + publish ------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

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
RUN apt-get update \
    && apt-get install -y --no-install-recommends docker.io \
    && rm -rf /var/lib/apt/lists/* \
    # docker.io ships the CLI but NOT the compose v2 plugin — drop the plugin binary in.
    && mkdir -p /usr/local/lib/docker/cli-plugins \
    && curl -fSL "https://github.com/docker/compose/releases/download/${COMPOSE_VERSION}/docker-compose-linux-x86_64" \
         -o /usr/local/lib/docker/cli-plugins/docker-compose \
    && chmod +x /usr/local/lib/docker/cli-plugins/docker-compose

# The Aspire CLI (matches the Aspire.Hosting.* package version) — Hosting's compose publish shells it.
RUN dotnet tool install --global Aspire.Cli --version 13.4.6
ENV PATH="$PATH:/root/.dotnet/tools"

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
