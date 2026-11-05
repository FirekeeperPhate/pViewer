using pViewer.Services;
using Xunit;

namespace pViewer.Tests;

public class ControlBarTests
{
    [Fact]
    public void ControlBar_IsOnByDefault_AlsoForSettingsWrittenBeforeIt()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pv_bar_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.True(new AppSettings().ShowControlBar);

            // A settings.json of 2.2 has no such key: the bar is on.
            string old = Path.Combine(dir, "old.json");
            File.WriteAllText(old, "{ \"ShowToolbar\": false }");
            var loaded = SettingsStore.Load(old);
            Assert.False(loaded.ShowToolbar);
            Assert.True(loaded.ShowControlBar);

            // Turned off, it stays off.
            string path = Path.Combine(dir, "settings.json");
            SettingsStore.Save(new AppSettings { ShowControlBar = false }, path);
            Assert.False(SettingsStore.Load(path).ShowControlBar);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
