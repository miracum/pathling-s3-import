# pathling-s3-import

[![OpenSSF Scorecard](https://api.securityscorecards.dev/projects/github.com/miracum/pathling-s3-import/badge)](https://api.securityscorecards.dev/projects/github.com/miracum/pathling-s3-import)
[![SLSA 3](https://slsa.dev/images/gh-badge-level3.svg)](https://slsa.dev)

Tool for automatically [$import'ing](https://pathling.csiro.au/docs/server/operations/import) lists of ndjson FHIR resources from an S3-compatible bucket into a Pathling server.

## Usage

See the help text of the command by simply running:

```sh
docker run --rm -it ghcr.io/miracum/pathling-s3-import:v2.1.3
```

## Development

Launch development fixtures:

```sh
docker compose up
```

Install dependencies

```sh
dotnet restore
dotnet tool restore
```

Start the tool

### import

```sh
dotnet run --project src/PathlingS3Import/ -- import \
    --s3-endpoint=http://localhost:9000 \
    --pathling-server-base-url=http://localhost:8082/fhir \
    --s3-access-key=admin \
    --s3-secret-key=miniopass \
    --s3-bucket-name=fhir \
    --s3-object-name-prefix=staging/ \
    --enable-metrics=true \
    --pushgateway-endpoint=http://localhost:9091/ \
    --dry-run=false
```

Or to test importing from a checkpoint:

```sh
dotnet run --project src/PathlingS3Import/ -- import \
    --s3-endpoint=http://localhost:9000 \
    --pathling-server-base-url=http://localhost:8082/fhir \
    --s3-access-key=admin \
    --s3-secret-key=miniopass \
    --s3-bucket-name=fhir \
    --s3-object-name-prefix=staging-with-checkpoint/ \
    --continue-from-last-checkpoint=true \
    --dry-run=false
```

### merge

```sh
dotnet run --project src/PathlingS3Import/ -- merge \
    --s3-endpoint=http://localhost:9000 \
    --s3-access-key=admin \
    --s3-secret-key=miniopass \
    --s3-bucket-name=fhir \
    --s3-object-name-prefix=staging/ \
    --max-merged-bundle-size=10 \
    --dry-run=true
```

### Run E2E Tests

```sh
docker build -t ghcr.io/miracum/pathling-s3-import:test .

dotnet test
```
