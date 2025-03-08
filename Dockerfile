FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0.200-noble@sha256:12e2373b9ea6f904e0d255a54e65eae31d78ae542dc612baa01fe59198e3e22a AS build
WORKDIR /build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

COPY src/PathlingS3Import/PathlingS3Import.csproj .
COPY src/PathlingS3Import/packages.lock.json .
COPY src/Directory.Build.props .

RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages dotnet restore --locked-mode
COPY . .

ARG VERSION=3.1.0
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages dotnet publish \
    -c Release \
    -p:Version=${VERSION} \
    -o /build/publish \
    src/PathlingS3Import/PathlingS3Import.csproj

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/runtime:9.0.2-noble-chiseled@sha256:a3120b72a2f7f22f71e4ab2a8dbc814ccebdff21b2bf018e96427fb4e7eb50c8
WORKDIR /opt/pathling-s3-import
USER 65534:65534
ENV DOTNET_ENVIRONMENT="Production" \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

COPY --from=build /build/publish .
ENTRYPOINT ["dotnet", "/opt/pathling-s3-import/PathlingS3Import.dll"]
CMD [ "--help"]
