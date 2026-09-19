# Contributing

## Code of conduct

Participation in this project is governed by our
[Code of Conduct](CODE_OF_CONDUCT.md).

## Security

Found a vulnerability? See [SECURITY.md](SECURITY.md) - please don't
report it via a public issue.

## Before you start

Every PR should be tied to an existing issue or feature request. If there
isn't one yet, open one first using the
[bug report](.github/ISSUE_TEMPLATE/bug_report.md) or
[feature request](.github/ISSUE_TEMPLATE/feature_request.md) template, then
reference it from your PR description.

## Contributing

Anyone can fork the repo and submit a PR.

AI-assisted code is welcome, but the human author must have reviewed it and
be able to explain and attest that it's correct - "the AI wrote it" is not
a substitute for understanding the change you're submitting.

## Branches

- `main` tracks the current Jellyfin LTS and takes both fixes and
  enhancements.
- Numbered branches (currently just `10.11`, more will be added as older
  Jellyfin LTS versions need continued support) take bugfix backports
  only - never new features.

Target the branch matching the Jellyfin version your change is for.

## Versioning

Each branch's version lives in `Directory.Build.props`
(`Version`/`AssemblyVersion`/`FileVersion`). Bump it as part of your PR.

Version numbers are **not** shared or reconciled across branches - each
branch's version line is independent. For example, `10.11` sits in the
`3.x` range while `main` has moved well past it, since enhancements have
kept landing there since `10.11` was cut. A fix backported to `10.11` stays
in its own `3.x` line; the same fix on `main` is just the next increment
of whatever version `main` is currently on.

You can propose a version in your PR but I reserve the right to change it.

## Testing

See [Development](docs/DEVELOPMENT.md) for how to run the plugin and
sidecar test suites.

- Test your change against your own Jellyfin library, or a disposable test
  library, before opening the PR.
- If you modify a class that already has unit tests, add or update tests
  covering your change.
- A PR whose unit tests or GitHub code scanning checks fail will be
  rejected automatically.

## Design principles

- Keep changes as minimal and simple as possible.
- Never break existing behavior - for example, a new parameter must ship
  with a backward-compatible default.
