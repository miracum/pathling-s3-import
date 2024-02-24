# pathling-s3-import

Tool for automatically [$import'ing](https://pathling.csiro.au/docs/server/operations/import) lists of ndjson FHIR resources from an S3-compatible bucket into a Pathling server.

## Usage

See the help text of the command by simply running:

```sh
docker run --rm -it ghcr.io/miracum/pathling-s3-import:v1.1.1
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

```sh
dotnet run --project src/PathlingS3Import/ -- \
    --s3-endpoint=http://localhost:9000 \
    --pathling-server-base-url=http://localhost:8082/fhir \
    --s3-access-key=admin \
    --s3-secret-key=miniopass \
    --s3-bucket-name=fhir \
    --s3-object-name-prefix=staging/ \
    --dry-run=false
```
