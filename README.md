# pViewer

Image viewer and small editor for Windows, with comic and manga reading straight from
archives.

This is the revised and finally updated version of the original pViewer, published on
SourceForge: [sourceforge.net/projects/picoviewer](https://sourceforge.net/projects/picoviewer/).
Version 2 is a complete rewrite in **.NET 10 + WPF** of pViewer 1.x (WinForms, .NET Framework
4.0), keeping its ideas and keyboard shortcuts.

## Features

- **Fast viewing**: nearby images are preloaded, large photos show their EXIF thumbnail
  instantly, photos are auto-rotated from their EXIF orientation, files are sorted like in
  File Explorer.
- **Formats**: animated GIF, WebP and PNG (P pauses), JPEG, PNG, GIF, BMP, TIFF, ICO, JPEG XR,
  WebP, TGA, QOI, PBM and, with the Windows extensions, HEIC/HEIF, AVIF, JPEG XL and RAW.
- **Comics and manga**: opens zip/cbz, rar/cbr, 7z/cb7 (nested archives too) reading pages in
  memory, with no temporary folders. Two-page **Manga** (right to left) and **Comic** (left to
  right) modes, F12 to realign page pairs; at the end of an archive the next volume in the
  folder opens automatically.
- **Views**: shrink to fit, fit, fill, fit width/height, actual size; zoom towards the pointer,
  pan, full screen, slideshow, white background.
- **Editing** with undo/redo: crop (Ctrl+drag), rectangle (Alt+drag, Tab fills it to hide
  data), text written directly on the image (Shift+click), red-eye, rotate/flip, resize,
  borders, invert, grayscale, sepia, black and white, brightness/contrast, hue/saturation,
  sharpen, blur with live preview.
- **Safe saving** (temporary file, then replace) that keeps the EXIF metadata of JPEG photos.
- **Batch processing** of a folder or an archive (resize, rotate, flip, convert, borders,
  grayscale, invert) into a separate folder, and **batch rename**.
- Delete to the Recycle Bin, rename, copy/paste via the clipboard, EXIF data, file properties,
  "Open with", set as desktop background.
- **Updates**: once a day pViewer asks GitHub whether a newer release exists (it sends nothing
  else) and offers to install it; the installer then reopens pViewer on the same file. The check
  can be turned off in Settings › Updates, and Help › Check for updates runs it on demand.
- Settings live in `settings.json` next to the executable (if that folder is not writable,
  `%AppData%\pViewer` is used).

All keyboard shortcuts are listed in the app's help (F1) and can be changed in Settings › Keyboard shortcuts.

## Installing

Download an installer from the [releases](https://github.com/MarcoTrombetta/pViewer/releases):

- **Full** (`pViewer-Setup-<version>-Full.exe`): includes the .NET runtime, no prerequisites.
- **Light** (`pViewer-Setup-<version>-Light.exe`): much smaller, needs the
  [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64); setup
  checks for it and offers the download page.

Both install per user by default (no administrator rights needed; installing for all users can
be chosen in the first dialog), optionally add pViewer to the "Open with" menu of images and
comic archives, and replace each other. Requires 64-bit Windows 10 or 11.

## Building

Requires the .NET 10 SDK.

```bash
dotnet build
dotnet test
```

To build the two installers (needs [Inno Setup](https://jrsoftware.org/isinfo.php) 6.6 or later) into
`installer\Output\` — the script runs the tests first, then publishes both editions:

```bash
powershell -ExecutionPolicy Bypass -File installer\build.ps1
```

## Project layout

| Folder | Contents |
|---|---|
| `src/pViewer/Core` | Formats, natural sorting, image sources (folder/archive), manga/comic navigation |
| `src/pViewer/Imaging` | WIC decoding with ImageSharp fallback, editing operations, saving |
| `src/pViewer/Services` | Caching and preloading, settings, undo/redo, batch, Windows integration |
| `src/pViewer/Controls` | `ImageViewer`: zoom/pan and editing gestures |
| `src/pViewer/ViewModels` | `MainViewModel` (MVVM with CommunityToolkit.Mvvm) |
| `src/pViewer/Views` | Dialogs |
| `tests/pViewer.Tests` | xUnit tests |

## Libraries

| Library | License |
|---|---|
| [ImageSharp](https://github.com/SixLabors/ImageSharp) 3.1 | Apache 2.0 (granted to open-source projects by the Six Labors Split License; 4.x requires a license key) |
| [MetadataExtractor](https://github.com/drewnoakes/metadata-extractor-dotnet) | Apache 2.0 |
| [XmpCore](https://github.com/drewnoakes/xmp-core-dotnet) (used by MetadataExtractor) | BSD 3-Clause |
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MIT |
| [.NET runtime](https://github.com/dotnet/runtime) (Full edition only) | MIT |

Their copyright notices and license texts are in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt),
which is installed next to pViewer and linked from its About window; the Full edition also
installs the .NET runtime's own `dotnet-LICENSE.txt` and `dotnet-THIRD-PARTY-NOTICES.txt`.

## License

pViewer is released under the GNU GPL v3 — see [LICENSE](LICENSE).
