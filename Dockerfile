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
# runtime image needs the SDK, not just the ASP.NET runtime. It also needs
# the Docker CLI so it can talk to a Docker daemon mounted in via
# /var/run/docker.sock (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS runtime

RUN apt-get update \
    && apt-get install -y --no-install-recommends docker.io \
    && rm -rf /var/lib/apt/lists/*

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
