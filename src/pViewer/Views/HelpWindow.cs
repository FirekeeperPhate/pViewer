using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace pViewer.Views;

/// <summary>Elenco delle scorciatoie da tastiera e dei gesti del mouse.</summary>
public sealed class HelpWindow : Window
{
    private static readonly (string Section, (string Keys, string Action)[] Items)[] Shortcuts =
    [
        ("Navigazione", [
            ("→  Spazio  E  PagSu", "Immagine successiva"),
            ("←  Q  PagGiù", "Immagine precedente"),
            ("Home  /  Fine", "Prima / ultima immagine"),
            ("M  /  C", "Modalità manga / comic (due pagine)"),
            ("F12  /  Maiusc+F12", "Sposta la coppia avanti / indietro di una pagina"),
            ("Ctrl+O", "Apri file, cartella o archivio"),
            ("Trascina un file", "Apre immagini, cartelle e archivi"),
        ]),
        ("Vista", [
            ("Rotella  /  +  −", "Zoom (verso il puntatore con la rotella)"),
            ("Trascina", "Sposta l'immagine ingrandita"),
            ("A", "Dimensioni reali ↔ vista preferita"),
            ("Ctrl+0  /  Tn 5", "Ripristina la vista"),
            ("Ctrl+1", "Zoom al 100%"),
            ("Tn 4 8 6 2", "Sposta la vista"),
            ("F11  /  doppio clic", "Schermo intero"),
            ("Alt+Invio", "Ingrandisci / ripristina la finestra"),
            ("W", "Sfondo bianco"),
            ("T", "Mostra / nascondi la barra strumenti"),
            ("Esc", "Esce da schermo intero o presentazione, altrimenti chiude"),
        ]),
        ("Modifica", [
            ("↑  /  ↓", "Ruota a destra / a sinistra"),
            ("Alt+↑  /  Alt+↓", "Rifletti orizzontalmente / verticalmente"),
            ("Ctrl+trascina", "Ritaglia"),
            ("Alt+trascina", "Disegna un rettangolo"),
            ("Tab", "Riempie l'ultimo rettangolo (per oscurare dati)"),
            ("Maiusc+clic", "Scrivi un testo (Ctrl+Invio conferma, Esc annulla)"),
            ("R", "Correzione occhi rossi (trascina attorno all'occhio)"),
            ("Ctrl+R", "Ridimensiona"),
            ("Ctrl+I  /  Ctrl+G", "Negativo / scala di grigi"),
            ("Ctrl+Z  /  Ctrl+Y", "Annulla / ripeti"),
            ("F5", "Ricarica l'originale (scarta le modifiche)"),
        ]),
        ("File", [
            ("Ctrl+S", "Salva"),
            ("Ctrl+Maiusc+S", "Salva con nome"),
            ("F2", "Rinomina"),
            ("Canc", "Elimina (nel Cestino)"),
            ("Ctrl+C  /  Ctrl+V", "Copia l'immagine / incolla dagli appunti"),
            ("I", "Dati EXIF e metadati"),
            ("Ctrl+W  /  Ctrl+Q", "Chiudi pViewer"),
        ]),
    ];

    public HelpWindow()
    {
        Title = "Scorciatoie da tastiera";
        Width = 620;
        Height = 700;
        MinWidth = 400;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(24, 8, 24, 24) };
        foreach (var (section, items) in Shortcuts)
        {
            panel.Children.Add(new TextBlock { Text = section, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) });
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < items.Length; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var keys = new TextBlock { Text = items[i].Keys, FontFamily = new FontFamily("Segoe UI Semibold"), Margin = new Thickness(0, 3, 12, 3), Opacity = 0.9 };
                var action = new TextBlock { Text = items[i].Action, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3), Opacity = 0.8 };
                Grid.SetRow(keys, i);
                Grid.SetRow(action, i);
                Grid.SetColumn(action, 1);
                grid.Children.Add(keys);
                grid.Children.Add(action);
            }
            panel.Children.Add(grid);
        }
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        KeyDown += (_, e) => { if (e.Key is System.Windows.Input.Key.Escape or System.Windows.Input.Key.F1) Close(); };
    }
}
