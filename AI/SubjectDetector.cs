using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using Photon.Core;

namespace Photon.AI;

/// <summary>
/// Phase-4 ONNX-backed subject / person detector. Loads a YOLOv8-segmentation
/// model from <see cref="ModelPath"/> on first use, runs inference on each
/// image passed to <see cref="DetectAsync"/>, and returns a list of detected
/// subjects with bounding boxes, confidence scores, and (optionally)
/// segmentation masks.
///
/// If no model file is present, returns an empty list and logs a warning —
/// the rest of the app degrades gracefully to "no AI" mode.
/// </summary>
public sealed class SubjectDetector : IDisposable
{
    private readonly ILogger<SubjectDetector> _log;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private InferenceSession? _session;
    private bool _triedLoad;

    /// <summary>Expected location for the bundled ONNX model file.</summary>
    public static string ModelPath =>
        Path.Combine(AppPaths.AppRoot, "models", "yolov8n-seg.onnx");

    /// <summary>Input dimension the YOLOv8 model expects (square).</summary>
    private const int ModelInputSize = 640;

    /// <summary>Confidence threshold below which detections are discarded.</summary>
    public float ConfidenceThreshold { get; set; } = 0.45f;

    /// <summary>IoU threshold for non-maximum suppression.</summary>
    public float IoUThreshold { get; set; } = 0.5f;

    public SubjectDetector(ILogger<SubjectDetector> log) => _log = log;

    /// <summary>True when an ONNX model file is present at <see cref="ModelPath"/>.</summary>
    public bool IsAvailable => File.Exists(ModelPath);

    /// <summary>
    /// Run detection on the supplied image stream. Returns a list of detected
    /// subjects (bounding boxes in image pixel coordinates, segmentation masks
    /// downsampled to the model's output resolution).
    /// </summary>
    public async ValueTask<IReadOnlyList<DetectedSubject>> DetectAsync(
        Stream imageStream, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            if (!_triedLoad)
            {
                _log.LogWarning("SubjectDetector: no ONNX model at {Path}. " +
                                "Drop a YOLOv8n-seg ONNX model there to enable AI subject detection.",
                                ModelPath);
                _triedLoad = true;
            }
            return Array.Empty<DetectedSubject>();
        }

        var session = await GetSessionAsync().ConfigureAwait(false);
        if (session is null) return Array.Empty<DetectedSubject>();

        // Decode the source image to an SKBitmap, then preprocess to a 640×640 float tensor.
        using var codec = SKCodec.Create(imageStream);
        if (codec is null) return Array.Empty<DetectedSubject>();
        using var srcBitmap = SKBitmap.Decode(codec);
        if (srcBitmap is null) return Array.Empty<DetectedSubject>();

        var (tensor, scaleX, scaleY) = Preprocess(srcBitmap);

        // Run inference.
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("images", tensor),
        };

        using var results = session.Run(inputs);

        // YOLOv8-seg ONNX exports two outputs:
        //   output0: [1, 4 + num_classes + 32, num_anchors] — boxes + scores + mask coefficients
        //   output1: [1, 32, mask_height, mask_width]       — prototype masks
        // The exact names depend on the export; we read by index as a fallback.
        var outputTensors = results.ToList();
        if (outputTensors.Count < 1) return Array.Empty<DetectedSubject>();

        var output0 = outputTensors[0].AsTensor<float>();
        // We only parse boxes + scores (segmentation masks would need the
        // prototype tensor + matrix multiply — left as a Phase 5 enhancement).
        return Postprocess(output0, srcBitmap.Width, srcBitmap.Height, scaleX, scaleY);
    }

    private async Task<InferenceSession?> GetSessionAsync()
    {
        if (_session is not null) return _session;
        await _sessionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session is not null) return _session;
            var opts = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };
            // Try CPU EP first — works everywhere. CUDA EP can be added if available.
            _session = new InferenceSession(ModelPath, opts);
            _log.LogInformation("Loaded ONNX model from {Path}", ModelPath);
            return _session;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load ONNX model from {Path}", ModelPath);
            return null;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// Resize the source bitmap to 640×640 (letterboxed, but for simplicity we
    /// just stretch — YOLOv8 typically expects square input), normalize pixels
    /// to 0..1, and convert to NCHW float tensor.
    /// </summary>
    private (DenseTensor<float> Tensor, float ScaleX, float ScaleY) Preprocess(SKBitmap src)
    {
        var resized = new SKBitmap(ModelInputSize, ModelInputSize, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(resized))
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium })
        {
            canvas.Clear(SKColors.Black);
            canvas.DrawBitmap(src, new SKRect(0, 0, ModelInputSize, ModelInputSize), paint);
        }

        var tensor = new DenseTensor<float>(new[] { 1, 3, ModelInputSize, ModelInputSize });
        // Walk pixels and split into 3 channel planes (RGB), normalized to 0..1.
        // Skip alpha.
        for (int y = 0; y < ModelInputSize; y++)
        {
            for (int x = 0; x < ModelInputSize; x++)
            {
                var px = resized.GetPixel(x, y);
                tensor[0, 0, y, x] = px.Red   / 255f;
                tensor[0, 1, y, x] = px.Green / 255f;
                tensor[0, 2, y, x] = px.Blue  / 255f;
            }
        }
        resized.Dispose();

        float scaleX = (float)src.Width  / ModelInputSize;
        float scaleY = (float)src.Height / ModelInputSize;
        return (tensor, scaleX, scaleY);
    }

    /// <summary>
    /// Parse YOLOv8 detection output: shape [1, 4+num_classes, num_anchors].
    /// The first 4 channels per anchor are xywh (in 640-space); the next
    /// num_classes are class confidences. We pick the max confidence per
    /// anchor, threshold, and run NMS to remove overlaps.
    /// </summary>
    private List<DetectedSubject> Postprocess(
        Tensor<float> output, int srcW, int srcH, float scaleX, float scaleY)
    {
        var dims = output.Dimensions; // [1, 4+nc, num_anchors]
        if (dims.Length != 3) return new List<DetectedSubject>();
        int numClasses = dims[1] - 4;
        int numAnchors = dims[2];
        if (numClasses <= 0) return new List<DetectedSubject>();

        var candidates = new List<(int ClassIdx, float Score, SKRect Box)>(capacity: 256);

        for (int a = 0; a < numAnchors; a++)
        {
            // Find the best class for this anchor.
            float bestScore = 0f;
            int bestClass = -1;
            for (int c = 0; c < numClasses; c++)
            {
                float s = output[0, 4 + c, a];
                if (s > bestScore) { bestScore = s; bestClass = c; }
            }

            if (bestScore < ConfidenceThreshold || bestClass < 0) continue;

            // Box: cx, cy, w, h in 640-space.
            float cx = output[0, 0, a] * scaleX;
            float cy = output[0, 1, a] * scaleY;
            float w  = output[0, 2, a] * scaleX;
            float h  = output[0, 3, a] * scaleY;

            var box = new SKRect(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2);
            candidates.Add((bestClass, bestScore, box));
        }

        // Non-maximum suppression per class.
        var byClass = candidates.GroupBy(c => c.ClassIdx);
        var kept = new List<DetectedSubject>();
        foreach (var grp in byClass)
        {
            var ordered = grp.OrderByDescending(c => c.Score).ToList();
            var suppressed = new bool[ordered.Count];
            for (int i = 0; i < ordered.Count; i++)
            {
                if (suppressed[i]) continue;
                var keep = ordered[i];
                kept.Add(new DetectedSubject(
                    Label: ClassLabel(grp.Key),
                    Confidence: keep.Score,
                    BoundingBox: keep.Box,
                    SegmentationMask: null));

                for (int j = i + 1; j < ordered.Count; j++)
                {
                    if (suppressed[j]) continue;
                    if (IoU(keep.Box, ordered[j].Box) > IoUThreshold)
                        suppressed[j] = true;
                }
            }
        }
        return kept;
    }

    private static float IoU(SKRect a, SKRect b)
    {
        float ix = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        float iy = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        float intersection = ix * iy;
        float areaA = a.Width * a.Height;
        float areaB = b.Width * b.Height;
        float union = areaA + areaB - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    /// <summary>COCO class labels (YOLOv8's default 80-class taxonomy).</summary>
    private static readonly string[] CocoLabels = new[]
    {
        "person","bicycle","car","motorcycle","airplane","bus","train","truck","boat",
        "traffic light","fire hydrant","stop sign","parking meter","bench","bird","cat",
        "dog","horse","sheep","cow","elephant","bear","zebra","giraffe","backpack",
        "umbrella","handbag","tie","suitcase","frisbee","skis","snowboard","sports ball",
        "kite","baseball bat","baseball glove","skateboard","surfboard","tennis racket",
        "bottle","wine glass","cup","fork","knife","spoon","bowl","banana","apple",
        "sandwich","orange","broccoli","carrot","hot dog","pizza","donut","cake","chair",
        "couch","potted plant","bed","dining table","toilet","tv","laptop","mouse",
        "remote","keyboard","cell phone","microwave","oven","toaster","sink","refrigerator",
        "book","clock","vase","scissors","teddy bear","hair drier","toothbrush",
    };

    private static string ClassLabel(int idx) =>
        idx >= 0 && idx < CocoLabels.Length ? CocoLabels[idx] : $"class_{idx}";

    public void Dispose()
    {
        _session?.Dispose();
        _sessionLock?.Dispose();
    }
}

/// <summary>Single detected subject.</summary>
public sealed record DetectedSubject(
    string Label,           // "person", "dog", "car", ...
    float Confidence,       // 0..1
    SKRect BoundingBox,     // in image pixel space
    SKPath? SegmentationMask);
