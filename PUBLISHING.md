# Publishing

> **Develop-only for now.** Nothing is published to NuGet.org until an explicit release decision. This document captures what has to happen before and during the first release.

## Before the first release

1. **Package names — finalize.** Current PackageIds are the internal ones: `CPlugin.SaaSWebApi.Client` + `CPlugin.SaaSWebApi.Models`. For the public release decide whether to rebrand to match the JS/Python SDKs (`@mywebapi.com/sdk` / `mywebapi-sdk`) — e.g. `MyWebApi.Sdk` + `MyWebApi.Sdk.Models` — or keep the CPlugin names. NuGet IDs are a global, first-come namespace: verify availability (https://www.nuget.org/packages/<id>) just before the first release and reserve the prefix via [NuGet package ID prefix reservation](https://learn.microsoft.com/en-us/nuget/nuget-org/id-prefix-reservation) if rebranding.
2. **Repository** — `CPlugin/mywebapi.com-sdk-dotnet` (mirrors the JS SDK `CPlugin/mywebapi.com-sdk-js` and Python `CPlugin/mywebapi.com-sdk-py`). Create the GitHub repo under this org/name, then set `RepositoryUrl`/`PackageProjectUrl` in `Directory.Build.props` accordingly.
3. **Authentication: NuGet Trusted Publishing (OIDC)** — no long-lived API key to store or rotate. On https://www.nuget.org, go to your account → *Trusted Publishing* → add a policy: Owner = `<github-org>`, Repository = `<repo>`, Workflow = `publish.yml`, Environment = `nuget`. Create a matching GitHub **Environment** named `nuget` (Settings → Environments) with protection rules (required reviewers, restrict to tags). The `publish.yml` workflow is intentionally **not** committed yet — add it together with the release decision.
4. **Metadata sweep** — `Version`, `Description`, `PackageTags`, `PackageLicenseExpression` (MIT), `PackageReadmeFile`, symbol packages (`snupkg`), SourceLink.

## Releasing a version

1. Bump `<Version>` in `Directory.Build.props` following [semver](https://semver.org/).
2. Commit and push the version bump to `main`.
3. Tag the commit and push the tag:
   ```sh
   git tag v0.2.0
   git push origin v0.2.0
   ```
4. The (future) `publish.yml` workflow builds, packs and pushes both packages to NuGet.org via the OIDC trusted-publishing policy.

## Building locally (sanity check)

```sh
dotnet build CPlugin.SaaSWebApi.Client.sln -c Release
dotnet test tests/CPlugin.SaaSWebApi.Client.Tests/CPlugin.SaaSWebApi.Client.Tests.csproj -c Release --no-build
dotnet pack src/CPlugin.SaaSWebApi.Models/CPlugin.SaaSWebApi.Models.csproj -c Release -o nupkgs
dotnet pack src/CPlugin.SaaSWebApi.Client/CPlugin.SaaSWebApi.Client.csproj -c Release -o nupkgs
```

## Regenerating the client from a new spec

```sh
./scripts/fetch-spec.sh          # WEBAPI_BASE_URL to pick the host (default: staging)
./scripts/generate-models.sh     # NSwag → src/CPlugin.SaaSWebApi.Models/Generated/Dto.g.cs
./scripts/generate-endpoints.sh  # bespoke generator → src/.../Generated/MT4Endpoints.g.cs + MT5Endpoints.g.cs
dotnet test tests/CPlugin.SaaSWebApi.Client.Tests/CPlugin.SaaSWebApi.Client.Tests.csproj
```

The generated-surface test asserts the method count against the spec (172 operations today) — update it alongside spec changes.
