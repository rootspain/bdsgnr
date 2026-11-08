using System.Collections.Concurrent;
using BlastDesigner.Calc;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>
/// Движок вычислений для Engine и окон: встроенные алгоритмы (ячейки Вороного, сетка, МВЗ, время, расстояние до
/// линии, прогноз сейсмики) регистрируются в <see cref="CalcRegistry.Default"/>, встроенный профиль «SHOTPlus 6»
/// читается из ресурса. Профиль проекта — копия из <see cref="BlastProject.Calc"/>, без неё — встроенный.
/// </summary>
public static class CalcService
{
    public const string BuiltInName = "SHOTPlus 6";

    private static readonly ConcurrentDictionary<string, CalcModel> Models = new();
    private static readonly object Gate = new();
    private static CalcModel? _builtInModel;

    static CalcService()
    {
        Register(CalcRegistry.Default);
        using var s = typeof(CalcService).Assembly.GetManifestResourceStream("ShotPlus6.bdcalc")
                      ?? throw new InvalidOperationException("Нет ресурса ShotPlus6.bdcalc");
        using var r = new StreamReader(s);
        BuiltIn = CalcProfile.FromJson(r.ReadToEnd());
        Library.Add(BuiltIn);
    }

    /// <summary>Встроенный профиль «SHOTPlus 6» (только для чтения).</summary>
    public static CalcProfile BuiltIn { get; }

    /// <summary>Профили по имени: встроенный и загруженные из папки настроек (<see cref="CalcProfileLibrary.LoadFolder"/>).</summary>
    public static CalcProfileLibrary Library { get; } = new();

    public static CalcRegistry Registry => CalcRegistry.Default;

    /// <summary>Скомпилированный встроенный профиль.</summary>
    public static CalcModel BuiltInModel
    {
        get
        {
            var m = _builtInModel;
            if (m != null && m.RegistryRevision == Registry.Revision) return m;
            lock (Gate)
            {
                if (_builtInModel == null || _builtInModel.RegistryRevision != Registry.Revision)
                    _builtInModel = new CalcModel(Registry, BuiltIn.Flatten(Library.Find));
                return _builtInModel;
            }
        }
    }

    /// <summary>Компилирует профиль (с наследованием из библиотеки), с кэшем по хешу.</summary>
    public static CalcModel Compile(CalcProfile profile)
    {
        if (ReferenceEquals(profile, BuiltIn)) return BuiltInModel;
        var flat = profile.Base != null ? profile.Flatten(Library.Find) : profile;
        var key = flat.Hash() + "@" + Registry.Revision;
        return Models.GetOrAdd(key, _ => new CalcModel(Registry, flat));
    }

    /// <summary>Модель профиля проекта; испорченная копия профиля — встроенный.</summary>
    public static CalcModel ModelFor(BlastProject project)
    {
        var c = project.Calc;
        if (c == null) return BuiltInModel;
        if (!string.IsNullOrEmpty(c.ProfileJson))
        {
            var key = c.ProfileHash + "@" + Registry.Revision + "#" + c.ProfileJson.Length;
            if (Models.TryGetValue(key, out var cached)) return cached;
            try { return Models.GetOrAdd(key, _ => new CalcModel(Registry, CalcProfile.FromJson(c.ProfileJson!))); }
            catch (Exception) { return BuiltInModel; }
        }
        return Library.Find(c.ProfileName) is { } p ? Compile(p) : BuiltInModel;
    }

    /// <summary>Записывает в проект копию разложенного профиля и её хеш (встроенный — только имя).</summary>
    public static void SetProfile(BlastProject project, CalcProfile profile)
    {
        if (ReferenceEquals(profile, BuiltIn) || (profile.Name == BuiltInName && profile.ReadOnly))
        {
            if (project.Calc is { } existing) { existing.ProfileName = BuiltInName; existing.ProfileJson = null; existing.ProfileHash = BuiltIn.Hash(); existing.ProfileVersion = BuiltIn.Version; }
            return;
        }
        var flat = profile.Base != null ? profile.Flatten(Library.Find) : profile;
        project.Calc ??= new ProjectCalc();
        project.Calc.ProfileName = profile.Name;
        project.Calc.ProfileVersion = profile.Version;
        project.Calc.ProfileJson = flat.ToJson();
        project.Calc.ProfileHash = flat.Hash();
    }

    /// <summary>Сеанс над проектом по его профилю, с параметрами и ручными значениями проекта.</summary>
    public static CalcSession Session(BlastProject project, CalcModel? model = null)
    {
        var s = new CalcSession(model ?? ModelFor(project), project);
        if (project.Calc is { } c)
        {
            if (c.Parameters != null) foreach (var (k, v) in c.Parameters) s.BlastParameters[k] = v;
            if (c.HoleTypeParameters != null) foreach (var (t, d) in c.HoleTypeParameters) s.HoleTypeParameters[t] = new(d);
            if (c.Overrides is { Count: > 0 } o) s.SetOverrides(o);
        }
        return s;
    }

    /// <summary>
    /// Ячейки для итогов по части скважин — как в Quantities.Compute до движка: всегда по всем скважинам взрыва (если
    /// часть из них не в проекте — по самой части), граница — заданная или граница взрыва.
    /// </summary>
    public static void ConfigureCells(CalcSession session, IEnumerable<Hole>? subset, IReadOnlyList<Vec3>? boundary)
    {
        var project = session.Project;
        var real = (subset ?? project.Holes).Where(h => !h.IsDummy).ToList();
        var allReal = project.Holes.Where(h => !h.IsDummy).ToList();
        var inProject = allReal.ToHashSet();
        if (allReal.Count == 0 || real.Any(h => !inProject.Contains(h))) allReal = real;
        session.Scope.BuiltinArgs["cellArea.holes"] = allReal;
        session.Scope.BuiltinArgs["cellArea.boundary"] = boundary ?? Quantities.DefaultBoundary(project, allReal);
    }

    // ---- встроенные алгоритмы -------------------------------------------

    private static void Register(CalcRegistry r)
    {
        r.Input<BlastProject, double>("blast.linesKey", (_, p) => LinesKey(p), "1", "Отпечаток линий (граница, бровка)", "", "Entities");

        r.AddBuiltin(new BuiltinDef
        {
            Name = "cellArea", NameRu = "Площадь ячейки Вороного", Contexts = new[] { "hole" }, Unit = "m2",
            Description = "Ячейка Вороного скважины по всем скважинам взрыва, обрезанная границей (или контуром сетки + ½ Л.С.П.); вне ячеек — Л.С.П. × расстояние.",
            Inputs = new[] { "hole.x", "hole.y", "hole.isDummy", "blast.burden", "blast.spacing", "blast.linesKey" },
            Outputs = new[] { "hole.area" }, Settings = new[] { "cellArea.holes", "cellArea.boundary" },
            Prepare = s =>
            {
                var holes = s.Scope.BuiltinArgs.GetValueOrDefault("cellArea.holes") as IReadOnlyList<Hole> ?? s.Project.Holes.Where(h => !h.IsDummy).ToList();
                var boundary = s.Scope.BuiltinArgs.GetValueOrDefault("cellArea.boundary") as IReadOnlyList<Vec3> ?? Quantities.DefaultBoundary(s.Project, holes);
                return Quantities.VoronoiAreas(holes, boundary);
            },
            Value = (s, prepared, o, _) => ((Dictionary<Guid, double>)prepared!).TryGetValue(((Hole)o).Id, out var a) ? a : s.Project.Design.Burden * s.Project.Design.Spacing
        });
        r.AddBuiltin(new BuiltinDef
        {
            Name = "patternBurden", NameRu = "Средняя Л.С.П. по рядам", Contexts = new[] { "blast" }, Unit = "m",
            Inputs = new[] { "hole.x", "hole.y", "hole.isDummy", "row.holes", "blast.burden", "blast.spacing" },
            Outputs = new[] { "blast.avgBurden" },
            Prepare = s => Quantities.PatternEstimate(s.Project),
            Value = (_, prepared, _, _) => (((double Burden, double Spacing))prepared!).Burden
        });
        r.AddBuiltin(new BuiltinDef
        {
            Name = "patternSpacing", NameRu = "Среднее расстояние в рядах", Contexts = new[] { "blast" }, Unit = "m",
            Inputs = new[] { "hole.x", "hole.y", "hole.isDummy", "row.holes", "blast.burden", "blast.spacing" },
            Outputs = new[] { "blast.avgSpacing" },
            Prepare = s => Quantities.PatternEstimate(s.Project),
            Value = (_, prepared, _, _) => (((double Burden, double Spacing))prepared!).Spacing
        });
        r.AddBuiltin(new BuiltinDef
        {
            Name = "mic", NameRu = "Максимальный взрываемый заряд", Contexts = new[] { "blast" }, Unit = "kg",
            Description = "Наибольшая масса ВВ скважин, сработавших в окне МВЗ (огибающая по времени); по всем скважинам проекта.",
            Inputs = new[] { "hole.firingTimeMs", "hole.isDummy", "hole.chargeMass", "param:site.vib.window" },
            Outputs = new[] { "blast.mic" },
            Prepare = s =>
            {
                var window = s.ParameterValue("site.vib.window") ?? s.Project.Vibration.WindowMs;
                return Timing.TimeEnvelope(s.Project, window, h => s.Number("hole.chargeMass", h)).Select(e => e.ChargeKg).DefaultIfEmpty(0).Max();
            },
            Value = (_, prepared, _, _) => (double)prepared!
        });
        r.AddBuiltin(new BuiltinDef
        {
            Name = "firingTime", NameRu = "Время срабатывания (Timing.Solve)", Contexts = new[] { "hole" }, Unit = "ms",
            Description = "Решает схему замедлений взрыва и возвращает время срабатывания скважины; без инициирования — пусто.",
            Inputs = new[] { "tie.delayMs", "tie.from", "tie.to", "tie.product", "hole.electronicTimeMs", "primer.delayMs", "primer.detonator", "product.delayMs", "hole.x", "hole.y" },
            Prepare = s => { Timing.Solve(s.Project); return null; },
            Value = (_, _, o, _) => ((Hole)o).FiringTimeMs ?? double.NaN
        });
        r.AddBuiltin(new BuiltinDef
        {
            Name = "distToLine", NameRu = "Расстояние до линии", Contexts = new[] { "hole", "monitor" }, Unit = "m", ArgCount = 1,
            Description = "Расстояние в плане от устья до первой линии вида (Crest, Toe, Boundary…); нет линии — пусто.",
            Inputs = new[] { "hole.x", "hole.y", "blast.linesKey" },
            Prepare = _ => new Dictionary<string, PolylineEntity?>(StringComparer.OrdinalIgnoreCase),
            Value = (s, prepared, o, args) =>
            {
                var cache = (Dictionary<string, PolylineEntity?>)prepared!;
                var kindName = args[0].ToString()!;
                if (!cache.TryGetValue(kindName, out var line))
                {
                    if (!Enum.TryParse<LineKind>(kindName, true, out var kind)) throw new CalcEvaluationException($"Нет вида линии «{kindName}»");
                    cache[kindName] = line = s.Project.Entities.OfType<PolylineEntity>().FirstOrDefault(l => l.Kind == kind);
                }
                if (line == null) return double.NaN;
                var p = o switch { Hole h => h.Collar, PointEntity pt => pt.Position, _ => throw new CalcEvaluationException("distToLine: нет точки") };
                return GeoMath.DistanceToPolyline2D(line.Points, p, line.Closed);
            }
        });
        r.AddBuiltin(new BuiltinDef
        {
            Name = "nearestChargeDistance", NameRu = "Расстояние до ближайшего заряда", Contexts = new[] { "monitor" }, Unit = "m",
            Inputs = new[] { "hole.x", "hole.y", "hole.z", "hole.isDummy", "hole.chargeMass" },
            Prepare = s => s.Project.Holes.Where(h => !h.IsDummy && s.Number("hole.chargeMass", h) > 0).Select(h => h.Collar).ToList(),
            Value = (_, prepared, o, _) =>
            {
                var collars = (List<Vec3>)prepared!;
                var p = ((PointEntity)o).Position;
                return collars.Count == 0 ? double.NaN : collars.Min(c => Vec3.Distance(c, p));
            }
        });
        r.AddBuiltin(new BuiltinDef
        {
            Name = "predictPpv", NameRu = "Прогноз скорости колебаний по окнам", Contexts = new[] { "monitor" }, Unit = "?",
            Inputs = new[] { "hole.x", "hole.y", "hole.z", "hole.isDummy", "hole.chargeMass", "hole.firingTimeMs", "param:site.vib.K", "param:site.vib.B", "param:site.vib.window" },
            Value = (s, _, o, _) => Vibration.Predict(s.Project, ((PointEntity)o).Position).Ppv
        });
        r.AddFunction(new FunctionDef
        {
            Name = "studentT", ArgCount = 2, Unit = "1", Description = "Квантиль распределения Стьюдента (p, степени свободы)",
            Invoke = a => Stats.StudentTQuantile(a[0], (int)a[1])
        });

        void Bind(string id, Func<BlastProject, double> get) => r.BindParameter(id, s => get(s.Project));
        Bind("site.vib.K", p => p.Vibration.SiteK);
        Bind("site.vib.B", p => p.Vibration.SiteB);
        Bind("site.vib.airK", p => p.Vibration.AirK);
        Bind("site.vib.airB", p => p.Vibration.AirB);
        Bind("site.vib.window", p => p.Vibration.WindowMs);
        Bind("site.vib.ppvLimit", p => p.Vibration.PpvLimit);
        Bind("site.vib.airblastLimit", p => p.Vibration.AirblastLimitDb);
        Bind("site.flyrock.K", p => p.Flyrock.SiteK);
        Bind("site.flyrock.safetyFactor", p => p.Flyrock.SafetyFactor);
        Bind("site.flyrock.minClearance", p => p.Flyrock.MinimumClearance);
        Bind("site.frag.rockFactor", p => p.Fragmentation.RockFactor);
        Bind("site.frag.rockFactorSet", p => p.Fragmentation.RockFactorSet ? 1 : 0);
        Bind("site.frag.rockMassDescription", p => p.Fragmentation.RockMassDescription);
        Bind("site.frag.jointFactor", p => p.Fragmentation.JointFactor);
        Bind("site.frag.ucs", p => p.Fragmentation.Ucs);
        Bind("site.frag.youngsModulus", p => p.Fragmentation.YoungsModulus);
        Bind("site.frag.drillDeviation", p => p.Fragmentation.DrillDeviation);
        Bind("site.frag.oversize", p => p.Fragmentation.OversizeCm);
        Bind("site.frag.fines", p => p.Fragmentation.FinesCm);
    }

    /// <summary>Отпечаток линий проекта (вид и точки): меняется, когда правят границу или бровку.</summary>
    private static double LinesKey(BlastProject p)
    {
        var h = new HashCode();
        foreach (var l in p.Entities.OfType<PolylineEntity>())
        {
            h.Add(l.Kind);
            h.Add(l.Closed);
            foreach (var v in l.Points) { h.Add(v.X); h.Add(v.Y); }
        }
        return h.ToHashCode();
    }
}
