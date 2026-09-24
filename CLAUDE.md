# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview
HikiHackVision is a Windows console app (C# / .NET 10, UI text in Portuguese) with quick utilities for videos exported from Hikvision cameras/NVRs:
1. **Convert** Hikvision `.mp4` files to standard MP4, written to `<root>/convertidos/`.
2. **Cut** videos so that only footage between 06:00 and 18:00 is kept, written to `<root>/cortados/` under the original file name. A file that yields several segments (for example, one that runs across the night) gets `_parte1`, `_parte2`, … suffixes. Files that fall completely outside the window are discarded.

## Commands
```
dotnet build
dotnet run --project src/HikiHackVision
dotnet publish src/HikiHackVision -c Release -o publish
```
- There are no unit tests, by the user's choice. Verify changes by running the app against sample videos.
- To generate a sample, draw a clock in the top-left corner: `tools/ffmpeg -f lavfi -i testsrc=size=1280x720:rate=15:duration=180 -vf "drawtext=fontfile='C\:/Windows/Fonts/arial.ttf':text='%{pts\:gmtime\:<unix epoch>\:%m-%d-%Y %a %T}':x=16:y=16:fontsize=40:fontcolor=white:borderw=2:bordercolor=black" -c:v libx264 -g 15 out.mp4`. Use `%T`, because escaped colons inside drawtext strftime don't parse.
- Set `HIKI_OCR_DEBUG=1` to print every OSD reading (offset → date/time and match distance).
- The menu reads stdin, so it can be scripted: `printf '2\n<folder>\n0\n' | dotnet run --project src/HikiHackVision`.

## Architecture
- **External tools, all under `<root>/tools/` (gitignored):**
  - `FfmpegLocator` looks for `tools/ffmpeg.exe` and `tools/ffprobe.exe`, then checks PATH. If neither has them, it downloads the gyan.dev essentials zip.
  - All ffmpeg and ffprobe calls go through `FfmpegTools`, which wraps `ProcessRunner` using `ArgumentList` (no shell quoting). Frames for OSD reading come back as raw gray bytes (`ExtractGrayCornerAsync`, `-f rawvideo`), not as image files.
  - There are no NuGet dependencies.
- **Root folder:** `ProjectPaths.FindRoot` walks up from the exe's folder to the folder containing `HikiHackVision.sln*`. If there isn't one, it falls back to the exe's folder. The `tools/`, `convertidos/` and `cortados/` folders are created relative to this root.
- **Conversion (`VideoConverter`):** `FfmpegTools.ProbeAsync` picks the path. Empty (pre-allocated) blocks are skipped by `ListVideos`, and outputs that already exist are skipped too.
  - **NVR blocks (`hivXXXXX.mp4`):** these are really MPEG Program Streams (format `mpeg`) carrying H.265 or H.264. A direct remux produces a broken timeline, so the converter does two things. It first extracts the raw elementary stream (`-f hevc`/`h264`) into `%TEMP%/hiki_convert`. It then remuxes that into MP4 with `-r <fps>` (probed `r_frame_rate`, falling back to 10) and `-c copy`. Audio is dropped.
  - **Real MP4s:** stream-copy the video and re-encode the audio to AAC. Hikvision audio is G.711, which MP4 doesn't accept. If that fails, fall back to libx264.
  - To make a PS sample, use the clock command below with `-c:v libx265 -c:a mp2 -f mpeg hiv00001.mp4`.
- **Cutting pipeline (`Features/Cut`):** the wall-clock time exists only in the burned-in OSD (top-left, `AAAA-MM-DD Ddd HH:MM:SS`, a white bitmap font with a black outline). File names and metadata are not used. The requirement is strict: nothing recorded outside 06:00–18:00 may end up in `cortados/`.
  1. **`OsdGlyphReader`** uses template matching, not OCR. The font's cells are 7×10 "font pixels" (plus 1 column of spacing), so pitch ≈ 0.8 × glyph height. The 7×10 templates in the file were extracted from real 720p NVR footage.
     - `Calibrate` locates the text blind:
       - Stroke mask: bright pixels with dark outline pixels on both sides.
       - Connected components, grouped into a text row.
       - Pitch and origin fitted by phase search plus linear regression. Pitch errors of 0.1px accumulate over 20+ cells, so the fit has to be precise.
       - Each cell is Hamming-matched against the templates, then the reader searches for the `dd:dd:dd` pattern.
     - `ReadWithLayout` reuses the calibrated grid. It scores each template by "stroke pixels bright + outline pixels dark" and ignores the background, which lets it read frames against white sky where the mask approach fails.
     - Every digit is constrained by position: hour tens 0–2, minute/second tens 0–5, month tens 0–1, day tens 0–3. It is accepted only when the distance is small and the margin to the second-best allowed digit is large.
     - **Do not go back to generic OCR.** `Windows.Media.Ocr` drops strings like `05:59:01`. Tesseract confidently misreads digits (18 → 16) on bright backgrounds.
  2. **`OsdTimestampReader`** is one instance per video. It caches the `OsdLayout` from the first successful calibration and tries `ReadWithLayout` first. `ReadStartAsync` requires two readings 1s apart to agree (non-negative delta of at most 120s).
  3. **`ClockReference`** gives clock-elapsed seconds since the first reading, using the OSD date when one was read, so multi-day files work.
     - `CutWindowCalculator` intersects [start, start + span] with 06:00–18:00 for each day covered. A file can yield several `_parteN` segments.
  4. **`VideoCutter.VideoClock`** handles NVR recordings, which are event-based: the clock is **not** 1:1 with file time. It jumps between events and can run 2–50× faster than the file.
     - The only assumption is that the clock never goes backwards.
     - Every cut boundary is found by binary search over file offset down to 0.02s (below one frame), reading the exact frame (`ReadAtAsync(exact: true)`).
     - The result `hi` is the first position whose frames are already ≥ the boundary. That makes it an exact `-to` for segment ends.
  5. Cuts use `-c copy`. Segment starts snap to the **first keyframe ≥ the boundary** (`GetKeyframesAsync` parses packet flags), so no footage from before 06:00, or from an event before a gap, leaks in. `-ss` is passed as keyframe + 0.01 because input seeking lands on the keyframe at or before it.
