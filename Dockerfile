FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0.102-noble@sha256:67aba0077ce07ea5974bf139a47a4a144bd8f7819a3d91faff02bca4919e9932 AS build
WORKDIR /build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

COPY src/PathlingS3Import/PathlingS3Import.csproj .
COPY src/PathlingS3Import/packages.lock.json .
COPY src/Directory.Build.props .

RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages dotnet restore --locked-mode
COPY . .

ARG VERSION=3.0.0
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages dotnet publish \
    -c Release \
    -p:Version=${VERSION} \
    -o /build/publish \
    src/PathlingS3Import/PathlingS3Import.csproj

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/runtime:9.0.1-noble-chiseled@sha256:0de374fde25c5105e2fe96b04ce7ec01fde363a49f446dc25ebaf038392a3176
WORKDIR /opt/pathling-s3-import
USER 65534:65534
ENV DOTNET_ENVIRONMENT="Production" \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

COPY --from=build /build/publish .
ENTRYPOINT ["dotnet", "/opt/pathling-s3-import/PathlingS3Import.dll"]
CMD [ "--help"]
