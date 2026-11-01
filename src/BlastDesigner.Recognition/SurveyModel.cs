using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BlastDesigner.Recognition;

/// <summary>
/// The survey segment classifier trained by ml/train.py (LightGBM exported to ONNX), embedded in this assembly and
/// loaded once on first use. The model names the features it takes and the feature version it was trained on; a model
/// that does not fit this program's <see cref="SurveyFeatures"/> is not used, and the recognition falls back to rules.
/// </summary>
public static class SurveyModel
{
    public const string Resource = "BlastDesigner.survey-segments.onnx";

    private sealed record Loaded(InferenceSession Session, string Input, string Output, int[] Columns, SurveyLineClass[] Classes, string Version);

    private static readonly Lazy<Loaded?> Model = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    private static string? _problem;

    /// <summary>The model loaded and fits the features of this program.</summary>
    public static bool Available => Model.Value != null;

    /// <summary>Why the model is not available (missing, another feature version, runtime error); null when it is.</summary>
    public static string? Problem => Model.Value == null ? _problem : null;

    /// <summary>Model version from its metadata, for the window and the log.</summary>
    public static string? Version => Model.Value?.Version;

    private static Loaded? Load()
    {
        try
        {
            using var stream = typeof(SurveyModel).Assembly.GetManifestResourceStream(Resource);
            if (stream == null) { _problem = "the model is not part of this build"; return null; }
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var session = new InferenceSession(ms.ToArray());
            var meta = session.ModelMetadata.CustomMetadataMap;
            string Meta(string key) => meta.TryGetValue(key, out var v) ? v : "";
            if (Meta("feature_version") != SurveyFeatures.Version.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                _problem = $"the model was trained on features version {Meta("feature_version")}, the program computes version {SurveyFeatures.Version}";
                session.Dispose();
                return null;
            }
            var names = Meta("features").Split(',', StringSplitOptions.RemoveEmptyEntries);
            var columns = names.Select(n => Array.IndexOf(SurveyFeatures.Names, n)).ToArray();
            if (columns.Length == 0 || columns.Any(c => c < 0))
            {
                _problem = "the model asks for features the program does not compute: " + string.Join(", ", names.Where(n => !SurveyFeatures.Names.Contains(n)));
                session.Dispose();
                return null;
            }
            var classes = Meta("classes").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Enum.Parse<SurveyLineClass>).ToArray();
            // outputs: the label and the class probabilities [n, classes]
            var output = session.OutputMetadata.First(o => o.Value.ElementType == typeof(float) && o.Value.Dimensions.Length == 2).Key;
            return new Loaded(session, session.InputMetadata.Keys.First(), output, columns, classes, Meta("model_version"));
        }
        catch (Exception ex)
        {
            _problem = ex.Message;
            return null;
        }
    }

    /// <summary>Class probabilities for feature rows (in the order of <see cref="SurveyFeatures.Names"/>), columns in <see cref="SurveyLineClass"/> order.</summary>
    public static float[][] Probabilities(IReadOnlyList<double[]> rows)
    {
        var m = Model.Value ?? throw new InvalidOperationException(_problem ?? "no model");
        if (rows.Count == 0) return Array.Empty<float[]>();
        var input = new DenseTensor<float>(new[] { rows.Count, m.Columns.Length });
        for (int i = 0; i < rows.Count; i++)
            for (int k = 0; k < m.Columns.Length; k++) input[i, k] = (float)rows[i][m.Columns[k]];
        using var results = m.Session.Run(new[] { NamedOnnxValue.CreateFromTensor(m.Input, input) }, new[] { m.Output });
        var p = results.First().AsTensor<float>();
        var classCount = Enum.GetValues<SurveyLineClass>().Length;
        var result = new float[rows.Count][];
        for (int i = 0; i < rows.Count; i++)
        {
            result[i] = new float[classCount];
            for (int c = 0; c < m.Classes.Length; c++) result[i][(int)m.Classes[c]] = p[i, c];
        }
        return result;
    }

    /// <summary>Class and confidence (the winner's lead over the runner-up, relative to its own probability) for each row.</summary>
    public static IReadOnlyList<(SurveyLineClass Class, double Confidence)> Classify(IReadOnlyList<double[]> rows) =>
        Probabilities(rows).Select(p =>
        {
            var order = Enumerable.Range(0, p.Length).OrderByDescending(i => p[i]).ToArray();
            var (best, second) = (p[order[0]], p[order[1]]);
            return ((SurveyLineClass)order[0], best <= 0 ? 0.0 : Math.Clamp((best - second) / (double)best, 0, 1));
        }).ToList();

    /// <summary>Recognition by the model when it is available and <paramref name="useModel"/>, otherwise by the rules.</summary>
    public static SurveyRecognition Recognize(IReadOnlyList<(PolylineEntity Line, string Layer)> lines, IReadOnlyList<PointEntity> points, IReadOnlyList<Vec3> collars, bool useModel = true) =>
        SurveyRecognizer.Recognize(lines, points, collars, classifier: useModel && Available ? Classify : null);
}
