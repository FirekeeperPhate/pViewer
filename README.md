# pViewer

Visualizzatore e piccolo editor di immagini per Windows, con lettura di fumetti e manga
direttamente dagli archivi. Versione 2: riscrittura completa in **.NET 10 + WPF** del
pViewer 1.x (WinForms, .NET Framework 4.0).

## Funzioni

- **Visualizzazione veloce**: precaricamento delle immagini vicine, anteprima immediata dalla
  miniatura EXIF delle foto grandi, rotazione automatica secondo l'orientamento EXIF,
  ordinamento come in Esplora risorse.
- **Formati**: GIF, WebP e PNG animati (P mette in pausa), JPEG, PNG, GIF, BMP, TIFF, ICO, JPEG XR, WebP, TGA, QOI, PBM e, con le estensioni
  di Windows, HEIC/HEIF, AVIF, JPEG XL e RAW.
- **Fumetti e manga**: apre zip/cbz, rar/cbr, 7z/cb7 (anche annidati) leggendo le pagine in
  memoria, senza cartelle temporanee. Modalità a due pagine **Manga** (destra → sinistra) e
  **Comic** (sinistra → destra), F12 per riallineare le coppie; arrivati in fondo si passa da
  soli al volume successivo della cartella.
- **Viste**: adatta se più grande, adatta, riempi, adatta a larghezza/altezza, dimensioni
  reali; zoom verso il puntatore, pan, schermo intero, presentazione, sfondo bianco.
- **Modifica** con annulla/ripeti: ritaglio (Ctrl+trascina), rettangolo (Alt+trascina, Tab lo
  riempie per oscurare dati), testo direttamente sull'immagine (Maiusc+clic), occhi rossi,
  ruota/rifletti, ridimensiona, bordi, negativo, scala di grigi, seppia, bianco e nero,
  luminosità/contrasto, tinta/saturazione, nitidezza, sfocatura con anteprima dal vivo.
- **Salvataggio** sicuro (file temporaneo e sostituzione) che conserva i metadati EXIF dei JPEG.
- **Elaborazione in serie** di una cartella o di un archivio (ridimensiona, ruota, rifletti,
  converti, bordi, grigi, negativo) in una cartella separata, e **rinomina in serie**.
- Elimina nel Cestino, rinomina, copia/incolla dagli appunti, dati EXIF, proprietà del file,
  "Apri con", imposta come sfondo del desktop.
- **Portabile**: le impostazioni stanno in `settings.json` accanto all'eseguibile (se la cartella
  non è scrivibile si usa `%AppData%\pViewer`).

L’interfaccia è in inglese. Tutte le scorciatoie sono nella guida dell’app (F1).

## Compilare

Serve l'SDK di .NET 10.

```bash
dotnet build
dotnet test
```

Per i pacchetti distribuibili (eseguibile portabile unico e versione leggera che richiede il
.NET 10 Desktop Runtime) in `artifacts\`:

```bash
powershell -ExecutionPolicy Bypass -File publish.ps1
```

## Struttura

| Cartella | Contenuto |
|---|---|
| `src/pViewer/Core` | Formati, ordinamento naturale, sorgenti (cartella/archivio), navigazione manga/comic |
| `src/pViewer/Imaging` | Decodifica WIC con ripiego ImageSharp, operazioni di modifica, salvataggio |
| `src/pViewer/Services` | Cache e precaricamento, impostazioni, annulla/ripeti, batch, integrazione con Windows |
| `src/pViewer/Controls` | `ImageViewer`: zoom/pan e gesti di modifica |
| `src/pViewer/ViewModels` | `MainViewModel` (MVVM con CommunityToolkit.Mvvm) |
| `src/pViewer/Views` | Dialoghi |
| `tests/pViewer.Tests` | Test xUnit |

## Librerie

[ImageSharp](https://github.com/SixLabors/ImageSharp) 3.1 (Six Labors Split License; la 4.x
richiede una chiave di licenza), [SharpCompress](https://github.com/adamhathcock/sharpcompress) (MIT),
[MetadataExtractor](https://github.com/drewnoakes/metadata-extractor-dotnet) (Apache 2.0),
[CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) (MIT).

## Licenza

GNU GPL v3 — vedi [LICENSE](LICENSE).
