namespace PushPullMe.Editor.Session;

/// <summary>What a place in the tool bar is: a heading, a tool to choose, or an action done at once.</summary>
public enum ToolBarItemKind { Group, Tool, Action }

/// <summary>The tool bar's actions (done at once, no tool chosen).</summary>
public enum ToolAction { OuterShell, Sample, ZoomExtents }

/// <summary>A place in the tool bar: its kind, the tool or action, its icon (a heading: its text), its tooltip (Dutch;
/// English through <see cref="UiTexts"/>).</summary>
public sealed record ToolBarItem(ToolBarItemKind Kind, Tool? Tool, ToolAction? Action, string Icon, string Tip);

/// <summary>The tool bar as the window shows it, in order.</summary>
public static class ToolBar
{
    public static readonly IReadOnlyList<ToolBarItem> Items =
    [
        new(ToolBarItemKind.Group, null, null, "BASIS", ""),
        new(ToolBarItemKind.Tool, Tool.Select, null, "↖", "Selecteren (spatie)"),
        new(ToolBarItemKind.Tool, Tool.PushPull, null, "⇕", "Push/Pull (P): klik een plat vlak, beweeg, klik nog eens. Of typ de afstand + Enter."),
        new(ToolBarItemKind.Tool, Tool.Move, null, "✥", "Verplaatsen (M): klik een hoekpunt, rand of vlak, beweeg en klik nog eens; wat eraan vastzit buigt mee. Shift: het hele lichaam · Ctrl: een kopie van het lichaam. Pijltjes: rood, groen of blauwe as. Of typ de afstand + Enter."),
        new(ToolBarItemKind.Tool, Tool.Rotate, null, "↻", "Draaien (Q): klik bij een rand van een vlak om dat vlak om die rand te kantelen; klik midden op een vlak voor het draaipunt van het hele lichaam (de as staat er haaks op; pijltjes: rode, groene of blauwe as), klik de beginrichting, beweeg (stappen van 15°) en klik. Of typ de hoek + Enter. Ctrl bij de eerste klik: een gedraaide kopie."),
        new(ToolBarItemKind.Tool, Tool.Scale, null, "⇲", "Schalen (S): klik het punt dat blijft staan, klik het punt waaraan je trekt, beweeg en klik; of typ de factor (bv. 1,5) + Enter. Pijltjes: alleen langs de rode, groene of blauwe as (bij platte vlakken). Ctrl bij de eerste klik: een geschaalde kopie."),
        new(ToolBarItemKind.Group, null, null, "TEKENEN", ""),
        new(ToolBarItemKind.Tool, Tool.Line, null, "╱", "Lijn (L): klik punt na punt op een plat vlak (vastklikken op assen, evenwijdig en loodrecht aan randen, eindpunten, middens en hulplijnen). Terug op het beginpunt: een eigen vlak; van rand tot rand: het vlak wordt gedeeld. Typ een lengte + Enter."),
        new(ToolBarItemKind.Tool, Tool.Rectangle, null, "▭", "Rechthoek (R): klik een hoek op een plat vlak, klik de andere hoek. Of typ breedte;hoogte + Enter."),
        new(ToolBarItemKind.Tool, Tool.Circle, null, "◯", "Cirkel (C): klik het middelpunt op een plat vlak en dan de rand. Of typ de straal + Enter."),
        new(ToolBarItemKind.Tool, Tool.RotatedRectangle, null, "◇", "Rechthoek onder een hoek: klik een hoek, klik de tweede hoek (de eerste zijde, in elke richting), beweeg voor de breedte en klik; of typ de breedte + Enter."),
        new(ToolBarItemKind.Tool, Tool.Polygon, null, "⬡", "Veelhoek (G): klik het middelpunt op een plat vlak, beweeg (grootte en stand) en klik; of typ de straal + Enter. ↑/↓: meer of minder hoeken (standaard 6)."),
        new(ToolBarItemKind.Tool, Tool.CentreArc, null, "◜", "Boog vanaf het middelpunt: klik het middelpunt, klik het beginpunt (de straal), beweeg voor de hoek (stappen van 15°) en klik; of typ de hoek + Enter. Van rand tot rand deelt hij het vlak, anders een losse boog (die met lijnen of randen een vlak kan sluiten)."),
        new(ToolBarItemKind.Tool, Tool.Arc, null, "⌒", "Boog (A): klik het beginpunt, het eindpunt, en beweeg voor de bolling; klik, of typ de bolling + Enter. Van rand tot rand deelt hij het vlak (een hoek afronden), anders een losse boog (die met lijnen of randen een vlak kan sluiten)."),
        new(ToolBarItemKind.Tool, Tool.Arc3, null, "⌓", "Boog door drie punten: klik het beginpunt, een punt waar de boog doorheen gaat, en het eindpunt (vastklikken op hoeken, middens en randen). Van rand tot rand deelt hij het vlak, anders een losse boog (die met lijnen of randen een vlak kan sluiten)."),
        new(ToolBarItemKind.Tool, Tool.Pie, null, "◔", "Taartpunt: klik het middelpunt, klik het beginpunt (de straal), beweeg voor de hoek (stappen van 15°) en klik; of typ de hoek + Enter. Wordt een vlak (boog + twee stralen) om eruit te trekken."),
        new(ToolBarItemKind.Tool, Tool.Freehand, null, "✎", "Uit de vrije hand (Freehand): klik op een plat vlak om te beginnen, beweeg (het spoor wordt gevolgd) en klik om te eindigen. Terug bij het begin: een vlak; anders losse lijnen (die met andere lijnen of randen een vlak kunnen sluiten)."),
        new(ToolBarItemKind.Tool, Tool.WorkPlane, null, "⊞", "Werkvlak: waar je tekent als er geen vlak onder de muis is (eerst de grond, met het raster). Klik een plat vlak: het werkvlak ligt er gelijk mee; klik leeg: weer op de grond. Pijltjes: draaien (15°, Shift 5°); typ mm + Enter: verschuiven langs de normaal (x;y: in het vlak)."),
        new(ToolBarItemKind.Group, null, null, "BEWERKEN", ""),
        new(ToolBarItemKind.Tool, Tool.Offset, null, "▣", "Offset (F): klik een plat vlak en beweeg naar binnen: een nieuwe rand op vaste afstand van de buitenrand (voor een wand of kraag); beweeg naar buiten voor een rand eromheen (op het vlak eromheen). Of typ de afstand + Enter (min: naar buiten)."),
        new(ToolBarItemKind.Tool, Tool.Round, null, "◠", "Afronden (K): klik een scherpe rechte rand tussen twee platte vlakken en beweeg voor de straal; klik, of typ de straal + Enter. (Zoals DesignSpark; in SketchUp zit dit niet.)"),
        new(ToolBarItemKind.Tool, Tool.FollowMe, null, "↝", "Ronddraaien en volgen (Follow Me): klik een plat vlak (het profiel), en dan een as (een rechte rand, losse lijn of hulplijn in dat vlak) om het rond te draaien – beweeg voor de hoek (stappen van 15°, of 360°) en klik, of typ de hoek + Enter – of een vlak waarvan de rand het pad is: het profiel loopt die rand rond (in het lichaam: weggesneden, zoals een afschuining; erbuiten: een lijst)."),
        new(ToolBarItemKind.Tool, Tool.Mirror, null, "⇋", "Spiegelen: klik een plat vlak; het lichaam spiegelt in dat vlak naar de andere kant. Ctrl+klik: een gespiegelde kopie erbij (twee helften van een symmetrisch onderdeel)."),
        new(ToolBarItemKind.Action, null, ToolAction.OuterShell, "◫", "Buitenschil (Outer Shell): alle delen die elkaar overlappen of met een vlak raken, in één keer samenvoegen. (Platte en ronde vlakken: cilinders, kegels, bollen, ringen; nog geen vrije vormen.)"),
        new(ToolBarItemKind.Tool, Tool.Union, null, "∪", "Verenigen (Union): klik een deel, klik een tweede deel dat het overlapt of met een vlak raakt: ze worden één. (Platte en ronde vlakken: cilinders, kegels, bollen, ringen; nog geen vrije vormen.)"),
        new(ToolBarItemKind.Tool, Tool.Subtract, null, "∖", "Aftrekken (Subtract): klik het deel waar iets af moet, klik het deel dat eraf gaat (dat verdwijnt). (Platte en ronde vlakken: cilinders, kegels, bollen, ringen; nog geen vrije vormen.)"),
        new(ToolBarItemKind.Tool, Tool.Intersect, null, "∩", "Doorsnede (Intersect): klik een deel en een tweede dat het overlapt: alleen wat ze gemeen hebben blijft over. (Platte en ronde vlakken: cilinders, kegels, bollen, ringen; nog geen vrije vormen.)"),
        new(ToolBarItemKind.Tool, Tool.Trim, null, "✂", "Wegsnijden (Trim): klik het deel dat bijgesneden wordt, klik het deel dat snijdt (dat blijft staan). (Platte en ronde vlakken: cilinders, kegels, bollen, ringen; nog geen vrije vormen.)"),
        new(ToolBarItemKind.Tool, Tool.Split, null, "⊘", "Splitsen (Split): klik twee delen die elkaar overlappen: ze worden drie delen (alleen het eerste, het gemeenschappelijke, alleen het tweede). (Platte en ronde vlakken: cilinders, kegels, bollen, ringen; nog geen vrije vormen.)"),
        new(ToolBarItemKind.Group, null, null, "METEN EN AANTEKENEN", ""),
        new(ToolBarItemKind.Tool, Tool.Tape, null, "📏", "Meetlint (T): klik een punt en beweeg om te meten; klik een rand en trek eraf voor een hulplijn (of typ de afstand + Enter). Delete: alle hulplijnen weg."),
        new(ToolBarItemKind.Tool, Tool.Dimension, null, "↔", "Maatlijn (Dimensions): klik twee punten (vastklikken op hoeken en randen), beweeg om de maatlijn ervan af te zetten en klik. Klik een maat om hem weg te halen. Alleen in beeld."),
        new(ToolBarItemKind.Tool, Tool.Protractor, null, "∡", "Gradenboog (Protractor): klik het middelpunt op een vlak, klik een punt voor de beginlijn, beweeg voor de hoek (stappen van 15°) en klik; of typ de hoek + Enter. Er komt een hulplijn onder die hoek."),
        new(ToolBarItemKind.Tool, Tool.Axes, null, "✛", "Assen (Axes): klik een nieuwe oorsprong, klik een punt in de richting van de rode as en een punt voor de groene as: vastklikken, rechthoeken en de pijltjes volgen die assen. Klik op een lege plek: de gewone assen terug."),
        new(ToolBarItemKind.Tool, Tool.Text, null, "💬", "Tekstlabel (Text): klik een punt op het model, typ de tekst en druk op Enter. Klik een label om het weg te halen. Alleen in beeld."),
        new(ToolBarItemKind.Tool, Tool.Section, null, "⊟", "Doorsnedevlak (Section Plane): klik een plat vlak, beweeg om het snijvlak het model in te schuiven en klik (of typ de diepte + Enter): alles ervoor wordt weggelaten, de snijlijn dik getekend. Esc (met ⊟ gekozen): weg ermee."),
        new(ToolBarItemKind.Tool, Tool.Hide, null, "🏷", "Verbergen (Tag): klik een deel om het te verbergen (bv. om erachter te werken); klik op een lege plek om alles weer te tonen. Alleen in beeld."),
        new(ToolBarItemKind.Tool, Tool.Text3D, null, "A", "3D-tekst (3D Text): klik een punt op een plat vlak, typ de tekst en druk op Enter: de letters worden vlakken (zo hoog als de maat in het vak, standaard een vijfde van het vlak). Typ daarna een dikte + Enter om ze er allemaal uit te trekken."),
        new(ToolBarItemKind.Group, null, null, "KLEUR, SELECTIE EN GUM", ""),
        new(ToolBarItemKind.Tool, Tool.Paint, null, "🎨", "Verfemmer (B): kies een kleur en klik een vlak. Ctrl: het hele lichaam · Shift: alle vlakken in dezelfde kleur · Alt: kleur overnemen."),
        new(ToolBarItemKind.Action, null, ToolAction.Sample, "💧", "Pipet (Sample Material): klik een vlak om zijn kleur over te nemen in de verfemmer (ook: Alt+klik met de verfemmer)"),
        new(ToolBarItemKind.Tool, Tool.Lasso, null, "➰", "Lasso: klik, trek een lus om vlakken heen en klik nog eens: die vlakken zijn gekozen (Shift: erbij, Ctrl: eraf). Daarna verplaatst ✥ ze samen en verft 🎨 ze in één keer. Esc: keuze leeg."),
        new(ToolBarItemKind.Tool, Tool.Eraser, null, "⌫", "Gum (E): klik een getekende lijn, rechthoek of cirkel weg; de vlakken worden weer één."),
        new(ToolBarItemKind.Group, null, null, "KIJKEN", ""),
        new(ToolBarItemKind.Action, null, ToolAction.ZoomExtents, "⛶", "Alles in beeld (Zoom Extents)"),
        new(ToolBarItemKind.Tool, Tool.Zoom, null, "🔍", "Zoomen (Zoom): klik, beweeg omhoog om in te zoomen of omlaag om uit te zoomen, klik om te stoppen. (Het wieltje zoomt ook.)"),
        new(ToolBarItemKind.Tool, Tool.ZoomWindow, null, "🔎", "Zoomvenster (Zoom Window): klik een hoek, klik de andere hoek: wat in het kader staat, vult het beeld."),
        new(ToolBarItemKind.Tool, Tool.PositionCamera, null, "📷", "Camera plaatsen (Position Camera): klik waar je wilt staan (op een vlak of de grond); je kijkt vanaf ooghoogte. Typ een andere ooghoogte (mm) + Enter. Daarna rondkijken."),
        new(ToolBarItemKind.Tool, Tool.LookAround, null, "👁", "Rondkijken (Look Around): klik en beweeg om je hoofd te draaien zonder van je plek te gaan; klik om te stoppen."),
        new(ToolBarItemKind.Tool, Tool.Walk, null, "🚶", "Lopen (Walk): ↑/↓ vooruit en achteruit, ←/→ draaien, PageUp/PageDown omhoog en omlaag; klik en beweeg om rond te kijken."),
    ];

    /// <summary>Does the action.</summary>
    public static void Do(this EditorSession s, ToolAction a)
    {
        switch (a)
        {
            case ToolAction.OuterShell: s.OuterShell(); break;
            case ToolAction.Sample: s.Sample(); break;
            case ToolAction.ZoomExtents: s.ZoomExtents(); break;
        }
    }
}
