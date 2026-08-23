# Two workflow edits you have to make by hand

`.github/workflows/` is protected against writes from this session, so these two
changes did not land automatically. Everything else is already in the repo.

## 1. Add the new workflow

Copy `sidecar.yml` (delivered in the chat alongside this file) to:

```
.github/workflows/sidecar.yml
```

It builds and tests on every push touching `Sidecar/`, and cuts a release when
you push a `sidecar-vX.Y.Z` tag.

## 2. Exclude `Sidecar` from the addon zip

In `.github/workflows/release.yml`, the **Build the zip** step. Without this,
about forty C# files ship inside the CurseForge zip.

```diff
           rsync -a \
             --exclude '.git*' \
             --exclude 'build' \
             --exclude 'Tools' \
             --exclude 'scripts' \
+            --exclude 'Sidecar' \
             --exclude '*.md' \
             --exclude 'changelog.md' \
             --exclude 'fresh-guild-data.lua' \
             ./ "$staging/"
```

The step already ends with `unzip -l "$zip_name"`, so the next release run will
show you whether it took.
