# GitHub Actions CI/CD Documentation

This repository uses GitHub Actions for Continuous Integration and Continuous Deployment. Below is a description of each workflow and how to use them.

## Workflows

### 1. CI Workflow (`ci.yml`)

**Triggers:**
- Push to `main` or `develop` branches
- Pull requests to `main` or `develop` branches
- Manual dispatch

**Jobs:**
- **Build and Test**: Builds the project and runs all tests on Ubuntu, Windows, and macOS
- **Security Scan**: Performs CodeQL security analysis for vulnerability detection
- **Package Validation**: Creates a NuGet package to validate packaging

**Purpose:**
This workflow focuses on core CI tasks - ensuring the code builds, tests pass on all platforms, security vulnerabilities are detected, and the package can be created successfully.

**Artifacts:**
- Test results (TRX files) from all platforms
- NuGet package (validation only)

**Note:** Code quality checks (formatting, coverage, static analysis) are handled by the separate Code Quality workflow to keep CI fast and focused.

### 2. Publish Workflow (`publish.yml`)

**Triggers:**
- When a release is published on GitHub
- Manual dispatch with version input

**Jobs:**
- **Validate**: Runs all tests before publishing
- **Publish**: Creates and publishes NuGet package to NuGet.org
- **Create GitHub Release**: Attaches NuGet packages to GitHub release

**Required Secrets:**
- `NUGET_API_KEY`: Your NuGet.org API key

**How to Publish:**

1. **Automatic (Recommended):**
   - Create a new release on GitHub with a tag like `v1.0.0`
   - The workflow will automatically publish to NuGet.org

2. **Manual:**
   - Go to Actions → Publish NuGet Package → Run workflow
   - Enter the version number (e.g., `1.0.0`)

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
- **Test Coverage**: Generates detailed coverage reports with ReportGenerator and comments on PRs

**Purpose:**
This workflow provides comprehensive code quality checks including formatting, static analysis, documentation validation, and detailed test coverage reporting. It runs in parallel with CI to provide feedback without blocking the main CI pipeline.

**Artifacts:**
- Detailed coverage reports (HTML and Markdown)

**Note:** This workflow complements CI by providing deeper quality insights. While CI focuses on build/test/security, this workflow ensures code quality standards are met.
- **Test Coverage**: Generates detailed coverage reports

**Artifacts:**
- Coverage reports (HTML and Markdown)

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
