# Release Process

How a version of this repo reaches NuGet, npm, PyPI and the Go module proxy — and the specific
things that have broken a release here before. Four SDKs ship from one repo on four independent
tags, which is where most of the sharp edges come from.

**Read the [Gotchas](#gotchas) section before releasing.** Every item in it cost a follow-up PR at
least once.

---

## What Ships

| Language | Package | Manifest | Tag prefix |
|---|---|---|---|
| .NET | `MQuark.Actionful.Client` (NuGet) | `src/dotnet/Actionful.Client/Actionful.Client.csproj` | `v2.0.1` |
| JS/TS | `@mquark/actionful-client` (npm) | `src/js/package.json` | `npm-v2.0.1` |
| Python | `mquark-actionful-client` (PyPI) | `src/python/pyproject.toml` | `pypi-v2.0.1` |
| Go | `github.com/m-quark/ActionfulClient/src/go/v2` | `src/go/go.mod` | `src/go/v2.0.1` |

All four release at the same version. Nothing in the tooling enforces that — it is a convention,
kept by doing the whole set together.

Secrets consumed by `.github/workflows/publish.yml`: `NUGET_API_KEY`, `NPM_TOKEN`,
`PYPI_API_TOKEN`. Go needs no secret; see [Go publishes itself](#go-publishes-itself).

---

## The Procedure

### 1. Bump the manifests in a PR

One PR, all four manifests, merged to `master` before any tag exists. The tag must point at a
commit whose manifests already say the new version.

- `Actionful.Client.csproj` → `<Version>`
- `src/js/package.json` → `"version"`
- `src/python/pyproject.toml` → `version`
- `src/go/go.mod` → only on a **major** bump, where the module path suffix changes (`/v2` → `/v3`)

For a major bump, `go.mod` is not the only place: the `MODULE` variable in the Go publish job and
every install snippet move with it. See [Go major versions](#go-major-versions).

### 2. Verify master is green, then tag

```sh
git checkout master && git pull
gh run list --branch master --limit 3   # green on the exact commit you are tagging
```

### 3. Push tags — one at a time

```sh
git tag v2.0.1        && git push origin v2.0.1        && sleep 10
git tag npm-v2.0.1    && git push origin npm-v2.0.1    && sleep 10
git tag pypi-v2.0.1   && git push origin pypi-v2.0.1   && sleep 10
git tag src/go/v2.0.1 && git push origin src/go/v2.0.1
```

Not `git push origin --tags`. See [More than three tags at once](#more-than-three-tags-at-once).

### 4. Watch every run, and do not trust the summary

```sh
gh run list --limit 6
gh run view <id> --log-failed
```

A red job does not always mean nothing published, and — historically — a green job did not always
mean something did.

### 5. Confirm against the registries

```sh
# NuGet
curl -s "https://azuresearch-usnc.nuget.org/query?q=packageid:MQuark.Actionful.Client&prerelease=true"

# npm
curl -s https://registry.npmjs.org/@mquark/actionful-client | grep -o '"latest":"[^"]*"'

# PyPI
curl -s https://pypi.org/pypi/mquark-actionful-client/json \
  | python -c "import sys,json;print(json.load(sys.stdin)['info']['version'])"

# Go — note the case escaping and the /v2 suffix
curl -s "https://proxy.golang.org/github.com/m-quark/!actionful!client/src/go/v2/@v/v2.0.1.info"
```

NuGet and PyPI index within a few minutes of a successful push; npm and the Go proxy are
effectively immediate.

---

## Gotchas

### More than three tags at once

**GitHub does not fire `push` events for tags when more than three arrive in one push.** Pushing
all four at once produces *zero* workflow runs — no error, no runs, nothing to notice except the
absence of output. This is documented GitHub behaviour, not a quirk of this repo.

Push tags individually. If you already pushed a batch and see no runs, delete and re-push them one
at a time (see [Re-running a release](#re-running-a-release)).

### The tag is the version, not the manifest

NuGet, npm and PyPI all take the version from the tag and **overwrite the manifest during the
build**: `-p:Version=`, `npm version`, and a `sed` on `pyproject.toml` respectively. A tag of
`v2.0.2` against manifests reading `2.0.1` will publish 2.0.2 quite happily.

So why bump the manifests at all? Because they are what a developer building locally, and every
README, actually reads. The manifest is documentation; the tag is the release. Keeping them in
step is a discipline, not a safety net — nothing stops you if you skip it.

The one exception is `go.mod`, whose module path is overwritten by nothing and *is* load bearing.

### `npm version` fails when the version already matches

Because step 1 bumps `package.json` to the version you are about to tag, `npm version 2.0.1` finds
nothing to change and exits non-zero — `npm error Version not changed`. The manifest bump and the
publish step disagree about who owns the version, and the publish step loses.

Fixed with `--allow-same-version` in the workflow. If you see this error, the workflow has been
reverted.

### Go publishes itself

There is no upload step for Go. **The tag is the publication.** The workflow's final step only
*fetches* the module through `proxy.golang.org`, which makes it resolvable and indexes it on
pkg.go.dev — but the module exists the moment the tag is pushed.

The consequence: **a failed Go job does not mean an unpublished module.** This has happened — the
fetch queried the wrong path, the job went red, and the module was live and `go get`-able the
whole time. Check the proxy before re-tagging anything.

The inverse used to be true and was worse. The step once ended in `|| true`, so it could not fail
— and reported success for months against a module path that did not exist. The current step uses
`curl -sSf` with retries and no swallow, deliberately.

### Go module paths are three coupled facts

For the subdirectory module at `src/go`, these must all agree, and none of them checks the others:

| | Value | Note |
|---|---|---|
| `go.mod` module | `github.com/m-quark/ActionfulClient/src/go/v2` | path **plus** major suffix |
| Git tag | `src/go/v2.0.1` | directory prefix, **no** major suffix |
| Proxy URL in the workflow | `github.com/m-quark/!actionful!client/src/go/v2` | escaped **and** suffixed |

Two things bite here:

- **Case escaping.** Proxy URLs lowercase every uppercase letter and prefix it with `!`, so
  `ActionfulClient` becomes `!actionful!client`. An unescaped URL returns 404 with no hint why.
- **The major suffix appears in two of the three, not the third.** The tag prefix is a
  *directory*, and directories do not carry `/v2`. Adding `/v2` to `go.mod` without adding it to
  the workflow's `MODULE` produced exactly the red-job-live-module case above.

### Go major versions

At v2 and above, Go requires the major suffix in the module path — a v2 module is a *different
import path*, not a new version of the old one. Bumping to v3 means editing, in lockstep:

1. `src/go/go.mod` module line → `/v3`
2. Every internal import within `src/go`
3. `MODULE` in the Go publish job → `/v3`
4. Install and import snippets: this repo's `README.md`, and **`EndpointIntegration.razor` in
   mqPlatform**, which is a separate repo and is caught by nothing here

### The dead `go-v1.0.0` tag

`go-v1.0.0` exists in this repo's history and published nothing. It predates the subdirectory
module layout and used a prefix the module system has no meaning for. Left in place as history;
ignore it, and do not use `go-v*` as a prefix.

### npm entry points must match what tsup emits

`package.json` declares `main`/`module`/`types`/`exports` pointing at `dist/index.js`,
`dist/index.mjs`, `dist/index.d.ts` and `dist/index.d.mts`. tsup's output naming has to line up
with all of them. Version 2.0.0 shipped pointing at a file the build never produced, so a plain
`require()` failed with `MODULE_NOT_FOUND` — published, installable, and completely broken.

Inspection does not catch this. Before an npm release, check the actual tarball:

```sh
cd src/js && npm run build && npm pack
tar -tzf mquark-actionful-client-*.tgz     # every declared path must be present
```

Also confirm `README.md` and `LICENSE` are listed in `files` — npm ships neither by default, and
2.0.0 went out with no README on the package page.

### Re-running a release

Tags are the trigger, so re-triggering means moving the tag:

```sh
git tag -d npm-v2.0.1
git push origin :refs/tags/npm-v2.0.1
git tag npm-v2.0.1 <sha> && git push origin npm-v2.0.1
```

Safe when the failed run died **before** its publish step. If it published and then failed, this
will not work and you need a new patch version instead:

- **NuGet** pushes with `--skip-duplicate`, so a re-run is a no-op rather than an error.
- **npm and PyPI forbid republishing a version**, even an identical one. Burn the number.
- **Go** cannot be fixed by re-tagging at all — the proxy caches a version's content permanently
  by hash. A wrong `src/go/v2.0.1` is wrong forever; ship v2.0.2.

---

## Pre-flight Checklist

- [ ] Manifests bumped and merged to `master`; CI green on the exact commit being tagged
- [ ] `go.mod` path checked — and, on a major bump, the workflow `MODULE` and every install snippet
- [ ] `npm pack` tarball contains every path `package.json` declares, plus README and LICENSE
- [ ] Tags pushed **one at a time**, never `--tags`
- [ ] Four workflow runs exist — count them; a missing run is the three-tag trap
- [ ] All four registries confirmed by query, not by a green check mark
- [ ] mqPlatform `EndpointIntegration.razor` still matches the published install instructions
