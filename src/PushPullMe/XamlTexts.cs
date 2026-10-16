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
        ["⊞  grond"] = "⊞  ground",
        ["Werkvlak terug op de grond (net als ⊞ en klikken op een lege plek)"] = "Work plane back on the ground (as ⊞ and a click on empty space)",
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
        ["Uit de vrije hand (Freehand): klik op een plat vlak om te beginnen, beweeg (het spoor wordt gevolgd) en klik om te eindigen. Terug bij het begin: een vlak; anders losse lijnen (die met andere lijnen of randen een vlak kunnen sluiten)."] =
            "Freehand: click on a flat face to start, move (the track is followed) and click to finish. Back at the start: a face; otherwise loose lines (which can close a face with other lines or edges).",
        ["Werkvlak: waar je tekent als er geen vlak onder de muis is (eerst de grond, met het raster). Klik een plat vlak: het werkvlak ligt er gelijk mee; klik leeg: weer op de grond. Pijltjes: draaien (15°, Shift 5°); typ mm + Enter: verschuiven langs de normaal (x;y: in het vlak)."] =
            "Work plane: where you draw when there is no face under the mouse (at first the ground, with the grid). Click a flat face: the work plane lies level with it; click empty: back on the ground. Arrow keys: turn (15°, Shift 5°); type mm + Enter: shift along the normal (x;y: within the plane).",
        ["BEWERKEN"] = "EDIT",
        ["Offset (F): klik een plat vlak en beweeg naar binnen: een nieuwe rand op vaste afstand van de buitenrand (voor een wand of kraag); beweeg naar buiten voor een rand eromheen (op het vlak eromheen). Of typ de afstand + Enter (min: naar buiten)."] =
            "Offset (F): click a flat face and move inwards: a new edge at a fixed distance from the outer edge (for a wall or rim); move outwards for an edge round it (on the face round it). Or type the distance + Enter (minus: outwards).",
        ["Afronden (K): klik een scherpe rechte rand tussen twee platte vlakken en beweeg voor de straal; klik, of typ de straal + Enter. (Zoals DesignSpark; in SketchUp zit dit niet.)"] =
            "Round (K): click a sharp straight edge between two flat faces and move for the radius; click, or type the radius + Enter. (As in DesignSpark; SketchUp does not have this.)",
        ["Ronddraaien en volgen (Follow Me): klik een plat vlak (het profiel), en dan een as (een rechte rand, losse lijn of hulplijn in dat vlak) om het rond te draaien – beweeg voor de hoek (stappen van 15°, of 360°) en klik, of typ de hoek + Enter – of een vlak waarvan de rand het pad is: het profiel loopt die rand rond (in het lichaam: weggesneden, zoals een afschuining; erbuiten: een lijst)."] =
            "Revolve and follow (Follow Me): click a flat face (the profile), then an axis (a straight edge, loose line or guide line in that face) to turn it round – move for the angle (steps of 15°, or 360°) and click, or type the angle + Enter – or a face whose edge is the path: the profile goes round that edge (in the body: cut away, like a chamfer; outside it: a rim).",
        ["Spiegelen: klik een plat vlak; het lichaam spiegelt in dat vlak naar de andere kant. Ctrl+klik: een gespiegelde kopie erbij (twee helften van een symmetrisch onderdeel)."] =
            "Mirror: click a flat face; the body is mirrored in that face to the other side. Ctrl+click: add a mirrored copy (two halves of a symmetrical part).",
        ["Buitenschil (Outer Shell): alle delen die elkaar overlappen of met een vlak raken, in één keer samenvoegen. (Platte en ronde vlakken: cilinders, kegels, bollen, ringen; nog geen vrije vormen.)"] =
            "Outer Shell: unite all parts that overlap or touch with a face, in one go. (Flat and round faces: cylinders, cones, spheres, tori; no free-form faces yet.)",
        ["Verenigen (Union): klik een deel, klik een tweede deel dat het overlapt of met een vlak raakt: ze worden één. (Platte en ronde vlakken: cilinders, kegels, bollen, ringen; nog geen vrije vormen.)"] =
            "Union: click a part, click a second part that overlaps it or touches it with a face: they become one. (Flat and round faces: cylinders, cones, spheres, tori; no free-form faces yet.)",
        ["Aftrekken (Subtract): klik het deel waar iets af moet, klik het deel dat eraf gaat (dat verdwijnt). (Platte en ronde vlakken: cilinders, kegels, bollen, ringen; nog geen vrije vormen.)"] =
            "Subtract: click the part to take something from, click the part that goes (it disappears). (Flat and round faces: cylinders, cones, spheres, tori; no free-form faces yet.)",
        ["Doorsnede (Intersect): klik een deel en een tweede dat het overlapt: alleen wat ze gemeen hebben blijft over. (Platte en ronde vlakken: cilinders, kegels, bollen, ringen; nog geen vrije vormen.)"] =
            "Intersect: click a part and a second one that overlaps it: only what they have in common is left. (Flat and round faces: cylinders, cones, spheres, tori; no free-form faces yet.)",
        ["Wegsnijden (Trim): klik het deel dat bijgesneden wordt, klik het deel dat snijdt (dat blijft staan). (Platte en ronde vlakken: cilinders, kegels, bollen, ringen; nog geen vrije vormen.)"] =
            "Trim: click the part to be trimmed, click the part that cuts (it stays). (Flat and round faces: cylinders, cones, spheres, tori; no free-form faces yet.)",
        ["Splitsen (Split): klik twee delen die elkaar overlappen: ze worden drie delen (alleen het eerste, het gemeenschappelijke, alleen het tweede). (Platte en ronde vlakken: cilinders, kegels, bollen, ringen; nog geen vrije vormen.)"] =
            "Split: click two parts that overlap: they become three parts (only the first, the common part, only the second). (Flat and round faces: cylinders, cones, spheres, tori; no free-form faces yet.)",
        ["METEN EN AANTEKENEN"] = "MEASURE AND ANNOTATE",
        ["Meetlint (T): klik een punt en beweeg om te meten; klik een rand en trek eraf voor een hulplijn (of typ de afstand + Enter). Delete: alle hulplijnen weg."] =
            "Tape measure (T): click a point and move to measure; click an edge and pull away for a guide line (or type the distance + Enter). Delete: remove all guide lines.",
        ["Maatlijn (Dimensions): klik twee punten (vastklikken op hoeken en randen), beweeg om de maatlijn ervan af te zetten en klik. Klik een maat om hem weg te halen. Alleen in beeld."] =
            "Dimension: click two points (snapping to corners and edges), move to set the dimension line off and click. Click a dimension to remove it. Only in the view.",
        ["Gradenboog (Protractor): klik het middelpunt op een vlak, klik een punt voor de beginlijn, beweeg voor de hoek (stappen van 15°) en klik; of typ de hoek + Enter. Er komt een hulplijn onder die hoek."] =
            "Protractor: click the centre on a face, click a point for the base line, move for the angle (steps of 15°) and click; or type the angle + Enter. A guide line at that angle appears.",
        ["Assen (Axes): klik een nieuwe oorsprong, klik een punt in de richting van de rode as en een punt voor de groene as: vastklikken, rechthoeken en de pijltjes volgen die assen. Klik op een lege plek: de gewone assen terug."] =
            "Axes: click a new origin, click a point in the direction of the red axis and a point for the green axis: snapping, rectangles and the arrow keys follow those axes. Click an empty spot: the usual axes back.",
        ["Tekstlabel (Text): klik een punt op het model, typ de tekst en druk op Enter. Klik een label om het weg te halen. Alleen in beeld."] =
            "Text: click a point on the model, type the text and press Enter. Click a label to remove it. Only in the view.",
        ["Doorsnedevlak (Section Plane): klik een plat vlak, beweeg om het snijvlak het model in te schuiven en klik (of typ de diepte + Enter): alles ervoor wordt weggelaten, de snijlijn dik getekend. Esc (met ⊟ gekozen): weg ermee."] =
            "Section Plane: click a flat face, move to slide the cutting plane into the model and click (or type the depth + Enter): everything in front of it is left out, the cut drawn thick. Esc (with ⊟ chosen): remove it.",
        ["Verbergen (Tag): klik een deel om het te verbergen (bv. om erachter te werken); klik op een lege plek om alles weer te tonen. Alleen in beeld."] =
            "Hide (Tag): click a part to hide it (e.g. to work behind it); click an empty spot to show everything again. Only in the view.",
        ["3D-tekst (3D Text): klik een punt op een plat vlak, typ de tekst en druk op Enter: de letters worden vlakken (zo hoog als de maat in het vak, standaard een vijfde van het vlak). Typ daarna een dikte + Enter om ze er allemaal uit te trekken."] =
            "3D Text: click a point on a flat face, type the text and press Enter: the letters become faces (as high as the size in the box, by default a fifth of the face). Then type a thickness + Enter to pull them all out.",
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
        ["Zoomen (Zoom): klik, beweeg omhoog om in te zoomen of omlaag om uit te zoomen, klik om te stoppen. (Het wieltje zoomt ook.)"] =
            "Zoom: click, move up to zoom in or down to zoom out, click to stop. (The wheel zooms too.)",
        ["Zoomvenster (Zoom Window): klik een hoek, klik de andere hoek: wat in het kader staat, vult het beeld."] =
            "Zoom Window: click a corner, click the other corner: what is in the frame fills the view.",
        ["Camera plaatsen (Position Camera): klik waar je wilt staan (op een vlak of de grond); je kijkt vanaf ooghoogte. Typ een andere ooghoogte (mm) + Enter. Daarna rondkijken."] =
            "Position Camera: click where you want to stand (on a face or the ground); you look from eye height. Type another eye height (mm) + Enter. Then look around.",
        ["Rondkijken (Look Around): klik en beweeg om je hoofd te draaien zonder van je plek te gaan; klik om te stoppen."] =
            "Look Around: click and move to turn your head without leaving your place; click to stop.",
        ["Lopen (Walk): ↑/↓ vooruit en achteruit, ←/→ draaien, PageUp/PageDown omhoog en omlaag; klik en beweeg om rond te kijken."] =
            "Walk: ↑/↓ forward and back, ←/→ turn, PageUp/PageDown up and down; click and move to look around.",
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
