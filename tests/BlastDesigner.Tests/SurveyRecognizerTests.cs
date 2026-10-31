using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.IO;

namespace BlastDesigner.Tests;

/// <summary>Recognition of crests and toes by rules, measured on synthetic surveys, and the hand-label file.</summary>
public class SurveyRecognizerTests
{
    private static (SurveyRecognition R, SurveyScene S) Run(int seed, SurveyTrap trap = SurveyTrap.None)
    {
        var s = SurveySynth.Generate(seed, trap: trap);
        var lines = s.Lines.Where(SurveyFeatures.IsSurveyLine).Select(l => (l, s.Layers[l.Id])).ToList();
        return (SurveyRecognizer.Recognize(lines, s.Points.ToList(), s.Collars), s);
    }

    /// <summary>A floor for the rules (85 % by length on the training distribution when measured): a change that drops below it broke something.</summary>
    [Fact]
    public void Rules_GetMostOfTheLengthRight()
    {
        double right = 0, all = 0, crestRight = 0, crestAll = 0;
        for (int seed = 300; seed < 330; seed++)
        {
            var (r, s) = Run(seed);
            foreach (var g in r.Segments)
            {
                var line = s.Lines.First(l => l.Id == g.Segment.LineId);
                var len = SurveyRecognition.Length(line.Points, g.Segment);
                var truth = s.LineClasses[line.Id];
                all += len;
                if (truth == g.Class) right += len;
                if (truth == SurveyLineClass.Crest) { crestAll += len; if (g.Class == SurveyLineClass.Crest) crestRight += len; }
            }
        }
        Assert.True(right / all > 0.78, $"accuracy {right / all:P1}");
        Assert.True(crestRight / crestAll > 0.72, $"crest recall {crestRight / crestAll:P1}");
    }

    [Fact]
    public void Benches_HaveTheGeneratedHeights()
    {
        var errors = new List<double>();
        for (int seed = 400; seed < 415; seed++)
        {
            var (r, s) = Run(seed);
            foreach (var b in r.Benches.Where(b => b.Toe != null))
                errors.Add(s.Benches.Min(x => Math.Abs(x.Height - b.Height)));
        }
        Assert.NotEmpty(errors);
        Assert.True(SurveyFeatures.Median(errors) < 1, $"median bench height error {SurveyFeatures.Median(errors):0.00} m");
    }

    [Fact]
    public void Contours_AreRecognizedByTheirSingleElevation()
    {
        var (r, s) = Run(7, SurveyTrap.ContoursAlongWall);
        var contours = r.Segments.Where(g => s.LineClasses[g.Segment.LineId] == SurveyLineClass.Contour).ToList();
        Assert.NotEmpty(contours);
        Assert.True(contours.Count(g => g.Class == SurveyLineClass.Contour) > contours.Count * 0.9);
    }

    [Fact]
    public void Refresh_FollowsAChangedClass()
    {
        var (r, s) = Run(13);
        var lines = s.Lines.ToList();
        var crestLine = r.Benches.First().Crest;
        for (int i = 0; i < r.Segments.Count; i++)
            if (r.Segments[i].Segment.LineId == crestLine) r.Segments[i] = r.Segments[i] with { Class = SurveyLineClass.Other };
        SurveyRecognizer.Refresh(r, lines, s.Points.ToList());
        Assert.DoesNotContain(r.Benches, b => b.Crest == crestLine);
    }

    [Fact]
    public void Labels_SaveAndLoad_WithoutCoordinates()
    {
        var (r, s) = Run(21);
        var byId = s.Lines.ToDictionary(l => l.Id);
        var path = Path.Combine(Path.GetTempPath(), $"bd_labels_{Guid.NewGuid():N}.json");
        try
        {
            SurveyLabels.Save(path, SurveyLabels.From(r, byId));
            var text = File.ReadAllText(path);
            var x = s.Lines.First().Points[0].X.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
            Assert.DoesNotContain(x, text);
            var back = SurveyLabels.Load(path);
            Assert.Equal(r.Segments.Count, back.Count);
            var key = SurveyLabels.Key(s.Lines.First(l => l.Id == r.Segments[0].Segment.LineId));
            Assert.Equal(r.Segments[0].Class, back.First(l => l.Line == key && l.From == r.Segments[0].Segment.From).Class);
        }
        finally { File.Delete(path); }
    }
}
