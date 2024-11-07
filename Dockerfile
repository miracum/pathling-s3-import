FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0.403-noble@sha256:363fb696f1d3b48c2342e08aca3e6286c55b844dbd6865ccf79f54b588076737 AS build
WORKDIR /build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

COPY src/PathlingS3Import/PathlingS3Import.csproj .
COPY src/PathlingS3Import/packages.lock.json .

RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages dotnet restore --locked-mode
COPY . .

ARG VERSION=2.2.0
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages dotnet publish \
    -c Release \
    -p:Version=${VERSION} \
    -o /build/publish \
    src/PathlingS3Import/PathlingS3Import.csproj

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/runtime:8.0.10-noble-chiseled@sha256:90d9976fab6cb55a44516c695ab34dbca3cf1de0011ac971301fd5e90b016fa1
WORKDIR /opt/pathling-s3-import
USER 65534:65534
ENV DOTNET_ENVIRONMENT="Production" \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

COPY --from=build /build/publish .
ENTRYPOINT ["dotnet", "/opt/pathling-s3-import/PathlingS3Import.dll"]
CMD [ "--help"]
