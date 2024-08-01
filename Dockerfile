FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0.303-noble@sha256:0ece4e7951bc9ab64577b5a56e541a618c67d6623e9104e9cc5ab7d762f16d75 AS build
WORKDIR /build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

COPY src/PathlingS3Import/PathlingS3Import.csproj .
COPY src/PathlingS3Import/packages.lock.json .

RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages dotnet restore --locked-mode
COPY . .

ARG VERSION=2.1.3
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages dotnet publish \
    -c Release \
    -p:Version=${VERSION} \
    -o /build/publish \
    src/PathlingS3Import/PathlingS3Import.csproj

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/runtime:8.0.7-noble-chiseled@sha256:89d22da5947c002489990e99362cce58c13d0b05cdf709f9ad34dbb330a59cdf
WORKDIR /opt/pathling-s3-import
USER 65534:65534
ENV DOTNET_ENVIRONMENT="Production" \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

COPY --from=build /build/publish .
ENTRYPOINT ["dotnet", "/opt/pathling-s3-import/PathlingS3Import.dll"]
CMD [ "--help"]
