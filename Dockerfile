FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0.300-noble@sha256:9f7bd4d010026e15a57d9cf876f2f7d08c3eeed6a0ea987b8c5ba8c75e68e948 AS build
WORKDIR /build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

COPY src/PathlingS3Import/PathlingS3Import.csproj .
COPY src/PathlingS3Import/packages.lock.json .
COPY src/Directory.Build.props .

RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages dotnet restore --locked-mode
COPY . .

ARG VERSION=3.1.4
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages dotnet publish \
    -c Release \
    -p:Version=${VERSION} \
    -o /build/publish \
    src/PathlingS3Import/PathlingS3Import.csproj

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/runtime:9.0.5-noble-chiseled@sha256:bd4288d187eac2d9753e4623e0466b9ceec2b340254a640858d3ebb1b25afbac
WORKDIR /opt/pathling-s3-import
USER 65534:65534
ENV DOTNET_ENVIRONMENT="Production" \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

COPY --from=build /build/publish .
ENTRYPOINT ["dotnet", "/opt/pathling-s3-import/PathlingS3Import.dll"]
CMD [ "--help"]
