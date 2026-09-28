using System.Windows.Input;
using pViewer.Services;

namespace pViewer.Tests;

public class HotkeyTests
{
    [Fact]
    public void DefaultKeysAreNotSharedBetweenCommands()
    {
        var seen = new Dictionary<Shortcut, string>();
        foreach (var command in Hotkeys.All)
            foreach (var gesture in command.DefaultGestures)
                Assert.True(seen.TryAdd(gesture, command.Id), $"{gesture} is used by {seen.GetValueOrDefault(gesture)} and {command.Id}");
        Assert.Equal(Hotkeys.All.Length, Hotkeys.All.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void DefaultsMatchTheOriginalKeys()
    {
        var map = Hotkeys.BuildMap(null);
        Assert.Equal("Next", map[new Shortcut(Key.PageUp, ModifierKeys.None)]);      // PgUp = next, as in 1.x
        Assert.Equal("Previous", map[new Shortcut(Key.PageDown, ModifierKeys.None)]);
        Assert.Equal("SaveAs", map[new Shortcut(Key.S, ModifierKeys.Control | ModifierKeys.Shift)]);
        Assert.Equal("Maximize", map[new Shortcut(Key.Enter, ModifierKeys.Alt)]);
        Assert.Equal("ZoomIn", map[new Shortcut(Key.Add, ModifierKeys.Control)]);
    }

    [Theory]
    [InlineData("Ctrl+Shift+S", "Ctrl+Shift+S")]
    [InlineData("alt+right", "Alt+→")]
    [InlineData("PageUp", "PgUp")]
    [InlineData("Next", "PgDn")]           // another name of PageDown
    [InlineData("Alt+Return", "Alt+Enter")]
    [InlineData("Ctrl+D0", "Ctrl+0")]
    [InlineData("NumPad5", "Num 5")]
    [InlineData("Add", "Num +")]
    public void ParsesAndDisplays(string text, string display)
    {
        Assert.True(Shortcut.TryParse(text, out var gesture));
        Assert.Equal(display, gesture.Display);
        Assert.True(Shortcut.TryParse(gesture.ToString(), out var again));
        Assert.Equal(gesture, again);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Hyper+S")]
    [InlineData("NotAKey")]
    [InlineData("Ctrl+LeftShift")]
    public void RejectsInvalidText(string text) => Assert.False(Shortcut.TryParse(text, out _));

    [Fact]
    public void AssigningAKeyMovesItFromTheOtherCommand()
    {
        var overrides = new Dictionary<string, List<string>>();
        var ctrlS = new Shortcut(Key.S, ModifierKeys.Control);
        var previous = Hotkeys.Assign(overrides, "Copy", ctrlS);
        Assert.Equal("Save", previous?.Id);
        Assert.Empty(Hotkeys.GesturesOf("Save", overrides));
        Assert.Equal([new Shortcut(Key.C, ModifierKeys.Control), ctrlS], Hotkeys.GesturesOf("Copy", overrides));

        var map = Hotkeys.BuildMap(overrides);
        Assert.Equal("Copy", map[ctrlS]);
        // Untouched commands keep their defaults and are not stored.
        Assert.Equal("Next", map[new Shortcut(Key.Right, ModifierKeys.None)]);
        Assert.Equal(["Copy", "Save"], overrides.Keys.Order());
    }

    [Fact]
    public void KeysEqualToTheDefaultsAreNotStored()
    {
        var overrides = new Dictionary<string, List<string>>();
        Hotkeys.Set(overrides, "Save", [new Shortcut(Key.F9, ModifierKeys.None)]);
        Assert.Contains("Save", overrides.Keys);
        Hotkeys.Set(overrides, "Save", Hotkeys.Find("Save")!.DefaultGestures);
        Assert.Empty(overrides);
    }

    [Fact]
    public void BrokenEntriesInTheSettingsAreIgnored()
    {
        var overrides = new Dictionary<string, List<string>> { ["Save"] = ["F9", "Bogus+Key", "F9"] };
        Assert.Equal([new Shortcut(Key.F9, ModifierKeys.None)], Hotkeys.GesturesOf("Save", overrides));
    }
}
