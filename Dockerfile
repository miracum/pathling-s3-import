FROM mcr.microsoft.com/dotnet/sdk:8.0.203-jammy@sha256:c2c75cb385be90e8ade1dbe44cbb5a6195b7dbbe3386772da8b17fd0277a3d5f AS build
WORKDIR /build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

COPY src/PathlingS3Import/PathlingS3Import.csproj .
COPY src/PathlingS3Import/packages.lock.json .

RUN dotnet restore --locked-mode
COPY . .

ARG VERSION=1.2.5
RUN dotnet publish \
    -c Release \
    -p:Version=${VERSION} \
    -o /build/publish \
    src/PathlingS3Import/PathlingS3Import.csproj

FROM mcr.microsoft.com/dotnet/runtime:8.0.3-jammy-chiseled@sha256:2ae069fda85aec19b05dcb4524d794b67202cd6c870d52f77ff93b53b279b6f7
WORKDIR /opt/pathling-s3-import
USER 65534:65534
ENV DOTNET_ENVIRONMENT="Production" \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

COPY --from=build /build/publish .
ENTRYPOINT ["dotnet", "/opt/pathling-s3-import/PathlingS3Import.dll"]
CMD [ "--help"]
