# Novità

## 2.0.0

Riscrittura completa in .NET 10 e WPF (tema Fluent), mantenendo funzioni e scorciatoie della 1.x.

**Nuovo**
- Archivi letti in memoria, senza estrazione in una cartella temporanea; supporto 7z/cb7; si torna
  anche al volume *precedente* andando indietro dalla prima pagina.
- Annulla/ripeti per tutte le modifiche, con avviso se si lascia un'immagine modificata.
- Testo scritto direttamente sull'immagine alla dimensione finale, spostabile trascinandone il bordo.
- Tinta/saturazione, seppia, anteprima dal vivo degli effetti con confronto con l'originale.
- Viste "riempi", "adatta alla larghezza" (con scorrimento a rotella) e "adatta all'altezza".
- Zoom al 100% reale anche con scalatura dello schermo, pixel netti oltre il 250%.
- GIF, WebP e APNG animati (tasto P per pausa/riprendi).
- Formati WebP, HEIC, AVIF, JPEG XL, RAW (con i codec di Windows), TGA, QOI; salvataggio in WebP e JPEG XR.
- Il salvataggio conserva i dati EXIF delle foto (data, fotocamera, GPS) e azzera l'orientamento.
- Canc sposta nel Cestino (configurabile); la rinomina in serie non sovrascrive mai altri file.
- Elaborazione in serie anche delle pagine di un archivio, con avanzamento e interruzione.
- Impostazioni in `settings.json` (anche la posizione della finestra: niente più registro).
- Interfaccia interamente in inglese, con dialoghi di conferma che seguono il tema scuro/chiaro.

**Rimosso**
- Invio alla cartella Public di Dropbox (servizio chiuso da Dropbox nel 2017).
- Controllo aggiornamenti su pviewer.net.

## Storico 1.x

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
