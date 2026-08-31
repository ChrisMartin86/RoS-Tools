# Workflow edits — both applied

Historic note. This file described two changes to `.github/workflows/` that an
earlier session could not make itself. **Both are now in the repo:**

1. `.github/workflows/sidecar.yml` exists — builds and tests the sidecar on every
   push touching `Sidecar/`, and publishes a self-contained win-x64 exe on a
   `sidecar-vX.Y.Z` tag. The Azure Trusted Signing step is present but commented
   out, between publish and release.
2. `release.yml`'s **Build the zip** step now has `--exclude 'Sidecar' \` in its
   rsync list, alongside `Tools` and `scripts`. Without it about forty C# files
   shipped inside the CurseForge zip.

Neither had ever been applied, so until now the sidecar had no CI at all and the
next addon release would have carried the whole `Sidecar/` tree.

The original blocker was real but narrower than it looked: `device_commit_files`
refuses to write under `.github/workflows/`, and `device_list_dir` does not show
its contents. A shell on the machine reads and writes it normally, and
`git show HEAD:.github/workflows/release.yml` reads it regardless — so check with
one of those before concluding a workflow file is missing.
