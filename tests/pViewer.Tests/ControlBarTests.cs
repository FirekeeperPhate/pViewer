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
            Assert.Equal(ControlBarMode.Always, new AppSettings().ControlBar);

            // A settings.json of 2.2 has no such key: the bar is on.
            string old = Path.Combine(dir, "old.json");
            File.WriteAllText(old, "{ \"ShowToolbar\": false }");
            var loaded = SettingsStore.Load(old);
            Assert.False(loaded.ShowToolbar);
            Assert.Equal(ControlBarMode.Always, loaded.ControlBar);

            // The choice is kept.
            string path = Path.Combine(dir, "settings.json");
            SettingsStore.Save(new AppSettings { ControlBar = ControlBarMode.FullScreenOnly }, path);
            Assert.Equal(ControlBarMode.FullScreenOnly, SettingsStore.Load(path).ControlBar);

            // A hand-edited value that does not exist: back to the default.
            File.WriteAllText(path, "{ \"ControlBar\": 7 }");
            Assert.Equal(ControlBarMode.Always, SettingsStore.Load(path).ControlBar);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
