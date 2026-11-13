using System.Collections.Concurrent;
using System.Globalization;
using BlastDesigner.Calc;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>Сводка значения по скважинам: текст одного значения или «разные» с суммой либо средним, и выход за диапазон.</summary>
public sealed record HoleValueSummary(string Text, bool Different, string? Aggregate, bool IsSum, bool OutOfRange);

/// <summary>
/// Движок вычислений для Engine и окон: встроенные алгоритмы (ячейки Вороного, сетка, МВЗ, время, расстояние до
/// линии, прогноз сейсмики) регистрируются в <see cref="CalcRegistry.Default"/>, встроенный профиль «Formulas»
/// читается из ресурса. Профиль проекта — копия из <see cref="BlastProject.Calc"/>, без неё — встроенный.
/// </summary>
public static class CalcService
{
    public const string BuiltInName = "Formulas";

    private static readonly ConcurrentDictionary<string, CalcModel> Models = new();
    private static readonly object Gate = new();
    private static CalcModel? _builtInModel;

    static CalcService()
    {
        Register(CalcRegistry.Default);
        using var s = typeof(CalcService).Assembly.GetManifestResourceStream("Formulas.bdcalc")
                      ?? throw new InvalidOperationException("Нет ресурса Formulas.bdcalc");
        using var r = new StreamReader(s);
        BuiltIn = CalcProfile.FromJson(r.ReadToEnd());
        Library.Add(BuiltIn);
    }

    /// <summary>Встроенный профиль «Formulas» (только для чтения).</summary>
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

    // ---- публикация и история -------------------------------------------

    /// <summary>Контрольные примеры профиля, которые на этом проекте не сходятся (или не считаются).</summary>
    public static List<TestCaseDef> FailedTests(CalcSession session)
    {
        var failed = new List<TestCaseDef>();
        foreach (var t in session.Model.Profile.Tests)
        {
            CalcValue v;
            try { v = session.Evaluate(session.Model.CompileExpression("blast", t.Expression), session.Project); }
            catch (CalcSyntaxException e) { v = CalcValue.Fail(e.Message); }
            if (v.IsError || Math.Abs(v.Number - t.Expected) > t.Tolerance) failed.Add(t);
        }
        return failed;
    }

    /// <summary>Почему профиль нельзя опубликовать (затирает встроенный, ошибки формул, несошедшиеся примеры); null — можно.</summary>
    public static string? PublishProblem(BlastProject project, CalcProfile profile)
    {
        if (string.Equals(profile.Name, BuiltInName, StringComparison.OrdinalIgnoreCase) && !ReferenceEquals(profile, BuiltIn))
            return $"Профиль «{BuiltInName}» только для чтения: опубликуйте изменения под другим именем.";
        var model = new CalcModel(Registry, profile.Base != null ? profile.Flatten(Library.Find) : profile);
        if (model.Diagnostics.FirstOrDefault(d => !d.IsWarning) is { } error) return "Ошибка в формуле: " + error;
        var failed = FailedTests(Session(project, model));
        return failed.Count > 0 ? "Не сходятся контрольные примеры: " + string.Join(", ", failed.Select(t => t.Name)) : null;
    }

    /// <summary>
    /// Записывает профиль в проект и в его историю (с причиной). Первая публикация сначала заносит в историю прежнее
    /// состояние, чтобы к нему можно было вернуться. Версия, совпавшая с последней, увеличивается.
    /// Без отличий от встроенного записывается сам встроенный. Проверка — <see cref="PublishProblem"/>, она делается до правки.
    /// </summary>
    public static void Publish(BlastProject project, CalcProfile profile, string reason, string author, DateTime now)
    {
        var c = project.Calc ??= new ProjectCalc { ProfileName = BuiltInName, ProfileVersion = BuiltIn.Version, ProfileHash = BuiltIn.Hash() };
        var history = c.History ??= new();
        if (history.Count == 0) history.Add(Snapshot(c, "Исходное состояние", author, now));
        if (history[^1].Version == profile.Version) profile.Version = NextVersion(profile.Version);
        var unchanged = profile.Base == BuiltInName && profile.Formulas.Count == 0 && profile.Parameters.Count == 0 && profile.Properties.Count == 0 && profile.Tests.Count == 0;
        SetProfile(project, unchanged ? BuiltIn : profile);
        history.Add(Snapshot(c, reason, author, now));
    }

    /// <summary>Возвращает проект к версии из истории; сам откат тоже остаётся в истории.</summary>
    public static void Rollback(BlastProject project, ProfileVersion version, string author, DateTime now)
    {
        var c = project.Calc ?? throw new InvalidOperationException("У проекта нет истории профиля");
        c.ProfileName = version.ProfileName;
        c.ProfileVersion = version.Version;
        c.ProfileJson = version.ProfileJson;
        c.ProfileHash = version.ProfileHash;
        (c.History ??= new()).Add(Snapshot(c, $"Откат к версии {version.Version}", author, now));
    }

    private static ProfileVersion Snapshot(ProjectCalc c, string reason, string author, DateTime now) => new()
    {
        Version = c.ProfileVersion, Published = now, Author = author, Reason = reason, ProfileName = c.ProfileName, ProfileJson = c.ProfileJson, ProfileHash = c.ProfileHash
    };

    private static string NextVersion(string v) =>
        int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? (n + 1).ToString(CultureInfo.InvariantCulture) : v + ".1";

    /// <summary>Значение свойства сущности вышло за разумный диапазон свойства.</summary>
    public static bool IsOutOfRange(BlastProject project, string propertyId, object entity) =>
        ModelFor(project).FindProperty(propertyId) is { } d && (d.Min != null || d.Max != null)
        && Value(project, propertyId, entity) is { IsError: false, Object: null } v && d.OutOfRange(v.Number);

    // ---- долгий сеанс документа -----------------------------------------

    private static readonly List<WeakReference<LiveCalc>> LiveSessions = new();

    /// <summary>Заводит долгий сеанс открытого документа (один на документ; повторный вызов возвращает тот же).</summary>
    public static LiveCalc Attach(Document doc)
    {
        lock (LiveSessions)
        {
            LiveSessions.RemoveAll(w => !w.TryGetTarget(out var l) || l.Document == null);
            foreach (var w in LiveSessions)
                if (w.TryGetTarget(out var l) && ReferenceEquals(l.Document, doc)) return l;
            var live = new LiveCalc(doc);
            LiveSessions.Add(new WeakReference<LiveCalc>(live));
            return live;
        }
    }

    /// <summary>Долгий сеанс документа с этим проектом, готовый отвечать; null — считать коротким сеансом.</summary>
    public static CalcSession? Live(BlastProject project)
    {
        lock (LiveSessions)
        {
            for (int i = 0; i < LiveSessions.Count; i++)
                if (LiveSessions[i].TryGetTarget(out var l) && l.SessionFor(project) is { } s) return s;
        }
        return null;
    }

    /// <summary>Модель проекта изменилась мимо документа (времена после Timing.Solve): долгие сеансы синхронизируются заново.</summary>
    public static void Touch(BlastProject project)
    {
        lock (LiveSessions)
            foreach (var w in LiveSessions)
                if (w.TryGetTarget(out var l) && ReferenceEquals(l.Document?.Project, project)) l.Touch();
    }

    /// <summary>Сеанс для чтения значения сущности: долгий, если он знает сущность, иначе короткий.</summary>
    public static CalcSession SessionFor(BlastProject project, string context, object entity) =>
        Live(project) is { } s && s.Knows(context, entity) ? s : Session(project);

    /// <summary>Значение свойства (<c>hole.volume</c>) сущности проекта.</summary>
    public static CalcValue Value(BlastProject project, string propertyId, object entity)
    {
        var dot = propertyId.IndexOf('.');
        var context = dot > 0 ? propertyId[..dot] : "";
        return SessionFor(project, context, entity).Value(propertyId, entity);
    }

    /// <summary>Названия свойств по-русски (окна, подписи плана, отчёты); иначе по-английски. Задаёт приложение по языку.</summary>
    public static bool RussianNames { get; set; } = true;

    /// <summary>Название свойства для столбца, подписи или отчёта; без названия — имя свойства.</summary>
    public static string Caption(BlastProject project, string propertyId)
    {
        var d = ModelFor(project).FindProperty(propertyId);
        if (d == null) return propertyId;
        var name = RussianNames ? (d.NameRu.Length > 0 ? d.NameRu : d.NameEn) : (d.NameEn.Length > 0 ? d.NameEn : d.NameRu);
        return name.Length > 0 ? name : d.Name;
    }

    /// <summary>Единица свойства для вывода: пусто для безразмерных и неизвестных.</summary>
    public static string UnitText(BlastProject project, string propertyId) =>
        ModelFor(project).FindProperty(propertyId)?.Unit is { } u && u is not ("?" or "1" or "" or "-") ? u.Replace("m3", "m³").Replace("m2", "m²") : "";

    /// <summary>Значение свойства текстом по его формату (с единицей); ошибка — «#».</summary>
    public static string Format(BlastProject project, string propertyId, object entity, bool withUnit = true)
    {
        var dot = propertyId.IndexOf('.');
        return Format(SessionFor(project, dot > 0 ? propertyId[..dot] : "", entity), propertyId, entity, withUnit);
    }

    /// <summary>То же в готовом сеансе: много значений одного расчёта (поля отчёта) считают ячейки и МВЗ один раз.</summary>
    public static string Format(CalcSession session, string propertyId, object entity, bool withUnit = true)
    {
        var project = session.Project;
        var v = session.Value(propertyId, entity);
        if (v.IsError) return "#";
        var d = ModelFor(project).FindProperty(propertyId);
        var text = v.Object switch
        {
            bool b => b ? "✓" : "—",
            string s => s,
            _ => v.Number.ToString(d?.Format is { Length: > 0 } f ? f : "0.##", CultureInfo.CurrentCulture)
        };
        var unit = withUnit && v.Object == null ? UnitText(project, propertyId) : "";
        return unit.Length > 0 ? text + " " + unit : text;
    }

    /// <summary>
    /// Значение свойства у выделенных скважин: одинаковое — текстом; разные — сумма (для кг, МДж, м³, т и стоимости) или
    /// среднее. Скважины с ошибкой формулы в итог не входят; <paramref name="holes"/> — не пустой список.
    /// </summary>
    public static HoleValueSummary Summarize(BlastProject project, string propertyId, IReadOnlyList<Hole> holes)
    {
        var values = holes.Select(h => Value(project, propertyId, h)).ToList();
        var range = holes.Any(h => IsOutOfRange(project, propertyId, h));
        var good = values.Where(v => !v.IsError).ToList();
        if (good.Count == 0) return new HoleValueSummary("#", false, null, false, range);
        var first = holes[values.IndexOf(good[0])];
        var same = values.Count == good.Count && good.All(v => Equals(v.Object, good[0].Object) && (v.Object != null || Math.Abs(v.Number - good[0].Number) <= 1e-9 * Math.Max(1, Math.Abs(good[0].Number))));
        if (same) return new HoleValueSummary(Format(project, propertyId, first), false, null, false, range);
        if (good[0].Object != null) return new HoleValueSummary("", true, null, false, range);
        var d = ModelFor(project).FindProperty(propertyId);
        var sum = d != null && (d.Unit is "kg" or "MJ" or "m3" or "t" || d.Group == "Стоимость");
        var total = good.Sum(v => v.Number);
        var number = (sum ? total : total / good.Count).ToString(d?.Format is { Length: > 0 } f ? f : "0.##", CultureInfo.CurrentCulture);
        var unit = UnitText(project, propertyId);
        return new HoleValueSummary("", true, unit.Length > 0 ? number + " " + unit : number, sum, range);
    }

    /// <summary>Вычисляемые свойства контекста профиля проекта (для выбора столбцов, подписей, итогов).</summary>
    public static IReadOnlyList<PropertyDef> Calculated(BlastProject project, string context) =>
        ModelFor(project).Nodes.Where(n => n.Context.Name == context).Select(n => n.Property).OrderBy(p => p.Group).ThenBy(p => p.Name).ToList();

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
