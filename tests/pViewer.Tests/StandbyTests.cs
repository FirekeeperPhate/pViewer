using pViewer.Services;

namespace pViewer.Tests;

public class StandbyTests
{
    [Fact]
    public void RequestSurvivesTheTrip()
    {
        var sent = new StandbyRequest(@"C:\Pics\Été", [@"C:\Pics\Été\a b.jpg", "second"], @"C:\t\trace.txt", 638_000_000_000_000_000);
        var stream = new MemoryStream();
        sent.Write(stream);
        stream.Position = 0;
        var received = StandbyRequest.Read(stream)!;
        Assert.Equal(sent.WorkingDirectory, received.WorkingDirectory);
        Assert.Equal(sent.Args, received.Args);
        Assert.Equal(sent.TracePath, received.TracePath);
        Assert.Equal(sent.StartTicksUtc, received.StartTicksUtc);
    }

    [Fact]
    public void RequestOfAnotherVersionIsRefused()
    {
        var stream = new MemoryStream();
        new StandbyRequest("C:\\", [], "", 0).Write(stream);
        var bytes = stream.ToArray();
        bytes[0] = 99; // the version comes first
        Assert.Null(StandbyRequest.Read(new MemoryStream(bytes)));
    }

    [Fact]
    public void ServeAnswersWithItsProcessAndTakesTheRequest()
    {
        // What the waiting copy reads (the request) and what it writes (process id, then "taken").
        var incoming = new MemoryStream();
        new StandbyRequest(@"D:\x", ["one.png"], "", 0).Write(incoming);
        var pipe = new TwoWay(new MemoryStream(incoming.ToArray()), new MemoryStream());
        var request = Standby.Serve(pipe)!;
        Assert.Equal(["one.png"], request.Args);
        var answer = pipe.Written.ToArray();
        Assert.Equal(Environment.ProcessId, BitConverter.ToInt32(answer, 0));
        Assert.Equal(1, answer[4]);
        // A caller that hangs up before sending anything: nothing taken, no exception.
        Assert.Null(Standby.Serve(new TwoWay(new MemoryStream(), new MemoryStream())));
    }

    [Theory]
    [InlineData(true, "--standby")]
    [InlineData(false)]
    [InlineData(false, "photo.jpg")]
    [InlineData(false, "--standby", "photo.jpg")]
    public void OnlyTheBareArgumentStartsTheWaitingCopy(bool expected, params string[] args) =>
        Assert.Equal(expected, Standby.IsStandbyStart(args));

    [Fact]
    public void KeepReadyIsOnByDefaultAndSaved()
    {
        Assert.True(new AppSettings().KeepReady);
        using var tmp = new TempFolder();
        string path = tmp.File("settings.json");
        SettingsStore.Save(new AppSettings { KeepReady = false }, path);
        Assert.False(SettingsStore.Load(path).KeepReady);
    }

    [Fact]
    public void ATestBuildNeverWaitsInTheBackground()
    {
        // No uninstaller next to the test host: nothing may be left running after the tests.
        Assert.False(Standby.Allowed);
        Assert.False(Standby.IsRunning());
    }

    /// <summary>Reads from one stream and writes to another, like the two directions of a pipe.</summary>
    private sealed class TwoWay(MemoryStream read, MemoryStream written) : Stream
    {
        public MemoryStream Written => written;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => written.Write(buffer, offset, count);
    }
}
