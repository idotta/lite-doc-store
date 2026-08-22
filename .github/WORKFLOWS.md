# GitHub Actions CI/CD Documentation

This repository uses GitHub Actions for Continuous Integration and Continuous Deployment. Below is a description of each workflow and how to use them.

## Workflows

### 1. CI Workflow (`ci.yml`)

**Triggers:**
- Push to `main` or `develop` branches
- Pull requests to `main` or `develop` branches
- Manual dispatch

**Jobs** (all `ubuntu-latest` — CI is deliberately single-OS to keep it fast):
- **Build and Test**: Builds, runs unit + integration tests with coverage, runs every example (`-- all`), and renders a ReportGenerator summary into the job summary from that same test run
- **Pack**: Packs the library and asserts the nupkg/snupkg actually contain the README, icon, XML docs and PDB
- **AOT Publish**: Publishes `examples/AotVerification` for `linux-x64` with trim/AOT warnings as errors, then runs the native binary
- **Dependency Audit**: Fails on vulnerable packages (`dotnet list package --vulnerable --include-transitive`); reports deprecated ones
- **Security Scan**: CodeQL analysis (`security-extended`)

**Purpose:**
Core CI: the code builds, tests pass, the samples still run against the current API, the package is well formed, the AOT claim is proven by an actual Native AOT publish + run, and no vulnerable dependency slips in.

**Also runs:** Dependency Review on pull requests.

**Not covered:** Windows and macOS. The integration tests contain Windows-specific file-lock workarounds that nothing exercises, and the bundled SQLite version differs per OS — re-add `strategy.matrix.os` if either bites.

**Artifacts:**
- Test results (TRX) and the coverage HTML report
- NuGet package (validation only, versioned `0.0.0-ci`)

**Note:** Formatting, static analysis and documentation checks live in the separate Code Quality workflow.

### 2. Publish Workflow (`publish.yml`)

**Triggers:**
- When a release is published on GitHub (no manual dispatch)

**Steps (single `deploy` job):**
- Resolves `VERSION` from the release tag (`refs/tags/vX.Y.Z`) **before** building, and passes `-p:Version=` to build and pack, so the assembly version and the package version always match
- Builds, tests, packs (symbols included as `.snupkg` via the csproj)
- Attests build provenance for the packed nupkg
- Pushes `*.nupkg` and `*.snupkg` separately, both with `--skip-duplicate`

**Required setup:**
- `NUGET_API_KEY` repository secret

**How to Publish:**
- Create a release on GitHub with a tag like `v1.0.0`; the workflow publishes to NuGet.org

### 3. Code Quality Workflow (`code-quality.yml`)

**Triggers:**
- Push to `main` or `develop` branches
- Pull requests
- Weekly on Sunday at midnight
- Manual dispatch

**Jobs:**
- **Format Check**: Validates code formatting with `dotnet format`
- **Static Code Analysis**: Runs .NET analyzers with warnings as errors
- **Documentation Check**: Validates XML documentation and README

(Coverage reporting lives in the CI workflow, not here.)

**Purpose:**
Formatting, static analysis and documentation validation. Runs in parallel with CI to provide feedback without blocking the main pipeline.

### 4. Label PR Workflow (`label-pr.yml`)

**Triggers:**
- Pull request opened, edited, synchronized, or reopened

**Jobs:**
- Auto-labels PRs based on changed files
- Adds size labels (xs, s, m, l, xl) based on PR size

### 5. Stale Issues/PRs Workflow (`stale.yml`)

**Triggers:**
- Daily at midnight
- Manual dispatch

**Configuration:**
- Issues: Marked stale after 60 days, closed after 7 more days
- PRs: Marked stale after 30 days, closed after 7 more days
- Exempt labels: `pinned`, `security`, `bug`

## Dependabot

Dependabot is configured to automatically check for updates:
- NuGet packages: Weekly on Monday
- GitHub Actions: Weekly on Monday

## Setup Instructions

### 1. Required Secrets

Add these secrets to your repository settings:

- `NUGET_API_KEY`: Your NuGet.org API key
  - Get it from https://www.nuget.org/account/apikeys
  - Requires "Push" permission
  - Set expiration as needed

- `CODECOV_TOKEN` (Optional): For code coverage reporting
  - Get it from https://codecov.io/
  - Not required but recommended

### 2. Repository Settings

Enable these settings in your repository:

1. **Actions → General:**
   - Allow all actions and reusable workflows

2. **Code security and analysis:**
   - Enable Dependabot alerts
   - Enable Dependabot security updates

3. **Branches:**
   - Protect `main` branch
   - Require status checks to pass before merging
   - Require branches to be up to date before merging

### 3. Release Process

1. Update version in code if needed
2. Create a new release on GitHub:
   - Tag: `v1.0.0` (follow semantic versioning)
   - Title: `Release 1.0.0`
   - Description: Changelog
3. Publish the release
4. GitHub Actions will automatically:
   - Run all tests
   - Create NuGet package
   - Publish to NuGet.org
   - Attach package to release

## Status Badges

Add these badges to your README:

```markdown
[![CI](https://github.com/idotta/lite-doc-store/actions/workflows/ci.yml/badge.svg)](https://github.com/idotta/lite-doc-store/actions/workflows/ci.yml)
[![Code Quality](https://github.com/idotta/lite-doc-store/actions/workflows/code-quality.yml/badge.svg)](https://github.com/idotta/lite-doc-store/actions/workflows/code-quality.yml)
[![NuGet](https://img.shields.io/nuget/v/LiteDocumentStore.svg)](https://www.nuget.org/packages/LiteDocumentStore/)
[![codecov](https://codecov.io/gh/idotta/lite-doc-store/branch/main/graph/badge.svg)](https://codecov.io/gh/idotta/lite-doc-store)
```

## Troubleshooting

### Build Failures

1. Check the workflow logs for detailed error messages
2. Ensure all dependencies are compatible with .NET 10
3. Verify SQLite 3.45+ is available (included in Microsoft.Data.Sqlite 10.0.0)

### Publish Failures

1. Verify `NUGET_API_KEY` secret is set correctly
2. Check that the version number doesn't already exist on NuGet.org
3. Ensure the package ID `LiteDocumentStore` is available or owned by you

### Coverage Report Issues

1. Coverage reports require the `coverlet.collector` package (already included in test projects)
2. If Codecov upload fails, check that `CODECOV_TOKEN` is set (optional but recommended)

## Contributing

When contributing:
1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Ensure all tests pass locally: `dotnet test`
5. Create a pull request
6. CI will automatically run on your PR
7. Address any issues flagged by CI
