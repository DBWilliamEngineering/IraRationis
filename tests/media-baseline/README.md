# Media primitive and decoding contracts

This independent executable checks NV12 conversion, PCM mixing, and WAVE serialization, then decodes an original synthetic H.264/AAC fixture using a trusted FFmpeg executable. It shares `MediaClipInspector` with `irx media-probe` and does not execute a guest image.

From the repository root after building the solution:

```powershell
$ffmpeg = (Get-Command ffmpeg.exe -ErrorAction Stop).Source
$clip = (Resolve-Path tests/media-baseline/fixture.mp4).Path
dotnet run --project tests/media-baseline/MediaBaseline.csproj -c Release --no-build -- $ffmpeg $clip
```

Add `--play` after `$clip` for synchronous Windows audio output. This test exports video previews; it does not open a live video window. For audiovisual playback, use the [CLI media commands](../../docs/RELEASING.md#media-and-gpu-acceptance).

The original fixture contains 60 frames at 30 FPS and two seconds of 48 kHz stereo audio. Expected observations are 60 distinct frame hashes and 192,000 interleaved PCM samples. The test requires changing video and non-silent audio; the general CLI accepts static or silent media. Inspection output goes to a unique directory under `artifacts/media-baseline`.

`LocalMediaDecoder` is a bounded local-file adapter around an external decoder. These checks establish fixture behavior, not a native console movie ABI or game startup. The project is included in the solution and CI invokes its headless decoding path. A build result alone does not establish that this test executed successfully.
