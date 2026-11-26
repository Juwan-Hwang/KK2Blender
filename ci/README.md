# CI

`github-actions-ci.yml` is the GitHub Actions workflow: it builds the Studio plugin (net35, against
the IllusionMods reference assemblies), runs the format tests, the add-on's pytest suite (including a
cross-check of a track file written by the C# code) and the Blender tests in Blender 5.2, and builds
the release zips. On a `v*` tag it publishes them as a GitHub release.

It lives here until the repository's token can write workflows; then it moves to
`.github/workflows/ci.yml`.
