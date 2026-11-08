using System.Globalization;
using System.Text.Json;
using BlastDesigner.Calc;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>
/// Долгий сеанс расчёта открытого документа: один <see cref="CalcSession"/> на документ, синхронизация после каждого
/// изменения (<see cref="Document.Changed"/>: правка, отмена, повтор, сохранение), новый сеанс при отмене (новый объект
/// проекта) и при смене профиля или параметров проекта. Пока идёт правка (<see cref="Document.IsChanging"/>), с другого
/// потока и для сущностей не из проекта (предпросмотр, копия в окне правки) сеанс не отвечает — тогда считает короткий.
/// </summary>
public sealed class LiveCalc
{
    private readonly WeakReference<Document> _doc;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private CalcSession? _session;
    private string _key = "";
    private bool _dirty = true;

    internal LiveCalc(Document doc)
    {
        _doc = new WeakReference<Document>(doc);
        doc.Changed += (_, _) => _dirty = true;
    }

    public Document? Document => _doc.TryGetTarget(out var d) ? d : null;

    /// <summary>Сколько раз сеанс синхронизировался (для тестов).</summary>
    public int Syncs { get; private set; }

    /// <summary>Модель изменилась без события документа (решение времён): синхронизироваться при следующем обращении.</summary>
    public void Touch() => _dirty = true;

    /// <summary>Сеанс проекта документа, синхронизированный с моделью; null — отвечать нельзя (правка идёт, другой поток, другой проект).</summary>
    public CalcSession? SessionFor(BlastProject project)
    {
        if (!_doc.TryGetTarget(out var doc) || !ReferenceEquals(doc.Project, project) || doc.IsChanging || Environment.CurrentManagedThreadId != _thread)
            return null;
        if (_dirty || _session == null || !ReferenceEquals(_session.Project, project)) Refresh(project);
        return _session;
    }

    private void Refresh(BlastProject project)
    {
        var model = CalcService.ModelFor(project);
        var key = project.Calc == null ? "" : JsonSerializer.Serialize(project.Calc, ProjectJson.Options);
        if (_session == null || !ReferenceEquals(_session.Project, project) || !ReferenceEquals(_session.Model, model) || key != _key)
            _session = CalcService.Session(project, model);
        _key = key;
        _session.Sync();
        _dirty = false;
        Syncs++;
    }
}
