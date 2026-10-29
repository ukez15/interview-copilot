using Whisper.net.Ggml;

namespace InterviewCopilot.Infrastructure.Transcription;

/// <summary>A downloadable ggml model: its type, quantization and about how big it is (null when not shown).</summary>
internal readonly record struct GgmlModel(GgmlType Type, QuantizationType Quantization, long? ApproximateBytes = null);

/// <summary>Maps model file names to downloadable ggml models.</summary>
internal static class WhisperModelCatalog
{
    /// <summary>The more accurate, slower choice offered next to the configured default (#85).</summary>
    public const string AccurateModel = "ggml-large-v3-turbo-q5_0.bin";

    private static readonly Dictionary<string, GgmlModel> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ggml-tiny.en.bin"] = new(GgmlType.TinyEn, QuantizationType.NoQuantization),
        ["ggml-base.en.bin"] = new(GgmlType.BaseEn, QuantizationType.NoQuantization),
        ["ggml-small.en.bin"] = new(GgmlType.SmallEn, QuantizationType.NoQuantization, 488_000_000),
        ["ggml-medium.en.bin"] = new(GgmlType.MediumEn, QuantizationType.NoQuantization),
        [AccurateModel] = new(GgmlType.LargeV3Turbo, QuantizationType.Q5_0, 574_000_000),
    };

    public static bool TryGet(string modelFileName, out GgmlModel model) =>
        Known.TryGetValue(modelFileName, out model);
}
