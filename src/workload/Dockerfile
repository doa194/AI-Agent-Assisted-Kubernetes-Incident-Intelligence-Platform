# One Dockerfile builds every demo service.
#
# The service to build is chosen with a build argument. Doing it this way
# rather than writing five nearly identical Dockerfiles means the five images
# share the same base layers and the same restore layer, which saves a
# noticeable amount of disk on a machine that is already tight on space.
#
# Build from the repository root, for example:
#   docker build -f src/workload/Dockerfile --build-arg SERVICE=KubeSage.Workload.Gateway .

FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
ARG SERVICE
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/workload/ src/workload/

RUN dotnet restore "src/workload/${SERVICE}/${SERVICE}.csproj"

RUN dotnet publish "src/workload/${SERVICE}/${SERVICE}.csproj" \
    --no-restore \
    --configuration Release \
    --output /app/publish


FROM mcr.microsoft.com/dotnet/aspnet:10.0.9 AS runtime
ARG SERVICE
WORKDIR /app

COPY --from=build /app/publish .

# The entry point needs the assembly name, which is a build argument, so it is
# baked into an environment variable that the shell form can expand.
ENV SERVICE_DLL="${SERVICE}.dll"

# Unprivileged user. None of these services needs root, and running as a
# normal user is also what makes the Kubernetes securityContext in the
# manifests achievable.
USER $APP_UID

EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet \"$SERVICE_DLL\""]
