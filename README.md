<div align="center">

# Photon

### A modern, native Windows 11 photo & media experience.

A fast, GPU-accelerated photo viewer, editor, and library manager built on the **Windows App SDK (WinUI 3)** and **.NET 8**. Photon pairs a Google-Photos-style gallery with a non-destructive editor, on-device AI subject detection, and an encrypted Secure Folder — all rendered on a SkiaSharp surface and packaged as a single-project MSIX app.

[Features](#-features) · [Screenshots](#-screenshots) · [Getting Started](#-getting-started) · [Architecture](#-architecture) · [Configuration](#-configuration) · [Roadmap](#-roadmap) · [Contributing](#-contributing)

</div>

---

## ✨ Features

### Gallery & Library
- **Justified Google-Photos-style layout** — greedy row-packing algorithm that targets a fixed row height and exactly fills the container width; debounced relayout on window resize.
- **Live library index** — `FileSystemWatcher` per folder with 150 ms debounce keeps the index in sync with the disk; scans run in parallel (`MaxDegreeOfParallelism = 8`).
- **Grouping** by Date (default, "MMMM yyyy"), Folder, Format, or None.
- **Sorting** — Date ↓ / Date ↑ / Name ↑ / Size ↓ — persisted across sessions.
- **Search** by filename (case-insensitive `Contains`).
- **Drag-and-drop** a folder to add it to the library, or drop files to copy them into the first library folder.
- **Per-card context menu** — Open, Reveal in File Explorer, Copy path, Add to album.

### Viewer
- **GPU-accelerated SkiaSharp canvas** with zoom (10%–3200%) and pan.
  - Ctrl+Wheel zoom toward cursor · drag to pan · double-click toggles fit ↔ 100%.
  - Keyboard: arrows pan, `+`/`-` zoom, `F` fit, `1` 100%.
  - Pixel-perfect filtering above 2× zoom; high-quality Lanczos below.
- **Three media modes** — still image, animated GIF (frame-accurate timer), and video (Windows `MediaPlayerElement`).
- **Filmstrip** with auto-scroll-to-current.
- **EXIF panel** — camera make/model, lens, focal length, aperture, ISO, exposure, GPS, plus a curated set of secondary tags (flash, white balance, metering, exposure program, color space, scene type, digital zoom).
- **Info strip** — dimensions, file size, format, modified date, full path.
- **Toolbar** — Edit, Favorite, Save As, Share, Slideshow, Copy image, Copy path, Properties.
- **Chrome toggle** — tap canvas or press `Space` to hide/show all UI.
- **Keyboard navigation** — `←` / `→` move between siblings, `Esc` back, `I` toggles EXIF.

### Editor (non-destructive)
- **13 adjustment sliders** — Exposure, Brightness, Contrast, Highlights, Shadows, Saturation, Vibrance, Warmth, Tint, Sharpness, Clarity, Vignette, Grain.
  - Implemented as a fused SkiaSharp 4×5 color matrix (exposure + contrast + brightness + saturation/vibrance + warmth + tint), 256-entry LUT (highlights/shadows, structurally color-cast-free), 3×3 unsharp-mask convolution, and separate vignette/grain passes.
- **18 filter presets** — Original, Vivid, Bright, Dramatic, High Key, Low Key, Warm, Cool, Sunset, Moonlight, Fade, Matte, Vintage, Chrome, Film, B&W, Noir, Cinematic (teal-orange grade).
  - Each preset is rendered against a 72-px downsample for the filter chooser.
- **Crop & transform** — 7 aspect presets (Free / 1:1 / 4:3 / 3:2 / 16:9 / 3:4 / 2:3), interactive 8-handle overlay with rule-of-thirds guides, free rotation −45°..+45°, 90° rotate left/right, flip H/V.
- **Save As** — JPEG / PNG / WebP, with quality and metadata-strip options.
- **Compress to target size** — binary-searches JPEG quality (30 → user quality) until output ≤ target KB.

### Batch Operations
- **Rename** with `{n}` (sequence number, configurable start) and `{date}` placeholders.
- **Convert** to JPEG / PNG / WebP, with quality, max dimensions, and strip-metadata options.
- **Resize** by percentage (1–200%) or by max dimension (px).
- Progress bar + cancel button.

### Albums & Favorites
- **Albums** — create, edit name/description, set cover photo, delete (cascading).
- **Favorites** — starred items persisted in SQLite, dedicated gallery view with gold ★ badges.

### Secure Folder (Privacy Vault)
A first-class, password-protected photo vault.

- **AES-256-CBC** encryption (PKCS7 padding) per file.
- **PBKDF2** key derivation — 100,000 iterations of HMAC-SHA256 over a 32-byte random salt.
- **Windows Hello** biometric unlock — stores the password in the OS `PasswordVault` and retrieves it on fingerprint/face verification.
- **Screen-capture protection** — calls Win32 `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`, so the window appears **black in screenshots and screen recordings**.
- **In-memory-only plaintext** — vault items are decrypted to a `MemoryStream` and rendered via `BitmapImage`; nothing is ever written to disk unencrypted.
- **File format on disk** — random GUID filenames with original-name header, so the vault can be enumerated without decrypting file bodies.
- **Operations** — SetPassword, Unlock, ChangePassword, Lock, Import, Export, Remove, Reset Vault.

### On-Device AI Subject Detection
- **YOLOv8-seg ONNX model** (`Microsoft.ML.OnnxRuntime`), 640×640 RGB input, NCHW, normalized 0..1.
- **80 COCO classes** — person, bicycle, car, motorcycle, airplane, bus, train, truck, boat, …, toothbrush.
- **Per-class Non-Maximum Suppression** (IoU = 0.5, confidence ≥ 0.45).
- **Colored bounding-box overlay** with `Label  NN%` pills; tap-to-select a subject.
- **Degrades gracefully** — if no model file is present at `%LocalAppData%\Photon\models\yolov8n-seg.onnx`, detection silently returns an empty list (logged once).
- 100% on-device; nothing is ever sent to a server.

### Format Support
| Category | Extensions |
|---|---|
| Common images | `jpg` `jpeg` `png` `bmp` `tif` `tiff` `gif` `webp` `ico` |
| Modern images | `heic` `heif` `avif` `jxl` `psd` |
| Camera RAW | `cr2` `cr3` `nef` `arw` `dng` `orf` `rw2` `raf` `pef` `sr2` |
| Animated | `gif` (frame-accurate) |
| Videos | `mp4` `mov` `mkv` `avi` `webm` `m4v` `3gp` `wmv` `flv` |
| Export targets | `jpg` `jpeg` `png` `webp` `bmp` |

All 23 image extensions are registered as Windows file-type associations — double-click any of them in File Explorer to open it in Photon.

### Thumbnails & Performance
- **Disk-cached thumbnails** — cache key is `SHA256(path + "|" + lastWriteTicks)`, so edited files invalidate instantly.
- **Lanczos3** downscale, JPEG quality 82.
- **4-wide concurrency limiter** + in-flight deduplication via `ConcurrentDictionary`.
- **Configurable cache size** (default 4 GB) with oldest-first eviction.
- **Custom cache location** — relocate the thumbnail cache to any drive.
- **300-bitmap in-memory LRU** for the live gallery.
- **Background thumbnail generation**, **GPU acceleration**, and **adjacent-image prefetch** toggles.

### Shell Integration
- **MSIX-packaged** with `runFullTrust` + `systemAIModels` capabilities.
- **Windows Share Sheet** integration — share one or more files with a 256-px preview bitmap.
- **Clipboard** — copy bitmap + path + `StorageFile` in one operation (paste works in text fields, image editors, and File Explorer).
- **File activation** — opening a file from Explorer routes directly to the viewer.

### Full-Screen Slideshow
- Cycles through the supplied sibling list at the configured interval (≥ 2 s, default 5 s).
- 400 ms fade transitions via `DoubleAnimation` on `Opacity`.
- Auto-hiding control bar (3 s of pointer inactivity).
- Keyboard: `Esc` exits, `←` / `→` / `Space` navigate, `P` toggles play/pause.

---

## 📸 Screenshots

> Place screenshots in a `docs/screenshots/` folder at the repo root and reference them below. Suggested captures:
>
> | File | Suggested subject |
> |---|---|
> | `docs/screenshots/gallery.png` | The justified gallery with date grouping and the navigation pane open. |
> | `docs/screenshots/viewer.png` | The full-screen viewer with EXIF panel and filmstrip visible. |
> | `docs/screenshots/editor-adjust.png` | The editor's Adjust tab with a few sliders moved off neutral. |
> | `docs/screenshots/editor-filters.png` | The editor's Filters tab showing all 18 preset thumbnails. |
> | `docs/screenshots/editor-crop.png` | The crop overlay with rule-of-thirds guides and 8 handles. |
> | `docs/screenshots/secure-folder.png` | The Secure Folder lock screen (no sensitive content visible). |
> | `docs/screenshots/ai-overlay.png` | The viewer with AI subject-detection bounding boxes. |
>
> Embed them like this once the PNGs exist:
>
> ```markdown
> ### Gallery
> ![Photon gallery](docs/screenshots/gallery.png)
> ```

---

## 🚀 Getting Started

### Prerequisites

- **Windows 10 version 1809 (build 17763)** or later — Windows 11 recommended for the full Fluent/acrylic experience.
- **.NET 8 SDK** — [download](https://dotnet.microsoft.com/download/dotnet/8.0).
- **Visual Studio 2022 17.10+** with the following workloads:
  - `.NET desktop development`
  - `Universal Windows Platform development`
  - The **Windows App SDK C# Templates** individual component.
- (Optional) **Single-project MSIX Packaging Tools** VS extension for one-click packaging.

### Build from source

```bash
git clone https://github.com/<your-user>/Photon.git
cd Photon
dotnet restore
dotnet build -c Release
```

To run a debug build directly:

```bash
dotnet run -c Debug
```

> **Platform note:** the project defaults to `x64` so `dotnet build` works without choosing a solution platform. To target ARM64 or x86, pass `-p:Platform=arm64` or `-p:Platform=x86` respectively.

### Package as MSIX

1. Open `Photon.sln` in Visual Studio 2022.
2. Set configuration to `Release | x64`.
3. Right-click the `Photon` project → **Package and Publish** → **Create App Packages**.
4. Choose sideload or store upload; Visual Studio will produce the `.msix` and signing certificate.

For command-line packaging, see the [Windows App SDK packaging guide](https://learn.microsoft.com/windows/apps/windows-app-sdk/manage-packaging-and-deployment).

### Install the MSIX

```powershell
# Trust the signing cert (developer mode)
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=princ" -CertStoreLocation Cert:\CurrentUser\My
Export-Certificate -Cert $cert -FilePath Photon.cer
Import-Certificate -FilePath Photon.cer -CertStoreLocation Cert:\CurrentUser\Root

# Install
Add-AppxPackage -Path Photon_x64.msix
```

Once installed, Photon will appear in the Start menu and will be the default handler for the 23 registered image extensions.

---

## 🏗 Architecture

Photon follows a thin **MVVM-ish** layering: XAML pages own their layout, code-behind wires up events and calls into services resolved through `Microsoft.Extensions.DependencyInjection`.

```
Photon/
├── App.xaml / App.xaml.cs         # Entry point, DI container, file activation
├── MainWindow.xaml / .cs          # Shell: title bar + NavigationView + Frame
├── Photon.csproj                  # WinUI 3 / .NET 8 / MSIX project
├── Package.appxmanifest           # Identity, capabilities, file-type associations
│
├── Core/                          # Domain services (no UI)
│   ├── AppPaths.cs                # %LocalAppData%\Photon\ path layout
│   ├── AppSettings.cs             # JSON-serialized user preferences
│   ├── FormatRegistry.cs          # Single source of truth for read/write formats
│   ├── LibraryDatabase.cs         # SQLite: favorites + albums (FK-cascading)
│   ├── MediaLibrary.cs            # Live in-memory index + FileSystemWatchers
│   ├── MetadataReader.cs          # EXIF/IPTC/XMP via MetadataExtractor
│   ├── SecureVault.cs             # AES-256 + PBKDF2 encrypted photo vault
│   └── ThumbnailEngine.cs         # SHA256-keyed disk cache + Lanczos3 resize
│
├── Decode/                        # Pluggable image decoders
│   ├── IImageDecoder.cs           # Shared contract
│   ├── ImageSharpDecoder.cs       # jpg/png/bmp/tiff/gif/webp
│   ├── MagickDecoder.cs           # heic/avif/jxl/psd + camera RAW
│   ├── AnimatedGifDecoder.cs      # Frame-accurate GIF playback
│   └── DecoderFactory.cs          # Extension-based dispatch
│
├── Edit/                          # Non-destructive editing pipeline
│   ├── AdjustmentEngine.cs        # 13-axis color/tonal adjustments
│   ├── FilterPipeline.cs          # 18 named presets + custom matrices
│   ├── CropTool.cs                # Aspect math + overlay renderer
│   ├── ConversionPipeline.cs      # Single + batch format conversion
│   └── CompressionTool.cs         # Size-budget binary search
│
├── AI/
│   └── SubjectDetector.cs         # ONNX YOLOv8-seg inference (80 COCO classes)
│
├── Share/
│   └── ShareProvider.cs           # Windows Share Sheet + clipboard
│
├── Services/
│   └── ServiceRegistration.cs     # DI wiring (all singletons)
│
├── Models/
│   └── MediaItem.cs               # Immutable record: file + metadata + edit state
│
├── Controls/                      # Reusable custom XAML controls
│   ├── Filmstrip.xaml             # Horizontal thumbnail strip (viewer)
│   ├── ZoomCanvas.xaml            # GPU SkiaSharp zoom/pan surface
│   └── SubjectOverlay.xaml        # AI bounding-box renderer
│
└── Views/                         # One XAML page per navigation target
    ├── GalleryView.xaml           # Main justified gallery
    ├── FavoritesPage.xaml         # Starred items
    ├── VideosPage.xaml            # 16:9 video cards
    ├── AlbumsPage.xaml            # Album grid
    ├── AlbumContentPage.xaml      # Items in one album
    ├── SecureFolderPage.xaml      # Encrypted vault UI
    ├── SettingsPage.xaml          # All app settings
    ├── ViewerPage.xaml            # Full-screen viewer (image/GIF/video)
    ├── EditPage.xaml              # 4-tab non-destructive editor
    ├── SlideshowPage.xaml         # Full-screen slideshow
    ├── BatchOpsDialog.xaml        # Rename / convert / resize dialog
    ├── AdjustSliderRow.xaml       # Custom labeled-slider control
    ├── ViewerNavigationPayload.cs # (Current, Siblings) tuple
    └── ViewModels/
        └── GalleryViewModels.cs   # AlbumVm + helpers
```

### Dependency graph (high level)

```
App.OnLaunched
  └─ ServiceRegistration.Build() ─────────────────────────────┐
       ├─ AppSettings        ← settings.json                   │
       ├─ MetadataReader                                       │
       ├─ ThumbnailEngine    ── cache → AppPaths.ThumbCacheDir  │
       ├─ MediaLibrary       ── FileSystemWatcher → ItemsChanged│
       ├─ LibraryDatabase    ── SQLite → library.db             │
       ├─ SecureVault        ── AES-256 → vault.db + *.vault    │
       ├─ ConversionPipeline                                   │
       ├─ CompressionTool                                      │
       └─ SubjectDetector   ── ONNX → models/yolov8n-seg.onnx   │
                                                               │
  MainWindow ── Frame ──► Pages ──► App.Services ───────────────┘
```

### Key design choices

- **Immutable data model.** `MediaItem` is a `record` with no pixel payload — instances can flow across threads and be cached without bloating memory.
- **Pluggable decoders.** `IImageDecoder` is implemented by ImageSharp (common formats), Magick.NET (HEIC/AVIF/JXL/PSD/RAW), and a dedicated GIF animator; `DecoderFactory` dispatches by extension.
- **Non-destructive editing.** Sliders, filters, and crop are all stored as state; the pipeline recomposes the final bitmap on demand. The original file is never modified.
- **Service-oriented AI.** `SubjectDetector` is a singleton that lazily loads the ONNX session and degrades to a no-op when the model file is missing — so the rest of the app never has to think about whether AI is available.
- **Defense-in-depth Secure Folder.** Per-file random IVs, master key derived with 100k-iteration PBKDF2, plaintext never touches disk, screen-capture disabled at the Win32 layer, and biometric unlock routed through the OS `PasswordVault`.

---

## ⚙️ Configuration

All user settings live in `%LocalAppData%\Photon\settings.json` and are editable from the in-app **Settings** page.

| Setting | Default | Description |
|---|---|---|
| `LibraryFolders` | `[User Pictures folder]` | Folders scanned by `MediaLibrary`. |
| `IncludeSubfolders` | `true` | Recurse into subfolders during scan. |
| `AppTheme` | `System` | `System` / `Light` / `Dark`. |
| `ThumbnailSize` | `1` (120 px) | 0=80 · 1=120 · 2=180 · 3=240 px. |
| `GalleryGroupBy` | `Date` | `Date` / `Folder` / `Format` / `None`. |
| `SortOrder` | `DateDesc` | `DateDesc` / `DateAsc` / `NameAsc` / `SizeDesc`. |
| `ThumbnailCacheGB` | `4` | Max on-disk thumbnail cache size. |
| `BackgroundThumbs` | `true` | Generate thumbnails off the UI thread. |
| `GPUAcceleration` | `true` | Use SkiaSharp GPU rendering. |
| `PrefetchAdjacent` | `true` | Pre-decode next/prev images in the viewer. |
| `SlideshowInterval` | `5` (seconds) | Minimum 2 s. |
| `DefaultConvertFormat` | `JPEG` | Default target for Save As. |
| `DefaultJPEGQuality` | `90` | 0–100. |
| `AISubjectDetect` | `true` | Run YOLOv8-seg inference in the viewer. |
| `CustomThumbCachePath` | `null` | Override the thumbnail cache directory. |

### Runtime file layout

```
%LocalAppData%\Photon\
├── settings.json
├── library.db                # SQLite: favorites + albums
├── vault.db                  # Secure Folder salt + verification hash
├── logs\
├── thumbs\                   # SHA256-keyed JPEG thumbnails
├── vault\                    # <guid>.vault encrypted files
└── models\
    └── yolov8n-seg.onnx      # (optional) drop in to enable AI
```

### Enabling AI subject detection

Photon does **not** bundle the ONNX model file (to keep the MSIX small). To enable AI subject detection:

1. Download `yolov8n-seg.onnx` (or any YOLOv8-seg variant) — e.g. from the [Ultralytics releases](https://github.com/ultralytics/ultralytics).
2. Place it at `%LocalAppData%\Photon\models\yolov8n-seg.onnx`.
3. Restart Photon and open any photo in the viewer. Bounding boxes will appear within ~200 ms on a typical CPU.

If the file is missing, Photon silently skips detection — no errors, no telemetry, no degraded UX.

---

## 🎯 Usage Tips

- **Open a file from Explorer** — Photon registers as the default handler for 23 image extensions, so a double-click is enough.
- **Reveal in File Explorer** — right-click any gallery card → *Open in folder* launches Explorer with `/select`.
- **Copy a bitmap to the clipboard** — viewer toolbar → *Copy image*. Works for pasting into image editors, chat apps, or File Explorer.
- **Share via Windows Share Sheet** — viewer toolbar → *Share*. Picks up nearby devices, Mail, Teams, etc.
- **Slideshow from any photo** — open the viewer → *Slideshow*. Cycles through all siblings in the current gallery.
- **Compress to a specific file size** — editor → *Convert* tab → enter target KB. Photon binary-searches JPEG quality until it fits.
- **Lock the Secure Folder on demand** — toolbar → *Lock*. The window's screen-capture protection is also restored.
- **Reset everything** — close Photon, delete `%LocalAppData%\Photon\`. Next launch recreates defaults.

### Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `←` / `→` | Previous / next image (viewer, slideshow) |
| `Space` | Toggle viewer chrome / pause slideshow |
| `Esc` | Exit viewer or slideshow |
| `I` | Toggle EXIF panel (viewer) |
| `+` / `-` | Zoom in / out (viewer, editor) |
| `F` | Fit to canvas |
| `1` | 100% zoom |
| `Ctrl+Wheel` | Zoom toward cursor |
| `Double-click` | Toggle fit ↔ 100% |
| `P` | Play / pause slideshow |

---

## 🛣 Roadmap

- [ ] Segmentation mask rendering — wire YOLOv8-seg prototype-mask matmul into `SubjectOverlay`.
- [ ] "Focus on this subject" viewer mode (zoom-to-bbox on tap).
- [ ] People view — face clustering across the library.
- [ ] Map view — heat-map of GPS-tagged photos.
- [ ] Timeline scrubber for video playback.
- [ ] Localized UI (currently English-only).
- [ ] Light/dark theme toggle for the editor canvas.
- [ ] Bulk import from connected phones / cameras (WPD API).

Suggestions and PRs are welcome — see [Contributing](#-contributing).

---

## 🤝 Contributing

Contributions are welcome and appreciated!

1. **Fork** the repo and create a feature branch: `git checkout -b feat/my-feature`.
2. **Run a debug build** to make sure the project compiles cleanly on your machine: `dotnet build -c Debug`.
3. **Make your change.** Keep PRs focused — one feature or fix per PR is much easier to review.
4. **Test on Windows 11** if you can; the acrylic backdrop and WinUI 3 theming behave differently on Windows 10.
5. **Open a Pull Request** with a clear description of what changed and why.

### Coding conventions

- Target the existing style: `Nullable` enabled, `ImplicitUsings` enabled, `LangVersion=latest`.
- Prefer `record` types for immutable data (see `MediaItem`).
- Keep view code-behind thin — push business logic into `Core/` or `Services/`.
- New decoders should implement `IImageDecoder` and be added to `DecoderFactory` + `FormatRegistry`.
- New filters should be added to `FilterPipeline.Presets` with a one-line description and (if needed) a custom color matrix.
- Run `dotnet format` before committing if you have it installed.

### Reporting bugs

When filing an issue, please include:

- Windows version (`winver`).
- Photon version (visible in Settings → About, or from the MSIX package).
- Whether you ran the unpackaged `.exe` or the MSIX install.
- Steps to reproduce, expected vs actual behavior.
- Logs from `%LocalAppData%\Photon\logs\` (truncate to the relevant session).

---

## 📄 License

This project is licensed under the **MIT License** — see the [LICENSE](LICENSE) file for details.

> **Note:** Photon depends on third-party libraries with their own licenses:
> - [Magick.NET](https://github.com/dlemstra/Magick.NET) (Apache 2.0)
> - [SixLabors.ImageSharp](https://github.com/SixLabors/ImageSharp) (Six Labors Split License)
> - [MetadataExtractor](https://github.com/drewnoakes/metadata-extractor-dotnet) (Apache 2.0)
> - [ONNX Runtime](https://github.com/microsoft/onnxruntime) (MIT)
> - [SkiaSharp](https://github.com/mono/SkiaSharp) (MIT)
>
> If you ship Photon commercially, make sure your distribution complies with each of these.

---

## 🙏 Acknowledgments

- **[Windows App SDK team](https://github.com/microsoft/WindowsAppSDK)** — for the modern WinUI 3 shell.
- **[Ultralytics](https://github.com/ultralytics/ultralytics)** — for the YOLOv8 architecture and COCO pre-trained weights.
- **[Dirk Lemstra](https://github.com/dlemstra)** — for Magick.NET, which makes HEIC/AVIF/JXL/RAW decoding trivial on Windows.
- **[Six Labors](https://github.com/SixLabors)** — for ImageSharp, the workhorse for common-format decoding and thumbnailing.
- **[Drew Noakes](https://github.com/drewnoakes)** — for MetadataExtractor, the de-facto EXIF/IPTC/XMP reader for .NET.
- **The Mono Project** — for SkiaSharp, the cross-platform GPU canvas this whole thing is rendered on.

---

<div align="center">

Built with care for Windows 11. ⭐ Star the repo if Photon makes your photo workflow better.

</div>
