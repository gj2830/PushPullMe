namespace PushPullMe.Core;

/// <summary>What a point snapped to is.</summary>
public enum Snap
{
    /// <summary>The end of a straight edge (a corner of a face).</summary>
    Endpoint,
    /// <summary>The middle of a straight edge.</summary>
    Midpoint,
    /// <summary>Somewhere on an edge.</summary>
    OnEdge,
    /// <summary>A corner of a body (the tape measure).</summary>
    Vertex,
    /// <summary>The centre of a circle or arc.</summary>
    Centre,
    /// <summary>Where a guide line meets another or an edge.</summary>
    GuideCrossing,
    /// <summary>Somewhere on a guide line.</summary>
    OnGuide,
    /// <summary>The end of a loose line.</summary>
    LooseEnd,
    /// <summary>The middle of a loose line.</summary>
    LooseMiddle,
    /// <summary>Somewhere on a loose line.</summary>
    OnLoose,
    /// <summary>The start of the line being drawn: it closes the shape.</summary>
    CloseShape,
}

public static class SnapNames
{
    /// <summary>The snap as shown, in the language chosen.</summary>
    public static string Name(this Snap s) => s switch
    {
        Snap.Endpoint => Lang.T("eindpunt", "endpoint"),
        Snap.Midpoint => Lang.T("midden", "midpoint"),
        Snap.OnEdge => Lang.T("op de rand", "on edge"),
        Snap.Vertex => Lang.T("hoekpunt", "vertex"),
        Snap.Centre => Lang.T("middelpunt", "centre"),
        Snap.GuideCrossing => Lang.T("snijpunt met de hulplijn", "intersection with the guide line"),
        Snap.OnGuide => Lang.T("op de hulplijn", "on the guide line"),
        Snap.LooseEnd => Lang.T("eindpunt van een losse lijn", "endpoint of a loose line"),
        Snap.LooseMiddle => Lang.T("midden van een losse lijn", "midpoint of a loose line"),
        Snap.OnLoose => Lang.T("op een losse lijn", "on a loose line"),
        Snap.CloseShape => Lang.T("beginpunt: de vorm sluiten", "start point: close the shape"),
        _ => s.ToString(),
    };
}
