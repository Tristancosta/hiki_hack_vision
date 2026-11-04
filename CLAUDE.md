# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview
HikiHackVision is a Windows console app (C# / .NET 10, UI text in Portuguese) with quick utilities for videos exported from Hikvision cameras/NVRs:
1. **Convert** Hikvision `.mp4` files to standard MP4, written to `<root>/convertidos/`.
2. **Cut** videos so that only footage between 06:00 and 18:00 is kept, written to `<root>/cortados/` under the original file name. A file that yields several segments (for example, one that runs across the night) gets `_parte1`, `_parte2`, … suffixes. Files that fall completely outside the window are discarded.
3. **Normalize days** (`Normatizar dias`): splits the footage into 15-minute clock periods within 06:00–18:00 and writes each period to `<root>/normatizados/` as `ddMMhhmm_x.mp4`. `ddMM` is the date, `hhmm` the period start (0600, 0615, …, 1745) and `x` the day's ordinal across the run (1 for the earliest date, 2 for the next, …). Pieces from different files that fall in the same period are concatenated in clock order.
4. **Full process** (`Processo completo`): runs 1 → 2 → 3 on a source folder, each step reading the previous step's output. Only the final result is kept, in `<root>/prontos/`. The intermediate videos are temporary.

## Commands
```
dotnet build
dotnet run --project src/HikiHackVision
dotnet publish src/HikiHackVision -c Release -o publish
powershell -ExecutionPolicy Bypass -File installer/build.ps1   # → dist/HikiHackVision-Setup-<version>.exe
```
- The installer needs Inno Setup 6 (`winget install JRSoftware.InnoSetup`). Its version comes from `<Version>` in the csproj; bump it before generating a new release.
- There are no unit tests, by the user's choice. Verify changes by running the app against sample videos.
- To generate a sample, draw a clock in the top-left corner: `tools/ffmpeg -f lavfi -i testsrc=size=1280x720:rate=15:duration=180 -vf "drawtext=fontfile='C\:/Windows/Fonts/arial.ttf':text='%{pts\:gmtime\:<unix epoch>\:%m-%d-%Y %a %T}':x=16:y=16:fontsize=40:fontcolor=white:borderw=2:bordercolor=black" -c:v libx264 -g 15 out.mp4`. Use `%T`, because escaped colons inside drawtext strftime don't parse.
- Set `HIKI_OCR_DEBUG=1` to print every OSD reading (offset → date/time and match distance).
- The menu reads stdin, so it can be scripted: `printf '2\n<folder>\n0\n' | dotnet run --project src/HikiHackVision`.

## Architecture
- **External tools, all under `<root>/tools/` (gitignored):**
  - `FfmpegLocator` looks for `tools/ffmpeg.exe` and `tools/ffprobe.exe`, then checks PATH. If neither has them, it downloads the gyan.dev essentials zip.
  - All ffmpeg and ffprobe calls go through `FfmpegTools`, which wraps `ProcessRunner` using `ArgumentList` (no shell quoting). Frames for OSD reading come back as raw gray bytes (`ExtractGrayCornerAsync`, `-f rawvideo`), not as image files.
  - There are no NuGet dependencies.
- **Root folder:** `ProjectPaths.FindRoot` walks up from the exe's folder to the folder containing `HikiHackVision.sln*`. If there isn't one, it falls back to the exe's folder. The `tools/`, `convertidos/`, `cortados/`, `normatizados/` and `prontos/` folders are created relative to this root.
- **Feature entry points:** each feature's `RunAsync` only asks for the input folder and passes `<root>/<output>` to its `ConvertFolderAsync` / `CutFolderAsync` / `NormalizeFolderAsync(folder, outDir)`. Those methods take any input/output folder pair, which is what the full process uses.
- **Conversion (`VideoConverter`):** `FfmpegTools.ProbeAsync` picks the path. Empty (pre-allocated) blocks are skipped by `ListVideos`, and outputs that already exist are skipped too.
  - **NVR blocks (`hivXXXXX.mp4`):** these are really MPEG Program Streams (format `mpeg`) carrying H.265 or H.264. A direct remux produces a broken timeline, so the converter does two things. It first extracts the raw elementary stream (`-f hevc`/`h264`) into `%TEMP%/hiki_convert`. It then remuxes that into MP4 with `-r <fps>` and `-c copy`. Audio is dropped.
    - The fps comes from `MeasureFrameRateAsync`: the median of packet intervals under 0.5s. NVR blocks mix 0.1s frames (motion) with frames every 1–2s (no motion), and the header's `r_frame_rate` matches neither (seen 1 and 30 for 10 fps cameras). The probed `r_frame_rate` is only a fallback (then 10).
    - NVR blocks often end in a truncated packet. Extracting from the MP4 (`hevc_mp4toannexb`) then exits non-zero although everything else was written, so `SplitByResolutionAsync` accepts a failed extraction when the raw stream has content and lets the remux validate the part (this used to drop a whole block, hiv00013).
    - A block can switch resolution mid-file (seen: 2560x1440 → 1280x720). A single MP4 keeps the first resolution in its header, which breaks cuts and puts the OSD layout in the wrong place. `GetResolutionRunsAsync` probes keyframes only (`-skip_frame nokey`), and if there is more than one resolution, `SplitByResolutionAsync` writes `name_1.mp4`, `name_2.mp4`, … (each re-extracted as a raw stream so its header is correct) and deletes the mixed MP4.
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
     - There are two font variants (thin original and "full", with 2-pixel strokes and complete bars), and one file can switch between them along with the date format (`AAAA-MM-DD` or `MM-DD-AAAA`, both accepted). `PickFont` decodes with each variant separately and picks the one that fits all date and time cells clearly better. Never mix variants within one reading: a thin "3" is almost contained in a full "8", and that turned 08:01 into 03:01.
     - `ContrastScores` uses a per-cell bright/dark threshold, clamped to 110..150, because compression dims thin bars and lightens the outline over foliage. It also penalizes stroke-like pixels (bright with dark on both sides) that fall outside the template, but not in the 1-pixel border around the cell: with the full font over white sky, the sky strip between two neighbouring outlines looks like a stroke there.
     - A separator must look more like itself than like the other separator, otherwise `10-21-2026` reads as `10:21:20`. This applies only while `Calibrate` scans for the time. With the layout known (`knownPosition`), the time's `:` only has to beat the digits: with the full font over white sky the sky strip left of the `:` makes it score closer to `-`, which once made a whole stretch unreadable (CAM03, 26/10 09:00–10:15 vanished from `prontos`).
     - `ReadWithLayout` re-aligns the date block on its own (`DateShift`: one shift in ±3px, step 0.5, for all 10 date cells, picked by best fit). The grid is fitted on the time, and a pitch error of a few hundredths of a pixel becomes ~1.5px 15 cells to the left: with pitch 16.025 on a 720p NVR the date digits scored ~13 and "26" failed the margin against "20", so 26–28/09 had no date and vanished from `prontos`. Shifted, the date fit went from 106 to 36.
     - Every digit is constrained by position: hour tens 0–2, minute/second tens 0–5, month tens 0–1, day tens 0–3. It is accepted only when the distance is small and the margin to the second-best allowed digit is large.
     - **Do not go back to generic OCR.** `Windows.Media.Ocr` drops strings like `05:59:01`. Tesseract confidently misreads digits (18 → 16) on bright backgrounds.
  2. **`OsdTimestampReader`** is one instance per video. It caches the `OsdLayout` from the first successful calibration and tries `ReadWithLayout` first. `ReadStartAsync` requires two readings 1s apart to agree (non-negative delta of at most 120s).
  3. **`ClockReference`** gives clock-elapsed seconds since the first reading, using the OSD date when one was read, so multi-day files work.
     - `CutWindowCalculator` intersects [start, start + span] with 06:00–18:00 for each day covered. A file can yield several `_parteN` segments.
  4. **`VideoCutter.VideoClock`** handles NVR recordings, which are event-based: the clock is **not** 1:1 with file time. It jumps between events and can run 2–50× faster than the file.
     - The only assumption is that the clock never goes backwards.
    - `ReadClockSpanAsync` samples the clock across the whole file (~every 4 min of file, 8–60 samples) and accumulates the advance between neighbouring samples. An undated reading only gives the time of day, so it is resolved against the previous sample: a time "earlier" than the previous one means midnight was crossed. Dated readings are absolute and override undated samples that ran ahead. Never derive the span from the end reading alone: with the date unreadable there, a file running 24/09 10:18 → 25/09 12:46 was taken as 24/09 10:18–12:46, and the whole night leaked into `cortados`.
    - Binary-search probes are resolved the same way, against the scan sample before the probe (with 1h slack because the scan doesn't seek exactly), and rejected if outside the neighbouring samples' range.
    - `StartDateTime` comes from the first reading, or, if that has no date, from the first dated scan sample minus the clock elapsed until it.
     - Every cut boundary is found by binary search over file offset down to 0.02s (below one frame), reading the exact frame (`ReadAtAsync(exact: true)`).
     - The result `hi` is the first position whose frames are already ≥ the boundary. That makes it an exact `-to` for segment ends.
  5. Cuts use `-c copy`. Segment starts snap to the **first keyframe ≥ the boundary** (`GetKeyframesAsync` parses packet flags), so no footage from before 06:00, or from an event before a gap, leaks in. `-ss` is passed as keyframe + 0.01 because input seeking lands on the keyframe at or before it.
- **Day normalization (`Features/Normalize/DayNormalizer`):** same OSD pipeline as the cut, reusing `VideoClock` (binary search) and `ClipCutter` (keyframe snap + `-c copy` cut), both shared with `VideoCutter`. `CalculateSlots` lists the 15-minute periods covered by [start, start + span] inside 06:00–18:00. Each file is cut per period into `normatizados/_tmp/`, then periods are assembled: one piece is moved, several are joined with the ffmpeg concat demuxer (`-f concat -safe 0 -c copy`). Period starts snap to the first keyframe ≥ the boundary, so a few seconds of clock may be missing at the start of each period; ends are exact. If the date is unreadable at the start (bright background), it uses `VideoClock.StartDateTime`, derived from the scan. Existing outputs with the same name are overwritten, leftovers from earlier runs are not deleted.
- **Full process (`Features/Pipeline/FullPipeline`):** calls the three folder methods in sequence. The intermediate outputs go to `%TEMP%/hiki_completo/{convertidos,cortados}`. That folder is cleared at the start (leftovers from an interrupted run) and deleted at the end, and `convertidos` is deleted as soon as the cut finishes to free disk space. The normalization writes straight to `<root>/prontos/`. If a step yields no videos, the process stops.
- **Installer (`installer/`):**
  - `build.ps1` publishes a self-contained single-file `win-x64` exe into `publish/win-x64` (target PCs need no .NET), then runs `ISCC` on `HikiHackVision.iss`. If `tools/ffmpeg.exe` and `tools/ffprobe.exe` exist, they are bundled; otherwise the app downloads them on first run.
  - The setup installs per-user, with no admin prompt, to `C:\HikiHackVision` by default; the user can pick another folder. With no `.sln` above it, that folder becomes the app's root, so all output folders live there. This is why the default avoids Program Files (not writable) and Documents (often synced to OneDrive).
  - Uninstalling removes only installed files, so generated videos stay. Keep the `AppId` GUID fixed, so that a newer setup upgrades the existing install in place.
  - Both scripts are saved as UTF-8 **with BOM**: Windows PowerShell 5.1 reads BOM-less files as ANSI.
