# ClubShell central server image (docs/server/DESIGN.md §11 S6).
#
# The build context is the REPOSITORY ROOT (the server builds from ../src/ClubShell.Contracts and config/), so:
#   docker build -f server/Dockerfile -t clubshell-server .
# Railway: Root Directory = repository root, dockerfilePath = server/Dockerfile (server/railway.toml).
#
# No VOLUME instruction on purpose: Railway rejects it. /app/data (JWT signing key, PIN pepper, seed JSON) is a volume
# mounted by the platform (compose.yaml: named volume `data`; Railway: a volume on /app/data).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Repository-wide build inputs first: they change rarely, so restore stays cached.
COPY global.json nuget.config .editorconfig Directory.Build.props Directory.Packages.props ./
COPY server/Directory.Build.props server/
COPY src/ClubShell.Contracts/ src/ClubShell.Contracts/
COPY server/src/ClubShell.Server/ClubShell.Server.csproj server/src/ClubShell.Server/
RUN dotnet restore server/src/ClubShell.Server/ClubShell.Server.csproj

COPY config/policies.example.json config/games.example.json config/products.example.json config/
COPY server/contracts/ server/contracts/
COPY server/src/ClubShell.Server/ server/src/ClubShell.Server/
RUN dotnet publish server/src/ClubShell.Server/ClubShell.Server.csproj -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# The base image's non-root `app` user owns the data directory; a named volume mounted here inherits that ownership.
# (A Railway volume is mounted as root: set RAILWAY_RUN_UID=0 on the service, see server/README.md.)
RUN mkdir -p /app/data && chown app:app /app/data
USER app

# 8080 is the aspnet image default (ASPNETCORE_HTTP_PORTS); when PORT is set (Railway) the server listens there instead.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
ENTRYPOINT ["dotnet", "ClubShell.Server.dll"]
