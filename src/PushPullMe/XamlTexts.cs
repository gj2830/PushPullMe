using System.Windows;
using System.Windows.Controls;
using PushPullMe.Core;

namespace PushPullMe;

/// <summary>
/// The texts of the window as written in its XAML (Dutch) and in English: in English, every tooltip, label and button
/// text is looked up here when the window opens.
/// </summary>
internal static class XamlTexts
{
    private static readonly Dictionary<string, string> En = new()
    {
        ["↩  ongedaan"] = "↩  undo",
        ["↪  opnieuw"] = "↪  redo",
        ["📂  openen"] = "📂  open",
        ["STL, 3MF of .rsdocx (Ctrl+O)"] = "STL, 3MF or .rsdocx (Ctrl+O)",
        ["💾  opslaan"] = "💾  save",
        ["Als .rsdocx (voor je tekenpakket) of .stl (voor de printer) (Ctrl+S)"] = "As .rsdocx (for your CAD program) or .stl (for the printer) (Ctrl+S)",
        ["BASIS"] = "BASICS",
        ["Selecteren (spatie)"] = "Select (space)",
        ["Push/Pull (P): klik een plat vlak, beweeg, klik nog eens. Of typ de afstand + Enter."] =
            "Push/Pull (P): click a flat face, move, click again. Or type the distance + Enter.",
        ["Verplaatsen (M): klik een hoekpunt, rand of vlak, beweeg en klik nog eens; wat eraan vastzit buigt mee. Shift: het hele lichaam · Ctrl: een kopie van het lichaam. Pijltjes: rood, groen of blauwe as. Of typ de afstand + Enter."] =
            "Move (M): click a vertex, edge or face, move and click again; what is attached to it follows. Shift: the whole body · Ctrl: a copy of the body. Arrow keys: red, green or blue axis. Or type the distance + Enter.",
        ["Draaien (Q): klik bij een rand van een vlak om dat vlak om die rand te kantelen; klik midden op een vlak voor het draaipunt van het hele lichaam (de as staat er haaks op; pijltjes: rode, groene of blauwe as), klik de beginrichting, beweeg (stappen van 15°) en klik. Of typ de hoek + Enter. Ctrl bij de eerste klik: een gedraaide kopie."] =
            "Rotate (Q): click near an edge of a face to tilt that face round the edge; click in the middle of a face for the pivot of the whole body (the axis is square to it; arrow keys: red, green or blue axis), click the start direction, move (steps of 15°) and click. Or type the angle + Enter. Ctrl at the first click: a rotated copy.",
        ["Schalen (S): klik het punt dat blijft staan, klik het punt waaraan je trekt, beweeg en klik; of typ de factor (bv. 1,5) + Enter. Pijltjes: alleen langs de rode, groene of blauwe as (bij platte vlakken). Ctrl bij de eerste klik: een geschaalde kopie."] =
            "Scale (S): click the point that stays put, click the point you pull at, move and click; or type the factor (e.g. 1.5) + Enter. Arrow keys: only along the red, green or blue axis (with flat faces). Ctrl at the first click: a scaled copy.",
        ["TEKENEN"] = "DRAW",
        ["Lijn (L): klik punt na punt op een plat vlak (vastklikken op assen, evenwijdig en loodrecht aan randen, eindpunten, middens en hulplijnen). Terug op het beginpunt: een eigen vlak; van rand tot rand: het vlak wordt gedeeld. Typ een lengte + Enter."] =
            "Line (L): click point after point on a flat face (snapping to axes, parallel and square to edges, endpoints, midpoints and guide lines). Back on the start point: a face of its own; from edge to edge: the face is divided. Type a length + Enter.",
        ["Rechthoek (R): klik een hoek op een plat vlak, klik de andere hoek. Of typ breedte;hoogte + Enter."] =
            "Rectangle (R): click a corner on a flat face, click the other corner. Or type width;height + Enter.",
        ["Cirkel (C): klik het middelpunt op een plat vlak en dan de rand. Of typ de straal + Enter."] =
            "Circle (C): click the centre on a flat face and then the edge. Or type the radius + Enter.",
        ["Rechthoek onder een hoek: klik een hoek, klik de tweede hoek (de eerste zijde, in elke richting), beweeg voor de breedte en klik; of typ de breedte + Enter."] =
            "Rotated rectangle: click a corner, click the second corner (the first side, in any direction), move for the width and click; or type the width + Enter.",
        ["Veelhoek (G): klik het middelpunt op een plat vlak, beweeg (grootte en stand) en klik; of typ de straal + Enter. ↑/↓: meer of minder hoeken (standaard 6)."] =
            "Polygon (G): click the centre on a flat face, move (size and turn) and click; or type the radius + Enter. ↑/↓: more or fewer sides (6 by default).",
        ["Boog vanaf het middelpunt: klik het middelpunt, klik het beginpunt (de straal), beweeg voor de hoek (stappen van 15°) en klik; of typ de hoek + Enter. Van rand tot rand deelt hij het vlak, anders een losse boog (die met lijnen of randen een vlak kan sluiten)."] =
            "Arc from the centre: click the centre, click the start point (the radius), move for the angle (steps of 15°) and click; or type the angle + Enter. From edge to edge it divides the face, otherwise a loose arc (that can close a face with lines or edges).",
        ["Boog (A): klik het beginpunt, het eindpunt, en beweeg voor de bolling; klik, of typ de bolling + Enter. Van rand tot rand deelt hij het vlak (een hoek afronden), anders een losse boog (die met lijnen of randen een vlak kan sluiten)."] =
            "Arc (A): click the start point, the end point, and move for the bulge; click, or type the bulge + Enter. From edge to edge it divides the face (rounding a corner), otherwise a loose arc (that can close a face with lines or edges).",
        ["Boog door drie punten: klik het beginpunt, een punt waar de boog doorheen gaat, en het eindpunt (vastklikken op hoeken, middens en randen). Van rand tot rand deelt hij het vlak, anders een losse boog (die met lijnen of randen een vlak kan sluiten)."] =
            "3 point arc: click the start point, a point the arc goes through, and the end point (snapping to corners, midpoints and edges). From edge to edge it divides the face, otherwise a loose arc (that can close a face with lines or edges).",
        ["Taartpunt: klik het middelpunt, klik het beginpunt (de straal), beweeg voor de hoek (stappen van 15°) en klik; of typ de hoek + Enter. Wordt een vlak (boog + twee stralen) om eruit te trekken."] =
            "Pie: click the centre, click the start point (the radius), move for the angle (steps of 15°) and click; or type the angle + Enter. Becomes a face (arc + two radii) to pull out.",
        ["Uit de vrije hand (Freehand) (nog niet)"] = "Freehand (not yet)",
        ["BEWERKEN"] = "EDIT",
        ["Offset (F): klik een plat vlak en beweeg naar binnen: een nieuwe rand op vaste afstand van de buitenrand (voor een wand of kraag). Of typ de afstand + Enter."] =
            "Offset (F): click a flat face and move inwards: a new edge at a fixed distance from the outer edge (for a wall or a collar). Or type the distance + Enter.",
        ["Afronden (K): klik een scherpe rechte rand tussen twee platte vlakken en beweeg voor de straal; klik, of typ de straal + Enter. (Zoals DesignSpark; in SketchUp zit dit niet.)"] =
            "Round (K): click a sharp straight edge between two flat faces and move for the radius; click, or type the radius + Enter. (As in DesignSpark; SketchUp does not have this.)",
        ["Ronddraaien (Follow Me, zoals DesignSpark's trekken met een draaias): klik een plat vlak, klik een as (een rechte rand, losse lijn of hulplijn in dat vlak), beweeg voor de hoek (stappen van 15°) en klik; of typ de hoek + Enter. Het vlak draait het lichaam uit."] =
            "Follow Me (as DesignSpark's pull with a revolve axis): click a flat face, click an axis (a straight edge, loose line or guide line in that face's plane), move for the angle (steps of 15°) and click; or type the angle + Enter. The face turns out of the body.",
        ["Spiegelen: klik een plat vlak; het lichaam spiegelt in dat vlak naar de andere kant. Ctrl+klik: een gespiegelde kopie erbij (twee helften van een symmetrisch onderdeel)."] =
            "Mirror: click a flat face; the body is mirrored in that face to the other side. Ctrl+click: add a mirrored copy (two halves of a symmetrical part).",
        ["Buitenschil (Outer Shell): overlappende delen samenvoegen tot één (nog niet)"] = "Outer Shell: merge overlapping parts into one (not yet)",
        ["Verenigen (Union): klik een deel, klik een tweede deel dat het overlapt of met een vlak raakt: ze worden één. (Nog alleen delen met platte vlakken en rechte randen.)"] =
            "Union: click a part, click a second part that overlaps it or touches it with a face: they become one. (Only parts with flat faces and straight edges, for now.)",
        ["Aftrekken (Subtract): een deel van een ander aftrekken (nog niet)"] = "Subtract: take one part away from another (not yet)",
        ["Doorsnede (Intersect): alleen wat delen gemeen hebben (nog niet)"] = "Intersect: only what parts have in common (not yet)",
        ["Wegsnijden (Trim): een deel wegsnijden met een ander (nog niet)"] = "Trim: cut a part away with another (not yet)",
        ["Splitsen (Split): delen splitsen langs hun snijvlak (nog niet)"] = "Split: split parts along where they meet (not yet)",
        ["METEN EN AANTEKENEN"] = "MEASURE AND ANNOTATE",
        ["Meetlint (T): klik een punt en beweeg om te meten; klik een rand en trek eraf voor een hulplijn (of typ de afstand + Enter). Delete: alle hulplijnen weg."] =
            "Tape measure (T): click a point and move to measure; click an edge and pull away for a guide line (or type the distance + Enter). Delete: remove all guide lines.",
        ["Maatlijnen (Dimensions) (nog niet)"] = "Dimensions (not yet)",
        ["Gradenboog (Protractor): hoeken meten, hulplijnen onder een hoek (nog niet)"] = "Protractor: measure angles, guide lines at an angle (not yet)",
        ["Assen (Axes): het assenstelsel verplaatsen of draaien (nog niet)"] = "Axes: move or rotate the axes (not yet)",
        ["Tekstlabels (Text) (nog niet)"] = "Text labels (not yet)",
        ["Doorsnedevlak (Section Plane): het model doorsnijden om erin te kijken (nog niet)"] = "Section Plane: cut through the model to look inside (not yet)",
        ["Labels/lagen (Tag): delen tonen en verbergen (nog niet)"] = "Tags: show and hide parts (not yet)",
        ["3D-tekst (3D Text): tekst als vorm (nog niet)"] = "3D Text: text as a shape (not yet)",
        ["KLEUR, SELECTIE EN GUM"] = "COLOUR, SELECTION AND ERASER",
        ["Verfemmer (B): kies een kleur en klik een vlak. Ctrl: het hele lichaam · Shift: alle vlakken in dezelfde kleur · Alt: kleur overnemen."] =
            "Paint bucket (B): choose a colour and click a face. Ctrl: the whole body · Shift: all faces of the same colour · Alt: pick up a colour.",
        ["Pipet (Sample Material): klik een vlak om zijn kleur over te nemen in de verfemmer (ook: Alt+klik met de verfemmer)"] =
            "Eyedropper (Sample Material): click a face to take its colour into the paint bucket (also: Alt+click with the paint bucket)",
        ["Lasso: klik, trek een lus om vlakken heen en klik nog eens: die vlakken zijn gekozen (Shift: erbij, Ctrl: eraf). Daarna verplaatst ✥ ze samen en verft 🎨 ze in één keer. Esc: keuze leeg."] =
            "Lasso: click, draw a loop round faces and click again: those faces are selected (Shift: add, Ctrl: remove). Then ✥ moves them together and 🎨 paints them at once. Esc: clear the selection.",
        ["Gum (E): klik een getekende lijn, rechthoek of cirkel weg; de vlakken worden weer één."] =
            "Eraser (E): click a drawn line, rectangle or circle away; the faces become one again.",
        ["KIJKEN"] = "VIEW",
        ["Alles in beeld (Zoom Extents)"] = "Zoom Extents",
        ["Zoomen (Zoom): slepen om in of uit te zoomen; zoomen kan al met het wieltje (nog niet)"] = "Zoom: drag to zoom in or out; the wheel zooms already (not yet)",
        ["Inzoomen op een venster (Zoom Window) (nog niet)"] = "Zoom Window (not yet)",
        ["Camera plaatsen (Position Camera) (nog niet)"] = "Position Camera (not yet)",
        ["Rondkijken (Look Around) (nog niet)"] = "Look Around (not yet)",
        ["Lopen (Walk): door het model lopen (nog niet)"] = "Walk: walk through the model (not yet)",
        ["Sleep een STL, 3MF of .rsdocx hierheen"] = "Drag an STL, 3MF or .rsdocx here",
        ["middelste knop = draaien · Shift + middelste knop = schuiven · wiel = zoomen"] = "middle button = orbit · Shift + middle button = pan · wheel = zoom",
        ["Vormen herkennen…"] = "Recognising shapes…",
        ["🎨 Verf"] = "🎨 Paint",
        ["Afstand"] = "Distance",
        ["Open een model om te beginnen."] = "Open a model to begin.",
    };

    private static readonly Dictionary<string, string> Nl = En.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>The window's texts (tooltips, labels, button texts) in the language chosen, all through it (from either
    /// language: it can be switched while the app runs).</summary>
    public static void Apply(DependencyObject root) => Walk(root);

    private static string Tr(string s) => (Lang.English ? En : Nl).TryGetValue(s, out var t) ? t : s;

    private static void Walk(object node)
    {
        if (node is FrameworkElement fe && fe.ToolTip is string tip) fe.ToolTip = Tr(tip);
        switch (node)
        {
            case TextBlock tb when tb.Text is { Length: > 0 }: tb.Text = Tr(tb.Text); break;
            case ContentControl cc when cc.Content is string text: cc.Content = Tr(text); break;
        }
        if (node is DependencyObject d)
            foreach (var child in LogicalTreeHelper.GetChildren(d)) Walk(child);
    }
}
