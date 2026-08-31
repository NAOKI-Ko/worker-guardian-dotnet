# Releasing

The project follows semantic versioning. Releases are produced from an annotated tag on `main` and
published as NuGet and symbol packages on GitHub. NuGet.org publication remains optional until a
repository secret or trusted publishing flow is deliberately configured.

## Prepare and review

1. Start `release/vX.Y.Z` from the latest `main`.
2. Update `<Version>` in `src/WorkerGuardian/WorkerGuardian.csproj` and move completed changelog
   entries into a dated Keep a Changelog section.
3. Run the local gate:

   ```bash
   dotnet restore WorkerGuardian.slnx --locked-mode
   dotnet build WorkerGuardian.slnx -c Release --no-restore -m:1
   dotnet format WorkerGuardian.slnx --verify-no-changes --no-restore
   dotnet run --project tests/WorkerGuardian.Tests -c Release --no-build -- \
     --minimum-expected-tests 20 --coverage \
     --coverage-output artifacts/coverage.cobertura.xml \
     --coverage-output-format cobertura
   dotnet build benchmarks/WorkerGuardian.Benchmarks -c Release --no-restore
   dotnet pack src/WorkerGuardian/WorkerGuardian.csproj -c Release --no-build \
     -o artifacts/packages
   ```

4. Confirm both `.nupkg` and `.snupkg` contents and metadata.
5. Open a pull request describing quality gates, packaging, release behavior, and breaking changes.
6. Merge only after CI succeeds, then fast-forward the local `main` checkout.

## Tag and publish

Create and push an annotated `vX.Y.Z` tag at the reviewed `main` commit. The release workflow checks
that the tag matches the project version, reruns locked restore, analyzers, formatting, tests and
both coverage gates, builds the benchmark project, packs the library, and attaches NuGet and symbol
packages to a non-draft GitHub Release.

NuGet.org publication requires a preconfigured secret or trusted publisher. Never create, expose,
or commit registry credentials, and do not claim registry availability until it is verified.

## Failed release or rollback

Never move or force-push a published tag. If the workflow fails before a Release exists, fix the
cause through a new pull request and determine whether the unused tag can be safely deleted or the
next patch version should be used. Once artifacts are public, preserve them for auditability,
document defects, and publish a corrected patch release instead of replacing packages in place.
