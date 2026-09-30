# Changelog

## 2.0.10

- The GitHub account moved from MarcoTrombetta to FirekeeperPhate: updates, links and the
  release page now point to https://github.com/FirekeeperPhate/pViewer.

## 2.0.9

**Fixed**
- Two-page view without edits: Ctrl+S says there is nothing to save, and Save As proposes a
  name like "p0-p1.png" instead of the first page's own file (accepting it replaced that page
  with the spread, and the view went on showing the old pages).
- The page pairing (F12) is kept after F5, Delete, Rename and batch rename; deleting the last
  page alone no longer splits the spread before it.
- A folder passed on the command line as "C:\Pics\" (which arrives as C:\Pics") opens, and a
  trailing backslash no longer stops Save As into that folder from updating the list.
- A settings.json briefly held by another program (sync client, backup, antivirus) is read
  again for a moment, and if it stays locked pViewer never writes its defaults over it.
- PNG: many compressed chunks just under the size limit share one budget (they added up to GBs
  in the metadata window); the status bar shows the real file size of a PNG whose oversized
  chunks were left out.
- A corrupt or oversized archive page is no longer read again each time it is prefetched.
- Rename and Set as wallpaper wait until the next image is shown, like Delete.
- Updates: another user's pViewer counts for an all-users install in any folder; a release
  with an odd installer size is ignored instead of showing an error.
- Light setup: a per-user .NET runtime in %LocalAppData%, which pViewer cannot use, no longer
  counts as installed.

## 2.0.8

**Fixed**
- Updates: edits made while the update downloads are asked about before pViewer closes, and a
  dialog, a save or an archive being opened is allowed to finish first. pViewer closes only once
  the installer has really started, so refusing the UAC prompt of an all-users install leaves it
  open; the installer then waits for pViewer to exit instead of stopping with "pViewer is
  running". Other open pViewer windows are reported before downloading. A download that stops
  responding fails after 30 s instead of waiting forever. Old downloaded installers are
  deleted. A clock that had been set ahead no longer stops the daily check.
- Archives: the "Opening… (Esc to cancel)" hint disappears when the opening ends (Esc then did
  something else, like closing the window); Esc pressed just as an archive finished opening
  still cancels it; a slideshow stops at the last volume when the next one cannot be opened;
  the page pairing of a volume is remembered only from two-page modes.
- A rectangle lower than its pen (an underline) no longer fails; Brightness/Contrast and
  Hue/Saturation with the sliders at zero no longer mark the image as changed.
- Holding Delete no longer deletes the images that follow one after another before they are
  shown (the same for Exit and Reload).
- Shortcuts: a key moved away and back leaves the command on its defaults; a misspelt key in
  settings.json keeps the command's default keys.
- Archive pages named with characters Windows does not allow (made on a Mac or on Linux) can
  be batch-processed and saved.
- "ActualSize" as the default view in a hand-edited settings file is reset (the A toggle had
  nothing to go back to).
- A PNG with a crafted compressed text chunk (or color profile) that inflates to hundreds of MB
  opens at once without that chunk, instead of taking tens of seconds and GBs of memory (also
  in the metadata window and when pasting).
- Saving a lossless WebP keeps it lossless (it was re-encoded as lossy).
- A page that could not be read (still being copied, locked by another program) is read again
  when shown, instead of repeating the error until F5.
- Fit height: scrolling sideways no longer stops the image from refitting when the window
  changes size, and the scroll position is kept, as in fit width.
- Zooming while dragging the image no longer makes it jump on the next mouse move.
- Resize: a side derived from the other (or from the percentage) never becomes 0.
- Slideshow › Custom accepts the decimal comma, like Settings.
- Holding Esc no longer goes on from leaving full screen to closing the window.
- Updates: undo or redo during the download is also asked about; another user's pViewer no
  longer blocks the update of a per-user install.
- The welcome screen no longer shows a stale zoom after a failed open.

## 2.0.7

**New**
- Automatic updates: once a day (after startup, in the background) pViewer looks for a newer
  release on GitHub and offers to install it (Update now / Skip this version / Later). The
  installer is downloaded from GitHub, checked (size and SHA-256), run, and pViewer reopens on
  the same file. On by default, it can be turned off in Settings › Updates; Help › Check for
  updates checks right away. Copies not installed with the setup open the download page instead.

## 2.0.6

- THIRD-PARTY-NOTICES.txt with the licenses of the bundled components, installed next to pViewer
  and linked from the About window; the Full edition also installs the .NET runtime's license
  and notices. README and About name the licenses as granted (ImageSharp: Apache 2.0) and list
  XmpCore.

## 2.0.5

**New**
- Opening an archive can be cancelled with Esc (a toast says so when it takes a while), and
  opening something else stops it.

**Fixed**
- Shortcuts: restoring a default no longer leaves a key on two commands; Esc cancels recording;
  Alt+F4, Alt+Space and Windows-key combinations are refused; punctuation keys are shown as the
  character of the keyboard layout; a hand-edited settings file can no longer crash pViewer.
- Files dropped while a dialog is open are refused (they could replace the image being saved).
- A hidden image opened from Explorer is the one shown.
- A maximized window saved on a larger monitor fits the current one when restored.
- In full screen, T no longer hides the toolbar of the normal window unseen.
- Batch rename after saving changes into the folder renames the right list of files.
- Saving over a file keeps its creation date and attributes (a hidden image stays hidden).
- The later parts of a multi-part RAR (.part2.rar…) are no longer taken for separate volumes;
  the F12 pairing of a volume is kept when coming back to it.
- Ctrl+S on a pasted image already saved says "Nothing to save" instead of asking again.
- Two-page view redraws when a file saved into the folder shifts the pair.

**Robustness**
- Crafted or broken files can no longer crash pViewer (metadata of an endless EXIF chain),
  allocate gigabytes (tiny TGA/QOI/PPM files claiming huge sizes, oversized archive pages or
  nested archives, oversized PNG color profiles) or freeze it (GIFs with thousands of frames are
  shown still).
- Out-of-range or broken values in a hand-edited settings.json go back to the defaults.

**Installer**
- Setup and uninstall ask to close a running pViewer; an interrupted upgrade no longer leaves a
  program that cannot start.
- Unticking "Open with" on an upgrade removes the registrations; uninstall leaves no empty
  registry key.
- The Light edition finds the .NET runtime also in per-user and custom locations.

**Other**
- A "Status bar" command that can be given a key; hints and help show the current keys; the
  help lists all mouse actions; English quotation marks in messages.

## 2.0.4

- Settings: "Show the toolbar" and "Show the status bar" options (the same as T and the view menu).
- Settings: a "Keyboard shortcuts" section to change the keys of every command (the defaults are
  the usual ones). Menus, toolbar tooltips and the F1 help show the current keys.

## 2.0.3

**Faster startup**
- Opening an image from Explorer is about 30% faster (about 1.0 s to 0.7 s for a 24 MP photo on
  a warm start): the image is read and decoded while the window is being built, and appears in the
  window's first frame instead of after an empty window.
- Settings are read in the background during startup; the image context menu is built once the
  first image is on screen.

## 2.0.2

**Fixed**
- Colors of wide-gamut photos (Adobe RGB, Display P3) no longer shift after an edit and a save:
  Windows shows them converted to sRGB, and the saved file now says so (regression of 2.0.1;
  edited JPEGs were affected before too). 16-bit, grayscale and CMYK images keep their own profile.
- Save As of an unedited CMYK or 16-bit image to PNG or BMP no longer fails; a CMYK image saved as
  PNG no longer gets an invalid CMYK profile.
- Animations too large to play are shown still but are never overwritten with a single frame.
- Edits made while "Save changes? → Yes" is saving are no longer lost when moving to another image;
  a new Ctrl+S after further edits is no longer refused as "Already saving".
- The right-click menu of the text box no longer uses the text's huge font; right-clicking the
  text frame opens the text menu instead of the image menu (whose commands dropped the text).
- Esc while drawing a crop or red-eye selection cancels it instead of closing pViewer.
- Text is not applied while a rotation or crop is still running; batch processing waits for
  running work.

**Interface**
- A window saved on a larger monitor fits the current one; tall dialogs fit small screens.
- In narrow windows the "All commands" button stays visible.
- The welcome text stays readable on white (W) and full-screen backgrounds.
- Error texts and dialog icons use the theme colors (readable in the light theme); the text color
  in Settings is shown as a swatch and the font preview adapts to the text color.

## 2.0.1

Bug-fix release: four review passes over the whole code base.

**Saving**
- Unsaved edits are never written over the wrong file: edits and saves run in order, belong to the
  image they were made on, and opening another image waits for them.
- The embedded color profile (ICC) is kept after edits, so colors no longer shift in other apps.
- Rotating, flipping and cropping keep 16-bit, CMYK and HDR images as they are; every edit keeps
  the image resolution (a 300 dpi scan stays 300 dpi).
- Animated GIF/WebP and multi-page TIFF files are never overwritten with a single frame: Ctrl+S
  asks for a new name.
- GIF and black & white keep transparency; BMP is always saved in a standard bit depth; long file
  names save correctly; the XMP orientation is reset together with the EXIF one.
- Save As switches to the saved file when it is in the folder being browsed; a second Ctrl+S
  while saving no longer asks again.

**Viewing**
- Animated PNG (APNG) files animate.
- Sharp images at 100% with 125/150% display scaling; rectangles and joined pages drawn on whole pixels.
- Wheel zoom follows touchpads and smooth-scrolling mice; small icons can be zoomed out again.
- Full screen, toolbar and window resizes refit the image after a simple click or a scroll that
  did not move it.
- Camera photos show a correctly proportioned preview while loading.

**Comics and archives**
- Switching to two pages mid-book, F12 realignment and Save As keep the page pairs correct in
  both directions.
- Broken volumes are skipped; a failed archive open restores the current page.
- Missing or locked files show a message on the page instead of an error dialog.

**Other**
- Toolbar menus no longer show their items as empty rectangles.
- Settings are stored in %AppData% when the program folder is not writable; invalid values
  (also "NaN") are rejected.
- Batch rename keeps the files in order with large starting numbers.
- Installer: cleaning an upgrade only touches a folder this setup installed to.

## 2.0.0

Complete rewrite in .NET 10 and WPF (Fluent theme), keeping the features and keyboard shortcuts of 1.x.

**New**
- Archives are read in memory, with no extraction to a temporary folder; 7z/cb7 support; going back
  from the first page also opens the *previous* volume.
- Undo/redo for every edit, with a warning when leaving a modified image.
- Text is written directly on the image at its final size and can be moved by dragging its border.
- Hue/saturation, sepia, live preview of effects with a compare-to-original option.
- "Fill", "fit width" (with mouse-wheel scrolling) and "fit height" views.
- True 100% zoom even with display scaling, sharp pixels beyond 250%.
- Animated GIF, WebP and APNG (P key to pause/resume).
- WebP, HEIC, AVIF, JPEG XL, RAW (with the Windows codecs), TGA and QOI formats; saving to WebP and JPEG XR.
- Saving keeps the EXIF data of photos (date, camera, GPS) and resets the orientation.
- Del moves files to the Recycle Bin (configurable); batch rename never overwrites other files.
- Batch processing also works on the pages of an archive, with progress and a stop button.
- Settings in `settings.json` (window position included: no more registry).
- English user interface, with confirmation dialogs that follow the dark/light theme.
- Installers in two editions: Full (.NET runtime included) and Light (needs the .NET 10 Desktop
  Runtime); per-user install without administrator rights, optional "Open with" entries.

**Removed**
- Sending images to the Dropbox Public folder (Dropbox shut that service down in 2017).
- Update check on pviewer.net.

## 1.x history

```
V. 1.6
*NEW* Completely redesigned the text writing, press Shift+Click on the picture to add text, write what you want (now in real time), move it wherever you want with the mouse, press Ctrl+Enter.
*NEW* Send image to the public folder of your dropbox installation, it will automatically put the link in your clipboard to send it wherever you want.
*NEW* Red Eye Correction effect. Press "R", draw a selection on the eye you want and release the mouse button, done. Do the same for the other eye/eyes.
*NEW* Pressing TAB after drawing a rectangle will fill it with the rectangle color. Useful to hide some sensible information in screenshots.
*NEW* New setting, completely optional, to treat every archive as a manga or a comic.
*NEW* Batch Resize, Rotate/Flip, Convert, White Border, Black Border, Invert, Grayscale. (It will create a new folder, no overwriting).
*NEW* Batch Rename.
*NEW* File Rename.
*IMPROVEMENT* The rectangle is now of the appropriate size and always proportional to the image.
*IMPROVEMENT* Better application icon.
*IMPREVEMENT* Better help form.
*MISC* A bit nicer settings window.

V. 1.5
*NEW* New effects: Sharpen, Blur, Brightness/Contrast, Tint. With real time preview.
*NEW* Automatic new version checker, enabled by default, can be disabled in the settings.
*NEW* JPEG save quality in the settings.
*NEW* Automatic EXIF Orientation selector in settings.
*NEW* New keyboard shortcut: Alt+Enter to maximize/restore the window.
*NEW* The application can now open folders directly, when drag and dropped into the main window.
*NEW* When in manga/comic mode from an archive, the application will automatically open the next archive in the folder when reaching the last file in the current archive. This means seamless manga/comic reading without having to open each chapter individually.
*IMPROVEMENT* MultiThreaded, next image is now cached for MUCH faster browsing.
*IMPROVEMENT* MultiThreaded, the next two images in manga/comic mode are created in the background, resulting in instant page switching.
*IMPROVEMENT* MultiThreaded, while loading a jpeg, the program will first load the thumbnail (if present) for seamless navigation (similar to the default windows photo viewer behaviour).
*IMPROVEMENT* Faster cropping and drawing.
*IMPROVEMENT* Various code optimizations.
*FIXED* Some images weren't loading when EXIF wasn't found.
*FIXED* Archive extraction progress bar should now be much more accurate.
*FIXED* In some specific cases the F12 shortcut would cause the application to crash.

V. 1.4
- Updated SharpCompress to the latest version.
- Changed the settings, now everything is saved in the "settings.ini" file, which reside in the same directory as the executable. This way the software is now fully portable, no need to write files on the disk if you run it from a usb thumb drive. If the software doesn't find the settings.ini file it will create a default one on its own.

V. 1.3
- Updated SharpCompress to the latest version.
- Added automatic EXIF orientation, now photos will be displayed in the correct orientation.
- Changed the code responsible for the loading of images, performance shouldn't be affected.
- Added 4 new shortcuts: PageUP/PageDown for Next/Previous respectively, and Home/End to go to the First/Last image in the folder.

V. 1.2
- Added two more shortcuts (Q for previous, E for next), for complete left hand navigation
- Added a Fit to Window viewing mode
- Added a Ctrl+C shortcut to copy the image to the clipboard (NOTE: this copy the actual image, NOT the file). You can then paste the image in your favourite image editor.
- Removed the logo when opening the program, to avoid conflicts with some Windows themes color (it didn't look nice with some themes, and i deemed it pretty useless anyway).

V. 1.1
- Slideshow skipped images, it's now fixed.

V. 1.0
- Initial release.
```
