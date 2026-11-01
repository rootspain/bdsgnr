using System.Text.Json;
using BlastDesigner.Engine;
using BlastDesigner.Recognition;

namespace BlastDesigner.Tests;

/// <summary>The embedded ONNX model: it loads, gives what Python gave, and beats the rules on synthetic surveys.</summary>
public class SurveyModelTests
{
    [Fact]
    public void Model_LoadsAndFitsTheFeatures()
    {
        Assert.True(SurveyModel.Available, SurveyModel.Problem);
        Assert.Null(SurveyModel.Problem);
    }

    /// <summary>The same rows through ONNX Runtime in C# and in Python (ml/models/parity.json): feature order, version and runtime agree.</summary>
    [Fact]
    public void Model_GivesWhatPythonGave()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "parity.json")));
        var names = doc.RootElement.GetProperty("features").EnumerateArray().Select(e => e.GetString()!).ToList();
        var rows = new List<double[]>();
        var expected = new List<double[]>();
        foreach (var row in doc.RootElement.GetProperty("rows").EnumerateArray())
        {
            var full = new double[SurveyFeatures.Names.Length];
            var x = row.GetProperty("x").EnumerateArray().Select(e => e.GetDouble()).ToList();
            for (int k = 0; k < names.Count; k++) full[Array.IndexOf(SurveyFeatures.Names, names[k])] = x[k];
            rows.Add(full);
            expected.Add(row.GetProperty("p").EnumerateArray().Select(e => e.GetDouble()).ToArray());
        }
        var p = SurveyModel.Probabilities(rows);
        for (int i = 0; i < rows.Count; i++)
            for (int c = 0; c < expected[i].Length; c++)
                Assert.True(Math.Abs(p[i][c] - expected[i][c]) < 1e-4, $"row {i} class {c}: {p[i][c]} vs {expected[i][c]}");
    }

    [Fact]
    public void Model_BeatsTheRulesOnSyntheticSurveys()
    {
        double model = 0, rules = 0, all = 0;
        for (int seed = 700; seed < 715; seed++)
        {
            var s = SurveySynth.Generate(seed, SurveySynthOptions.Shifted);
            var lines = s.Lines.Where(SurveyFeatures.IsSurveyLine).Select(l => (l, s.Layers[l.Id])).ToList();
            var points = s.Points.ToList();
            var byModel = SurveyModel.Recognize(lines, points, s.Collars);
            var byRules = SurveyModel.Recognize(lines, points, s.Collars, useModel: false);
            Assert.True(byModel.FromModel);
            Assert.False(byRules.FromModel);
            foreach (var (g, r) in byModel.Segments.Zip(byRules.Segments))
            {
                var line = lines.First(l => l.l.Id == g.Segment.LineId).l;
                var len = SurveyRecognition.Length(line.Points, g.Segment);
                var truth = s.LineClasses[line.Id];
                all += len;
                if (g.Class == truth) model += len;
                if (r.Class == truth) rules += len;
            }
        }
        Assert.True(model / all > 0.85, $"model {model / all:P1}");
        Assert.True(model > rules, $"model {model / all:P1}, rules {rules / all:P1}");
    }
}
