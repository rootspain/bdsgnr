namespace BlastDesigner.Domain.Model;

/// <summary>
/// Профиль расчёта, с которым считался проект: копия использованной версии (JSON *.bdcalc) и её хеш, значения
/// параметров уровня взрыва и типов скважин, ручные значения. Нет в файлах до движка вычислений — тогда считается
/// встроенным профилем «SHOTPlus 6».
/// </summary>
public sealed class ProjectCalc
{
    public string ProfileName { get; set; } = "";
    public string ProfileVersion { get; set; } = "";
    public string ProfileHash { get; set; } = "";
    /// <summary>Разложенный профиль (со всеми унаследованными определениями) в JSON; пусто — встроенный по имени.</summary>
    public string? ProfileJson { get; set; }
    public Dictionary<string, double>? Parameters { get; set; }
    /// <summary>Номер типа скважины → параметр → значение.</summary>
    public Dictionary<int, Dictionary<string, double>>? HoleTypeParameters { get; set; }
    public List<ValueOverride>? Overrides { get; set; }
    /// <summary>Опубликованные версии профиля, старые первыми: к любой можно откатиться.</summary>
    public List<ProfileVersion>? History { get; set; }
}

/// <summary>Версия профиля в истории проекта: когда, кем и почему опубликована, и сама копия профиля.</summary>
public sealed class ProfileVersion
{
    public string Version { get; set; } = "";
    public DateTime Published { get; set; }
    public string Author { get; set; } = "";
    public string Reason { get; set; } = "";
    public string ProfileName { get; set; } = "";
    /// <summary>Разложенный профиль; пусто — встроенный «SHOTPlus 6».</summary>
    public string? ProfileJson { get; set; }
    public string ProfileHash { get; set; } = "";
}

/// <summary>Ручное значение вычисляемого свойства для одной сущности (обобщение ManualLoading, ElectronicTimeManual).</summary>
public sealed class ValueOverride
{
    /// <summary>Ключ сущности: Guid сущности, «guid#n» для интервала, «guid#n#k» для боевика.</summary>
    public string Entity { get; set; } = "";
    public string Property { get; set; } = "";
    public double Value { get; set; }
}
