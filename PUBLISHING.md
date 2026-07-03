# Publishing

Releases go to **NuGet.org** automatically from the `publish.yml` workflow, triggered by a version tag. Authentication is **NuGet Trusted Publishing (OIDC)** — no long-lived API key to store or rotate.

Package names (final): **`MyWebApi.Sdk`** (client) + **`MyWebApi.Sdk.Models`** (DTO-only) — brand parity with the JS `@mywebapi.com/sdk` and Python `mywebapi-sdk`. Root namespaces in code stay `CPlugin.SaaSWebApi.*` (same split as Python: dist `mywebapi-sdk` / import `cplugin_webapi_sdk`).

## One-time setup before the first release

1. **Verify the package ids are free** (NuGet is a global, first-come namespace): <https://www.nuget.org/packages/MyWebApi.Sdk> and <https://www.nuget.org/packages/MyWebApi.Sdk.Models>. Consider reserving the `MyWebApi.` prefix afterwards via [package ID prefix reservation](https://learn.microsoft.com/en-us/nuget/nuget-org/id-prefix-reservation).
2. **GitHub repository** — `CPlugin/mywebapi.com-sdk-dotnet` (mirrors the JS/Python SDK repos). `Directory.Build.props` already points `RepositoryUrl`/`PackageProjectUrl` at it.
3. **Trusted Publishing policy** on <https://www.nuget.org/account/trustedpublishing> → *Add policy*:
   - Package owner: the account/organization that will own the packages
   - Repository Owner: `CPlugin`
   - Repository: `mywebapi.com-sdk-dotnet`
   - Workflow File: `publish.yml` (file name only, no path)
   - Environment: `nuget`

   The policy stays "temporarily active" (7 days) until the first successful publish, then becomes permanent.
4. **GitHub Environment** `nuget` (repo *Settings → Environments → New environment*):
   - secret `NUGET_USER` = the nuget.org **profile name** (NOT the email);
   - recommended: *Required reviewers* protection rule — a human approves each publish run.

## Releasing a version

1. Bump `<Version>` in `Directory.Build.props` (informational for local builds) following [semver](https://semver.org/).
2. Commit and push to `main`.
3. Tag and push the tag — **the tag is the source of truth for the published version** (`publish.yml` packs with `-p:Version=` derived from it):
   ```sh
   git tag v0.2.0
   git push origin v0.2.0
   ```
4. `publish.yml` builds, runs the hermetic tests, packs both packages (+ `snupkg` symbols, SourceLink), exchanges the GitHub OIDC token for a short-lived NuGet API key and pushes to NuGet.org. `--skip-duplicate` makes re-runs idempotent.

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
