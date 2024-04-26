FROM mcr.microsoft.com/dotnet/sdk:8.0.204-jammy@sha256:8e2c7b29ec394ae5bfd9d986085b233cb3ce6ade2898574a6fcd3eaa7b002389 AS build
WORKDIR /build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

COPY src/PathlingS3Import/PathlingS3Import.csproj .
COPY src/PathlingS3Import/packages.lock.json .

RUN dotnet restore --locked-mode
COPY . .

ARG VERSION=1.3.2
RUN dotnet publish \
    -c Release \
    -p:Version=${VERSION} \
    -o /build/publish \
    src/PathlingS3Import/PathlingS3Import.csproj

FROM mcr.microsoft.com/dotnet/runtime:8.0.4-jammy-chiseled@sha256:55123c37f3c365a638efb6987497e3210e9559d9ce928dcc0251eb13629747bd
WORKDIR /opt/pathling-s3-import
USER 65534:65534
ENV DOTNET_ENVIRONMENT="Production" \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

COPY --from=build /build/publish .
ENTRYPOINT ["dotnet", "/opt/pathling-s3-import/PathlingS3Import.dll"]
CMD [ "--help"]
