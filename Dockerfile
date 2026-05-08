# syntax=docker/dockerfile:1.7

# ─── build ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files first so `dotnet restore` is cached separately
# from source — small source edits don't bust the package cache.
COPY Kremeing.slnx ./
COPY src/Kremeing.Contracts/Kremeing.Contracts.fsproj  src/Kremeing.Contracts/
COPY src/Kremeing.Core/Kremeing.Core.fsproj            src/Kremeing.Core/
COPY src/Kremeing.Api/Kremeing.Api.fsproj              src/Kremeing.Api/
RUN dotnet restore src/Kremeing.Api/Kremeing.Api.fsproj

# Then bring source + the web client (linked into wwwroot/ via fsproj
# Content elements) and publish a self-contained-style output.
COPY src/ src/
COPY web/ web/
RUN dotnet publish src/Kremeing.Api/Kremeing.Api.fsproj \
        -c Release \
        -o /app/publish \
        --no-restore \
        /p:PublishTrimmed=false

# ─── runtime ───────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Run as non-root. The aspnet image ships a UID 1654 named `app`.
USER app

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_NOLOGO=true \
    DOTNET_USE_POLLING_FILE_WATCHER=false

EXPOSE 8080

COPY --from=build /app/publish ./

ENTRYPOINT ["dotnet", "Kremeing.Api.dll"]
