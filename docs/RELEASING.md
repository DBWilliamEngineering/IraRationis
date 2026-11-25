# Release engineering

The Windows Native AOT workflow uses the SDK baseline in `global.json` and compiles the existing native bridge with the x64 MSVC developer environment. CI is required to pass before a draft release can be prepared. No guest binaries or third-party commercial assets are necessary for validation.

## CI diagnosis

The November 25, 2026 [run 45712840752](https://github.com/DBWilliamEngineering/IraRationis/actions/runs/45712840752) built and published successfully. The native entry test reported the expected guest exit code `42`, printed a success message, then failed at the workflow shell boundary. GitHub's PowerShell shell propagates native exit status. The three native probe scripts now explicitly return `0` only after the original checks and cleanup succeed; their exception paths remain failures. This is a targeted correction, not proof that later tests pass.

If the next run fails, inspect the first failing step and its complete log before changing test expectations. Do not use `continue-on-error` to get a green release.

The later [run 45715465397](https://github.com/DBWilliamEngineering/IraRationis/actions/runs/45715465397) failed at restore with MSB4126 (Debug|x64). R37 normalizes the MSBuild Platform environment after the MSVC setup action and explicitly builds the solution as Release|Any CPU. Native AOT publication remains win-x64.

## Local acceptance

Run from an x64 Visual Studio Developer PowerShell:

```powershell
dotnet restore IraRationis.slnx -p:Configuration=Release -p:Platform="Any CPU"
dotnet build IraRationis.slnx -c Release --no-restore -p:Platform="Any CPU"
dotnet run --project IraRationis.Runtime.Tests -c Release --no-build
dotnet run --project tests/storage-contract/StorageContract.csproj -c Release --no-build
dotnet publish IraRationis.Cli -c Release -r win-x64 -o artifacts/native-r37

$irx = (Resolve-Path artifacts/native-r37/irx.exe).Path
& $irx memory-probe --self-test
& $irx veh-probe
python tests/smoke.py $irx
& ./tests/native/smoke.ps1 $irx
& ./tests/native/input-memory.ps1 $irx
& ./tests/native/storage.ps1 $irx
```

Check the exit status after each command. The native probe scripts translate the expected guest sentinel into exit code zero. Test media separately with an explicitly selected trusted FFmpeg executable:

```powershell
$ffmpeg = (Get-Command ffmpeg.exe -ErrorAction Stop).Source
$fixture = (Resolve-Path tests/media-baseline/fixture.mp4).Path
$intro = (Resolve-Path tests/media-baseline/vindex-intro.mp4).Path
dotnet run --project tests/media-baseline/MediaBaseline.csproj -c Release --no-build -- $ffmpeg $fixture
& $irx media-probe $ffmpeg $intro --stream
```

Expect 60 decoded frames from the original Vindex clip; review presented/skipped counts, visual playback, sound and termination. Audio is approximately two seconds (decoder padding may slightly increase the reported duration). Repeat with `--play` to compare R35's batch playback. The streaming path is an independent local player; guest media and account workflows are not integrated.

## Native media acceptance

Run `irx.exe media-probe --mf <local MP4> --play` against the original Vindex fixture. This uses Windows Media Foundation instead of FFmpeg. Use the manual workflow input `test_native_media` to run the noninteractive native-decoder check on a runner with appropriate codecs. This optional check is not required for tag publication, and the GUI/audio acceptance remains local.

## Ready-to-run distribution

Successful CI publishes both the release ZIP artifact and `irx-win-x64-unpacked`. The latter contains `irx.exe`, `irx-native.dll`, other published dependencies, license files, `QUICKSTART.txt`, and the original Vindex test clip. Download the whole package, extract it, and keep the executable and native DLL together. The release ZIP provides the same layout. No SDK or compiler is required on the end-user machine.

## Review and publish

1. Confirm the repository visibility and access policy. A private repository is not a substitute for correct software licensing.
2. Inspect `git status --short` and `git ls-files`. Do not commit commercial game files, executable images, extracted shaders, private logs, credentials, caches, or generated build directories. Confirm that the original test fixtures and branding assets are authorized for the intended audience.
3. Review GPL corresponding-source obligations and all third-party package licenses. Include applicable notices in the distribution before publishing; do not assume that a source archive alone satisfies every dependency's terms. Retain upstream attribution. The draft is not a legal compliance certification.
4. Commit and push the intended source changes. Inspect the Actions result for that exact commit. Use a new `v*` tag only after validating the commit. Tags do not change the assembly version automatically.
5. Push the annotated tag (e.g. `v0.1.0-r37`). The workflow tests the tagged source again, creates a binary ZIP, packages the exact tracked source using `git archive`, and writes a SHA-256 checksum manifest.
6. Inspect the draft prerelease and download the artifacts. Verify their checksums and contents. Confirm runtime binaries, license texts, third-party notices, corresponding source and release notes. Publish manually only after review.

The ordinary CI job is read-only. The draft-release job alone requests `contents: write` and runs only on successful tag pushes. If a draft for the same tag already exists, inspect it rather than automatically deleting or overwriting it. A rerun may fail with an existing release; use a new tag for a revised commit.

No runtime or guest-compatibility claim follows from a successful build. GPU output, physical controller behavior, media synchronization and native audio remain separate functional acceptance criteria.
