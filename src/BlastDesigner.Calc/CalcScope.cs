using BlastDesigner.Domain.Model;

namespace BlastDesigner.Calc;

/// <summary>
/// Данные, над которыми считает сеанс: проект, отбор скважин для итогов, аргументы встроенных алгоритмов и
/// ленивые индексы (продукт по id, скважина интервала, ряд скважины). Индексы строятся при первом обращении и
/// сбрасываются <see cref="ResetIndexes"/> при изменении состава объектов.
/// </summary>
public sealed class CalcScope
{
    private Dictionary<string, Product>? _products;
    private Dictionary<Guid, Entity>? _entities;
    private Dictionary<Guid, HoleRow>? _rowsById;
    private Dictionary<Hole, HoleRow>? _rowOfHole;
    private readonly Dictionary<Deck, Hole> _deckHole = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Primer, Deck> _primerDeck = new(ReferenceEqualityComparer.Instance);
    private bool _parentsScanned;
    private IReadOnlyList<object>? _holes;
    private Dictionary<Guid, List<object>>? _tiesIn, _tiesOut, _timingLines;

    public CalcScope(BlastProject project) => Project = project;

    public BlastProject Project { get; }

    /// <summary>
    /// Скважины, которые видит связь <c>blast.holes</c> (итоги по выбранным скважинам); null — все скважины проекта.
    /// </summary>
    public IReadOnlyList<Hole>? Subset { get; set; }

    /// <summary>Настраиваемые аргументы встроенных алгоритмов: «cellArea.boundary», «cellArea.holes».</summary>
    public Dictionary<string, object?> BuiltinArgs { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Значения параметров уровня рудника (из файла настроек рудника).</summary>
    public Dictionary<string, double> SiteParameters { get; } = new(StringComparer.Ordinal);

    public void ResetIndexes()
    {
        _products = null;
        _entities = null;
        _rowsById = null;
        _rowOfHole = null;
        _holes = null;
        _tiesIn = _tiesOut = _timingLines = null;
        _deckHole.Clear();
        _primerDeck.Clear();
        _parentsScanned = false;
    }

    public Product? FindProduct(string? id)
    {
        if (id is null) return null;
        if (_products == null)
        {
            // первый с таким id, как BlastProject.FindProduct
            _products = new Dictionary<string, Product>(StringComparer.Ordinal);
            foreach (var p in Project.Products) _products.TryAdd(p.Id, p);
        }
        return _products.GetValueOrDefault(id);
    }

    public Entity? FindEntity(Guid id)
    {
        if (_entities == null)
        {
            _entities = new Dictionary<Guid, Entity>();
            foreach (var e in Project.Entities) _entities.TryAdd(e.Id, e);
        }
        return _entities.GetValueOrDefault(id);
    }

    public IReadOnlyList<object> Holes
    {
        get
        {
            if (Subset != null) return Subset as IReadOnlyList<object> ?? Subset.Cast<object>().ToList();
            return _holes ??= Project.Holes.Cast<object>().ToList();
        }
    }

    public HoleRow? RowOf(Hole h)
    {
        if (h.RowId is { } id)
        {
            _rowsById ??= Project.Rows.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
            if (_rowsById.TryGetValue(id, out var r)) return r;
        }
        if (_rowOfHole == null)
        {
            _rowOfHole = new Dictionary<Hole, HoleRow>(ReferenceEqualityComparer.Instance);
            foreach (var r in Project.Rows)
                foreach (var hid in r.HoleIds)
                    if (FindEntity(hid) is Hole rh) _rowOfHole.TryAdd(rh, r);
        }
        return _rowOfHole.GetValueOrDefault(h);
    }

    /// <summary>Запоминает скважину интервала, когда к нему пришли от скважины (интервалы временных колонок не в проекте).</summary>
    public void NoteParent(Deck d, Hole h) => _deckHole.TryAdd(d, h);
    public void NoteParent(Primer p, Deck d) => _primerDeck.TryAdd(p, d);

    public Hole? HoleOf(Deck d)
    {
        if (_deckHole.TryGetValue(d, out var h)) return h;
        ScanParents();
        return _deckHole.GetValueOrDefault(d);
    }

    public Deck? DeckOf(Primer p)
    {
        if (_primerDeck.TryGetValue(p, out var d)) return d;
        ScanParents();
        return _primerDeck.GetValueOrDefault(p);
    }

    private void ScanParents()
    {
        if (_parentsScanned) return;
        _parentsScanned = true;
        foreach (var h in Project.Holes)
            foreach (var d in h.Decks)
            {
                _deckHole.TryAdd(d, h);
                foreach (var p in d.Primers) _primerDeck.TryAdd(p, d);
            }
    }

    /// <summary>Интервалы скважины по глубине (устойчиво: равные кровли — в порядке списка).</summary>
    public IReadOnlyList<object> DecksOf(Hole h)
    {
        var list = h.Decks;
        bool sorted = true;
        for (int i = 1; i < list.Count && sorted; i++) sorted = list[i - 1].Top <= list[i].Top;
        var result = sorted ? list.Cast<object>().ToList() : list.OrderBy(d => d.Top).Cast<object>().ToList();
        foreach (Deck d in result) NoteParent(d, h);
        return result;
    }

    public IReadOnlyList<object> PrimersOf(Hole h)
    {
        var result = new List<object>();
        foreach (Deck d in DecksOf(h))
            foreach (var p in d.Primers) { NoteParent(p, d); result.Add(p); }
        return result;
    }

    public IReadOnlyList<object> PrimersOf(Deck d)
    {
        foreach (var p in d.Primers) NoteParent(p, d);
        return d.Primers;
    }

    public IReadOnlyList<object> TiesIn(Hole h) => TieIndex(true).GetValueOrDefault(h.Id) ?? (IReadOnlyList<object>)Array.Empty<object>();
    public IReadOnlyList<object> TiesOut(Hole h) => TieIndex(false).GetValueOrDefault(h.Id) ?? (IReadOnlyList<object>)Array.Empty<object>();

    private Dictionary<Guid, List<object>> TieIndex(bool incoming)
    {
        if (_tiesIn == null)
        {
            _tiesIn = new();
            _tiesOut = new();
            foreach (var t in Project.Ties)
            {
                (_tiesIn.TryGetValue(t.ToId, out var a) ? a : _tiesIn[t.ToId] = new()).Add(t);
                (_tiesOut.TryGetValue(t.FromId, out var b) ? b : _tiesOut[t.FromId] = new()).Add(t);
            }
        }
        return incoming ? _tiesIn : _tiesOut!;
    }

    public object? TimingLineOf(Hole h)
    {
        if (_timingLines == null)
        {
            _timingLines = new();
            foreach (var l in Project.Entities.OfType<TimingLineEntity>())
                foreach (var id in l.HoleIds)
                    (_timingLines.TryGetValue(id, out var a) ? a : _timingLines[id] = new()).Add(l);
        }
        return _timingLines.GetValueOrDefault(h.Id)?.FirstOrDefault();
    }

    /// <summary>Ключ сущности для ручных значений: Guid сущности, «guid#n» для интервала, «guid#n#k» для боевика, id продукта.</summary>
    public string? KeyOf(object o) => o switch
    {
        Entity e => e.Id.ToString(),
        Deck d when HoleOf(d) is { } h => $"{h.Id}#{h.Decks.IndexOf(d)}",
        Primer p when DeckOf(p) is { } d && HoleOf(d) is { } h => $"{h.Id}#{h.Decks.IndexOf(d)}#{d.Primers.IndexOf(p)}",
        Product p => p.Id,
        HoleRow r => r.Id.ToString(),
        BlastProject => "blast",
        _ => null
    };
}
