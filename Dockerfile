FROM mcr.microsoft.com/dotnet/sdk:8.0.300-jammy@sha256:3208c409ba7a2a259920cd487da7bef70b0645dc04c35db70473839f3ac0865f AS build
WORKDIR /build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

COPY src/PathlingS3Import/PathlingS3Import.csproj .
COPY src/PathlingS3Import/packages.lock.json .

RUN dotnet restore --locked-mode
COPY . .

ARG VERSION=2.1.0
RUN dotnet publish \
    -c Release \
    -p:Version=${VERSION} \
    -o /build/publish \
    src/PathlingS3Import/PathlingS3Import.csproj

FROM mcr.microsoft.com/dotnet/runtime:8.0.5-jammy-chiseled@sha256:e30948c62bb2918891e4e533cadc6b56ae8a50afa504751e6f353b8f0295c5d0
WORKDIR /opt/pathling-s3-import
USER 65534:65534
ENV DOTNET_ENVIRONMENT="Production" \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

COPY --from=build /build/publish .
ENTRYPOINT ["dotnet", "/opt/pathling-s3-import/PathlingS3Import.dll"]
CMD [ "--help"]
