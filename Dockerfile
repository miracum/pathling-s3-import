FROM mcr.microsoft.com/dotnet/sdk:8.0.201-jammy@sha256:dc273e23006f85ef4bf154844a3147325c50adc4b5dac9191238bed4931743ac AS build
WORKDIR /build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

COPY src/PathlingS3Import/PathlingS3Import.csproj .
COPY src/PathlingS3Import/packages.lock.json .

RUN dotnet restore --locked-mode
COPY . .

ARG VERSION=1.0.0
RUN dotnet publish \
    -c Release \
    -p:Version=${VERSION} \
    -o /build/publish \
    src/PathlingS3Import/PathlingS3Import.csproj

FROM mcr.microsoft.com/dotnet/runtime:8.0.2-jammy@sha256:8a8b6038864efd998525050d8dc16cec2825648d587747c30d1d6b4280a39880 AS runtime
WORKDIR /opt/pathling-s3-import
USER 65534:65534
ENV DOTNET_ENVIRONMENT="Production" \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

COPY --from=build /build/publish .
ENTRYPOINT ["/bin/bash", "-c"]
CMD [ "/opt/pathling-s3-import/PathlingS3Import --help"]
