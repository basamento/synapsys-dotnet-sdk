# Releasing

NuGet.org is the canonical distribution channel. GitHub Releases mirror the immutable `.nupkg` and `.snupkg` artifacts for provenance and convenience; consumers should normally use `PackageReference` from NuGet.org.

## One-time NuGet.org setup

1. Sign in to NuGet.org with the account or organization that should own `Basamento.Synapsys` and `Basamento.Synapsys.Extensions.Hosting`.
2. Confirm the IDs are available. Uploading a package claims its ID; request the `Basamento` package-ID prefix reservation once ownership requirements are met. Reserve/claim both IDs before publicly announcing the first release.
3. In NuGet.org Trusted Publishing, add a GitHub Actions policy for owner `basamento`, repository `synapsys-dotnet-sdk`, workflow file `release.yml`, and environment `nuget`.
4. In the GitHub `nuget` environment, add the `NUGET_USER` secret containing the NuGet.org profile name (not an email address). Add required reviewers if desired.
5. Protect release tags and limit who can approve the `nuget` environment.

Trusted publishing exchanges GitHub's OIDC identity for a short-lived NuGet API key. A long-lived NuGet API key is therefore neither stored nor required. If trusted publishing is unavailable on the account, use a tightly scoped, short-lived `NUGET_API_KEY` only as a temporary fallback and update the workflow deliberately.

Package signing is not required for the initial release. NuGet.org signs packages at repository level; author signing can be added later only with a documented certificate custody and renewal process.

## Release process

1. Ensure `main` is clean and CI is green.
2. Move public API entries from `PublicAPI.Unshipped.txt` to `PublicAPI.Shipped.txt` for a stable release and update `CHANGELOG.md`.
3. Choose a SemVer 2.0 version. Prereleases use forms such as `1.0.0-alpha.1`, `1.0.0-beta.1`, or `1.0.0-rc.1`.
4. Create and push one annotated tag whose name is exactly `vX.Y.Z` or `vX.Y.Z-prerelease`.
5. The release workflow restores locked dependencies, formats, builds, tests, packs, validates the tag-derived version, obtains a one-use credential, publishes both package IDs, and creates a GitHub Release with packages and symbols attached.

```shell
git tag -a v0.1.0-alpha.1 -m "Synapsys .NET SDK v0.1.0-alpha.1"
git push origin v0.1.0-alpha.1
```

NuGet versions are immutable: the workflow intentionally does not use `--skip-duplicate`. If either version already exists, publishing fails instead of silently accepting a partial or repeated release. Never delete and attempt to republish a released version; increment the version and publish a corrective release.

The two packages always share one version and the hosting package pins its dependency on the core package to that generated package version.

