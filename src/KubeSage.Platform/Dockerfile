# Container image for the KubeSage platform.
#
# Built from the repository root so that Directory.Build.props,
# Directory.Packages.props and global.json - which the build genuinely needs -
# are inside the build context.
#
# The restore step is deliberately separated from the source copy. Docker
# caches it, so editing a .cs file rebuilds in seconds instead of downloading
# every NuGet package again.

# Image tags are pinned to exact versions rather than the floating "10.0" tag,
# so the same commit produces the same binaries months from now.
FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/KubeSage.Platform/KubeSage.Platform.csproj src/KubeSage.Platform/
RUN dotnet restore src/KubeSage.Platform/KubeSage.Platform.csproj

COPY src/ src/
COPY knowledge/ knowledge/

RUN dotnet publish src/KubeSage.Platform/KubeSage.Platform.csproj \
    --no-restore \
    --configuration Release \
    --output /app/publish


FROM mcr.microsoft.com/dotnet/aspnet:10.0.9 AS runtime
WORKDIR /app

# curl is used by the container health check defined in docker-compose.yml.
# Package lists are removed in the same layer so they do not bloat the image.
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Run as the unprivileged user that the .NET base images provide. The platform
# is an observer; nothing it does needs root inside its own container.
USER $APP_UID

EXPOSE 8080
ENTRYPOINT ["dotnet", "KubeSage.Platform.dll"]
