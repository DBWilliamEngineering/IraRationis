# Media baseline

This independent regression executable verifies the NV12 color conversion, PCM mixing and WAVE encoding primitives, then decodes an original synthetic H.264/AAC fixture using a trusted external FFmpeg executable. It shares `MediaClipInspector` with the main `irx media-probe` command. It never runs native guest code.

```powershell
$ffmpeg = (Get-Command ffmpeg.exe -ErrorAction Stop).Source
$clip = (Resolve-Path .\tests\media-baseline\fixture.mp4).Path
dotnet run --project tests/media-baseline/MediaBaseline.csproj -c Release -- $ffmpeg $clip --play
```

Omit `--play` for decode/inspection only. Output is written into a unique `artifacts/media-baseline/` subdirectory. The original fixture has 60 frames at 30 FPS, two seconds of 48 kHz stereo audio, and changing video content. Expected counts: 60 distinct frames and 192,000 interleaved audio samples. The test rejects stationary or silent output; the main CLI accepts legitimate static or silent media.

`LocalMediaDecoder` is a bounded local-file adapter (external process), not a native console decoder. Audio playback uses WinMM synchronously and video is exported as BMP previews, not played live. Windows build and execution still require validation.
