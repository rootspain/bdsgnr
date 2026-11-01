using Avalonia;

namespace BlastDesigner.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var i = Array.IndexOf(args, "--selftest");
        if (i >= 0) return SelfTest.Run(i + 1 < args.Length ? args[i + 1] : Path.Combine(Path.GetTempPath(), "BlastDesigner-selftest.txt"));
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // A DXGI swap chain hands frames to Windows directly. The default WinUI composition copies every frame
            // through an extra surface; with a blast over a survey that cost about half the frame rate. The program
            // has no transparent windows, which the swap chain cannot show. The other modes are fallbacks, in order.
            .With(new Win32PlatformOptions
            {
                CompositionMode = new[] { Win32CompositionMode.LowLatencyDxgiSwapChain, Win32CompositionMode.WinUIComposition, Win32CompositionMode.RedirectionSurface }
            })
            .WithInterFont()
            .LogToTrace();
}

/// <summary>
/// Checks a published build (trimmed or single-file) without opening a window: everything that goes through JSON or
/// reflection — project save and load, new holes (undo snapshot), settings, clipboard, dialog copies, import templates,
/// the property grid — must work. Results go to a text file (a windowed exe has no console). Exit code 0 = passed.
/// </summary>
internal static class SelfTest
{
    public static int Run(string reportPath)
    {
        var lines = new List<string>();
        var failures = 0;
        void Check(string name, Action a)
        {
            try { a(); lines.Add("OK   " + name); }
            catch (Exception ex) { failures++; lines.Add($"FAIL {name}: {ex.GetType().Name}: {ex.Message}"); }
        }
        var dir = Path.Combine(Path.GetTempPath(), "BlastDesigner-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var p = Domain.Model.BlastProject.CreateNew("Self test");
        Check("new hole with undo snapshot", () =>
        {
            var doc = new Domain.Document(p);
            doc.Execute("Add hole", pr => pr.Add(new Domain.Model.Hole { Label = "1", Collar = new Domain.Geometry.Vec3(1, 2, 3), Length = 10 }));
            doc.Undo();
            if (doc.Project.Holes.Any()) throw new Exception("undo did not remove the hole");
            doc.Redo();
            if (doc.Project.Holes.Count() != 1) throw new Exception("redo did not restore the hole");
            p = doc.Project;
        });
        p.Add(new Domain.Model.PolylineEntity { Points = { new(0, 0, 0), new(5, 0, 0), new(5, 5, 0) }, Closed = true });
        p.InitiationRules.Add(new Domain.Model.InitiationRule { Name = "R" });
        Check("project save and open", () =>
        {
            var path = Path.Combine(dir, "t" + IO.ProjectFile.Extension);
            IO.ProjectFile.Save(p, path, 0);
            var back = IO.ProjectFile.Load(path);
            if (back.Holes.Count() != 1 || back.Entities.Count != p.Entities.Count || back.InitiationRules.Count != 1 || back.Layers.Count != p.Layers.Count)
                throw new Exception("content lost on the way through the file");
            if (back.Holes.First().Collar.Z != 3) throw new Exception("hole coordinates lost");
        });
        Check("clipboard", () =>
        {
            var json = Domain.ProjectJson.SerializeEntities(p.Entities);
            if (Domain.ProjectJson.DeserializeEntities(json).Count != p.Entities.Count) throw new Exception("entities lost");
        });
        Check("settings file", () =>
        {
            var s = Domain.ProjectJson.DeepClone(new IO.ProgramSettings { Author = "x" });
            if (s.Author != "x") throw new Exception("settings lost");
        });
        Check("dialog copy", () =>
        {
            var c = Domain.ProjectJson.DeepClone(new ViewModels.MainViewModel.CurvedLayoutParameters { Rows = 4 });
            if (c.Rows != 4) throw new Exception("value lost");
        });
        Check("import template", () =>
        {
            var path = Path.Combine(dir, "fmt.json");
            new IO.TextImportFormat().Save(path);
            IO.TextImportFormat.Load(path);
        });
        Check("release notes resource", () =>
        {
            if (IO.Changelog.Load("ru").Count == 0 || IO.Changelog.Load("en").Count == 0) throw new Exception("release notes missing");
        });
        Check("survey model", () =>
        {
            if (!Recognition.SurveyModel.Available) throw new Exception(Recognition.SurveyModel.Problem ?? "not loaded");
            var answer = Recognition.SurveyModel.Classify(new[] { new double[Engine.SurveyFeatures.Names.Length] });
            if (answer.Count != 1) throw new Exception("no answer");
        });
        Check("property grid reflection", () =>
        {
            var props = typeof(Domain.Model.Hole).GetProperties().Select(x => x.Name).ToList();
            if (!props.Contains("Diameter") || !props.Contains("Collar")) throw new Exception("hole properties trimmed away");
        });
        try { Directory.Delete(dir, true); } catch { }
        lines.Add(failures == 0 ? "SELFTEST PASSED" : $"SELFTEST FAILED ({failures})");
        File.WriteAllLines(reportPath, lines);
        return failures == 0 ? 0 : 1;
    }
}
