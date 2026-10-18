using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace pViewer.Imaging;

/// <summary>
/// Removes the background of a photo: a neural network (ISNet, trained to tell the main subject
/// from everything else) gives every pixel a value from "background" to "subject", which becomes
/// the transparency of the image. It runs on this PC (ONNX Runtime, on the processor); the model,
/// 170 MB, is downloaded once, on request, and checked before it is used.
/// </summary>
internal static class BackgroundRemover
{
    public const string ModelFile = "isnet-general-use.onnx";
    public const long ModelSize = 178_648_008;
    public const string ModelSha256 = "60920e99c45464f2ba57bee2ad08c919a52bbf852739e96947fbb4358c0d964a";

    /// <summary>The ONNX conversion published by the rembg project (MIT) of the ISNet weights (Apache 2.0).</summary>
    public const string ModelUrl = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/isnet-general-use.onnx";

    /// <summary>Side of the square picture the network looks at.</summary>
    private const int Side = 1024;

    /// <summary>Kept per user, outside the program folder (which the user may not be allowed to write).</summary>
    public static string ModelPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pViewer", "models", ModelFile);

    /// <summary>Downloaded and complete (its content was checked when it was downloaded).</summary>
    public static bool ModelReady => File.Exists(ModelPath) && new FileInfo(ModelPath).Length == ModelSize;

    public static async Task DownloadModelAsync(IProgress<double>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        await Services.UpdateService.DownloadVerifiedAsync(ModelUrl, ModelPath, ModelSize, ModelSha256, progress, ct);
    }

    private static readonly object Lock = new();
    private static InferenceSession? _session;
    private static Timer? _release;

    /// <summary>
    /// The network, loaded on first use (about a second) and kept while background removal is
    /// being used; two minutes after the last use its memory goes back to Windows.
    /// </summary>
    private static InferenceSession Session()
    {
        lock (Lock)
        {
            if (_session is null)
            {
                // ONNX Runtime's own usage events: off (pViewer sends nothing anywhere).
                Environment.SetEnvironmentVariable("ORT_DISABLE_TELEMETRY", "1");
                OrtEnv.Instance().DisableTelemetryEvents();
                using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
                _session = new InferenceSession(ModelPath, options);
            }
            _release ??= new Timer(_ => Release());
            _release.Change(TimeSpan.FromMinutes(2), Timeout.InfiniteTimeSpan);
            return _session;
        }
    }

    private static void Release()
    {
        lock (Lock)
        {
            _session?.Dispose();
            _session = null;
        }
    }

    /// <summary>The same picture with its background made transparent.</summary>
    public static PixelBuffer Remove(PixelBuffer image)
    {
        float[] mask = Predict(image);
        return ApplyMask(image, mask, Side);
    }

    /// <summary>For each pixel of a <see cref="Side"/> × <see cref="Side"/> view of the image: 0 background … 1 subject.</summary>
    private static float[] Predict(PixelBuffer image)
    {
        var input = ToInput(Resampler.Resize(image, Side, Side));
        var session = Session();
        lock (Lock) // Run itself may run in parallel, but not against Release disposing the session
        {
            var tensor = new DenseTensor<float>(input, [1, 3, Side, Side]);
            using var outputs = session.Run([NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.First(), tensor)]);
            var prediction = outputs.First().AsTensor<float>();
            var mask = prediction is DenseTensor<float> dense ? dense.Buffer.Span[..(Side * Side)].ToArray() : prediction.ToArray()[..(Side * Side)];
            return Stretch(mask);
        }
    }

    /// <summary>
    /// The colors as the network was trained on them: planes of red, green and blue, divided by
    /// the brightest value of the picture, minus the average color of its training photos.
    /// </summary>
    internal static float[] ToInput(PixelBuffer square)
    {
        byte[] d = square.Data;
        int n = square.Width * square.Height;
        byte max = 1;
        for (int i = 0; i < d.Length; i += 4) max = Math.Max(max, Math.Max(d[i], Math.Max(d[i + 1], d[i + 2])));
        var input = new float[3 * n];
        for (int p = 0, i = 0; p < n; p++, i += 4)
        {
            input[p] = d[i + 2] / (float)max - 0.485f;          // red
            input[n + p] = d[i + 1] / (float)max - 0.456f;      // green
            input[2 * n + p] = d[i] / (float)max - 0.406f;      // blue
        }
        return input;
    }

    /// <summary>The network's values stretched to 0 … 1 (its raw scale has no fixed range).</summary>
    internal static float[] Stretch(float[] values)
    {
        float low = float.MaxValue, high = float.MinValue;
        foreach (float v in values) { low = Math.Min(low, v); high = Math.Max(high, v); }
        float range = Math.Max(1e-6f, high - low);
        for (int i = 0; i < values.Length; i++) values[i] = (values[i] - low) / range;
        return values;
    }

    /// <summary>The mask brought to the image's size and multiplied into its transparency.</summary>
    internal static PixelBuffer ApplyMask(PixelBuffer image, float[] mask, int side)
    {
        var small = new PixelBuffer(side, side);
        byte[] s = small.Data;
        for (int i = 0; i < side * side; i++)
        {
            byte v = (byte)Math.Clamp(mask[i] * 255 + 0.5f, 0, 255);
            s[i * 4] = s[i * 4 + 1] = s[i * 4 + 2] = v;
            s[i * 4 + 3] = 255;
        }
        byte[] full = Resampler.Resize(small, image.Width, image.Height).Data;
        var result = new PixelBuffer(image.Width, image.Height);
        byte[] d = image.Data, o = result.Data;
        Buffer.BlockCopy(d, 0, o, 0, o.Length);
        for (int i = 3; i < o.Length; i += 4) o[i] = (byte)((d[i] * full[i - 1] + 127) / 255);
        return result;
    }
}
