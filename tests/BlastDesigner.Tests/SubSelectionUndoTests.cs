using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>
/// What the view selects besides the objects (the vertices of the lines) goes with the undo steps of the document as the
/// selection does: an undo gives back what was selected before the step, a redo what was selected when it was undone.
/// </summary>
public class SubSelectionUndoTests
{
    [Fact]
    public void AnUndoGivesBackTheVerticesSelectedBeforeTheStep()
    {
        var p = BlastProject.CreateNew();
        var line = p.Add(new PolylineEntity { Points = { new Vec3(0, 0, 0), new Vec3(10, 0, 0), new Vec3(10, 10, 0), new Vec3(0, 10, 0) } });
        var doc = new Document(p);
        var vertices = new List<int> { 2 };
        doc.SaveSubSelection = () => vertices.ToArray();
        doc.RestoreSubSelection = saved => { vertices.Clear(); if (saved is int[] v) vertices.AddRange(v); };

        doc.Execute("Delete vertex", pr => ((PolylineEntity)pr.FindEntity(line.Id)!).Points.RemoveAt(2));
        vertices.Clear();
        vertices.Add(1);

        doc.Undo();
        Assert.Equal(4, ((PolylineEntity)doc.Project.FindEntity(line.Id)!).Points.Count);
        Assert.Equal(new[] { 2 }, vertices);

        doc.Redo();
        Assert.Equal(3, ((PolylineEntity)doc.Project.FindEntity(line.Id)!).Points.Count);
        Assert.Equal(new[] { 1 }, vertices);
    }

    [Fact]
    public void ASelectionStepKeepsTheVerticesSelectedBeforeIt()
    {
        var p = BlastProject.CreateNew();
        var line = p.Add(new PolylineEntity { Points = { new Vec3(0, 0, 0), new Vec3(10, 0, 0) } });
        var doc = new Document(p);
        var vertices = new List<int> { 0 };
        doc.SaveSubSelection = () => vertices.ToArray();
        doc.RestoreSubSelection = saved => { vertices.Clear(); if (saved is int[] v) vertices.AddRange(v); };
        doc.Select(new[] { line.Id });
        vertices.Clear();
        doc.Undo();
        Assert.Equal(new[] { 0 }, vertices);
    }
}
