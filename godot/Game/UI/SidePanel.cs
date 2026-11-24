using Godot;
using Stations.Core;
using Stations.Core.Sim;

namespace Stations.UI;

/// <summary>
/// Right-hand panel showing the selection or the active tool. It is rebuilt only on structural
/// changes (selection, tool, network edits); live values update through bound labels, so
/// text fields and drop-downs keep their focus.
/// </summary>
public partial class SidePanel : PanelContainer
{
    public Interaction UI = null!;
    private Game G => UI.Game;

    private VBoxContainer _box = null!;
    private ScrollContainer _scroll = null!;
    private readonly List<Action> _binds = new();
    private bool _dirty = true;
    private double _timer;
    private string _lineTab = "overview";

    /// <summary>Switch the line panel tab (also used by developer automation).</summary>
    public void ShowTab(string tab)
    {
        _lineTab = tab;
        _dirty = true;
    }

    public override void _Ready()
    {
        Name = "SidePanel";
        // Explicit anchors/offsets: SetAnchorsPreset would keep the old position and stretch the panel.
        AnchorLeft = 1;
        AnchorRight = 1;
        OffsetLeft = -360;
        OffsetRight = -10;
        OffsetTop = 78;
        GrowHorizontal = GrowDirection.Begin;
        CustomMinimumSize = new Vector2(350, 0);
        AddThemeStyleboxOverride("panel", Ui.CardStyle(10, 14));
        _scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        AddChild(_scroll);
        _box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _box.AddThemeConstantOverride("separation", 6);
        _scroll.AddChild(_box);
        UI.StateChanged += MarkDirty;
        G.Changed += MarkDirty;
    }

    public override void _ExitTree()
    {
        UI.StateChanged -= MarkDirty;
        G.Changed -= MarkDirty;
    }

    private void MarkDirty() => _dirty = true;

    public override void _Process(double delta)
    {
        // Fit the scroll area to the window height
        float maxH = GetViewportRect().Size.Y - OffsetTop - 20;
        _scroll.CustomMinimumSize = new Vector2(0, Mathf.Min(_box.GetCombinedMinimumSize().Y, maxH));
        Size = new Vector2(Size.X, 0);

        if (_dirty && !IsEditing())
        {
            _dirty = false;
            Rebuild();
        }
        _timer += delta;
        if (_timer < 0.4) return;
        _timer = 0;
        foreach (var b in _binds) b();
    }

    /// <summary>True while a text field in the panel has focus (don't pull it away).</summary>
    private bool IsEditing() => GetViewport().GuiGetFocusOwner() is LineEdit le && IsAncestorOf(le);

    // ---------------------------------------------------------------- building blocks

    private T Add<T>(T c) where T : Control
    {
        _box.AddChild(c);
        return c;
    }

    private void Bind(Label l, Func<string> text)
    {
        l.Text = text();
        _binds.Add(() => l.Text = text());
    }

    private void Header(string title, Control? badge = null, bool closable = true)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        if (badge != null) row.AddChild(badge);
        var l = Ui.Label(title, 17, bold: true);
        l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(l);
        if (closable)
        {
            var x = new Button { Text = "×", Flat = true };
            x.Pressed += () => UI.Select(null);
            row.AddChild(x);
        }
        Add(row);
    }

    private void Hint(string text)
    {
        var l = Ui.Label(text, 13, Ui.Muted);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(320, 0);
        Add(l);
    }

    private void Warn(string text)
    {
        var p = new PanelContainer();
        p.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color("fff4e5"), CornerRadiusTopLeft = 7, CornerRadiusTopRight = 7,
            CornerRadiusBottomLeft = 7, CornerRadiusBottomRight = 7,
            ContentMarginLeft = 9, ContentMarginRight = 9, ContentMarginTop = 5, ContentMarginBottom = 5,
        });
        var l = Ui.Label("⚠ " + text, 13, new Color("8a4b00"));
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        p.AddChild(l);
        Add(p);
    }

    private HFlowContainer Actions()
    {
        var a = new HFlowContainer();
        a.AddThemeConstantOverride("h_separation", 6);
        a.AddThemeConstantOverride("v_separation", 6);
        return Add(a);
    }

    private LineEdit NameField(string value, Action<string> set)
    {
        var e = new LineEdit { Text = value, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        e.AddThemeFontSizeOverride("font_size", 15);
        void Commit()
        {
            string v = e.Text.Trim();
            if (v != "") set(v);
            e.ReleaseFocus();
        }
        e.TextSubmitted += _ => Commit();
        e.FocusExited += () => { string v = e.Text.Trim(); if (v != "") set(v); };
        return Add(e);
    }

    private static Color LineColor(TransitLine l) => new(l.Color);

    private void Space(float h = 6) => Add(new Control { CustomMinimumSize = new Vector2(0, h) });

    // ---------------------------------------------------------------- rebuild

    private void Rebuild()
    {
        _binds.Clear();
        foreach (var c in _box.GetChildren())
        {
            _box.RemoveChild(c);
            c.QueueFree();
        }
        switch (UI.Tool)
        {
            case Tool.Line: DraftPanel(); return;
            case Tool.Track: TrackPanel(); return;
            case Tool.Edit when UI.EditLineId is int eid && G.Lines.ContainsKey(eid): EditPanel(G.Lines[eid]); return;
        }
        switch (UI.Selected)
        {
            case { Kind: "stop" } s when G.Stops.TryGetValue(s.Id, out var stop): StopPanel(stop); break;
            case { Kind: "line" } s when G.Lines.TryGetValue(s.Id, out var line): LinePanel(line); break;
            case { Kind: "vehicle" } s when G.Vehicles.TryGetValue(s.Id, out var v): VehiclePanel(v); break;
            case { Kind: "depot" } s when G.Depots.TryGetValue(s.Id, out var d): DepotPanel(d); break;
            default: OverviewPanel(); break;
        }
    }

    // ---------------------------------------------------------------- overview

    private void OverviewPanel()
    {
        Header(G.Map.Area.Name.Split(',')[0], closable: false);
        var kv = new Ui.KeyValues(_box);
        kv.Add("Einwohner im Gebiet", Util.Num(G.Map.TotalResidents));
        kv.Add("Arbeitsplätze", Util.Num(G.Map.TotalJobs));
        Bind(kv.Add("Erschlossen (≤ 500 m)"), () =>
            $"{(G.Map.TotalResidents > 0 ? Math.Round((G.Router?.CoveredRes ?? 0) / G.Map.TotalResidents * 100) : 0)} %");
        Bind(kv.Add("Einstiege heute"), () => Util.Num(G.Today.Boardings));
        Bind(kv.Add("Ø Wartezeit"), () => Util.Duration(G.Today.Boardings > 0 ? G.Today.WaitSum / G.Today.Boardings : double.NaN));
        Bind(kv.Add("Aufgegeben heute"), () => Util.Num(G.Today.Lost));
        if (G.History.Count > 0)
        {
            var y = G.History[^1];
            kv.Add("Gestern: Fahrgäste", Util.Num(y.Trips));
            var res = kv.Add("Gestern: Ergebnis", Util.Money(y.Revenue - y.Costs));
            res.AddThemeColorOverride("font_color", y.Revenue - y.Costs < 0 ? Ui.Danger : Ui.Positive);
        }

        if (G.Lines.Count == 0)
        {
            Space();
            Add(Ui.Section("Erste Schritte"));
            Hint("1. Baue einen Betriebshof (B)\n2. Zeichne eine Linie (L) – klicke dafür nacheinander auf Straßen\n" +
                 "3. Passe im Linien-Panel unter „Fahrplan“ Betriebszeit und Takt an\n4. Prüfe mit „Einzugsgebiet“ (C), welche Häuser erschlossen sind\n\n" +
                 "Für Trams: Modus „Tram“ wählen und zuerst Gleise bauen (G).");
        }

        var warnings = new List<(string Text, Action Go, Control? Badge)>();
        foreach (var l in G.Lines.Values.OrderBy(l => l.Id))
            foreach (var w in LineWarnings(l)) warnings.Add((w, () => UI.Select(new("line", l.Id)), Ui.Badge(l.Name, LineColor(l))));
        foreach (var d in G.Depots.Values.OrderBy(d => d.Id))
        {
            int missing = G.DriverDemand(d.Id) - G.DriversOf(d.Id).Count;
            if (missing > 0) warnings.Add(($"{d.Name}: {missing} Fahrer fehlen", () => UI.Select(new("depot", d.Id)), null));
        }
        if (warnings.Count > 0)
        {
            Space();
            Add(Ui.Section("Hinweise"));
            foreach (var (text, go, badge) in warnings)
                Ui.Row(_box, text, "", go, badge).GetParent().GetChild<Label>(badge == null ? 0 : 1).AddThemeColorOverride("font_color", new Color("8a4b00"));
        }

        Space();
        Add(Ui.Section("Linien"));
        if (G.Lines.Count == 0) Hint("Noch keine Linien.");
        foreach (var l in G.Lines.Values.OrderBy(l => l.Id))
        {
            string from = G.Stops.TryGetValue(l.StopIds[0], out var a) ? a.Name : "";
            string to = G.Stops.TryGetValue(l.StopIds[l.Circular ? 0 : ^1], out var b) ? b.Name : "";
            var meta = Ui.Row(_box, $"{from} – {to}", "", () => FocusLine(l), Ui.Badge(l.Name, LineColor(l)));
            Bind(meta, () => $"{G.VehiclesOf(l).Count} Fzg · {Util.Num(l.PassengersToday)}");
        }
        Actions().AddChild(Ui.Btn("+ Neue Linie", () => UI.SetTool(Tool.Line), primary: true));

        Space();
        Add(Ui.Section("Betriebshöfe"));
        if (G.Depots.Count == 0) Hint("Noch kein Betriebshof.");
        foreach (var d in G.Depots.Values.OrderBy(d => d.Id))
            Ui.Row(_box, d.Name, $"{G.Vehicles.Values.Count(v => v.DepotId == d.Id)} Fahrzeuge", () => UI.Select(new("depot", d.Id)));
    }

    private void FocusLine(TransitLine l)
    {
        UI.Select(new("line", l.Id));
        var p = l.Runs[0].Path;
        UI.Map.CenterOn(new Vector2((float)p.Xs.Average(), (float)p.Ys.Average()));
    }

    /// <summary>Problems of a line the player should fix.</summary>
    private List<string> LineWarnings(TransitLine l)
    {
        var w = new List<string>();
        if (!G.Depots.Values.Any(d => d.Mode == l.Mode))
            w.Add(l.Mode == ModeId.Tram ? "Kein Tram-Betriebshof – baue einen an den Gleisen" : "Kein Betriebshof – baue einen, um Busse einzusetzen");
        int missing = G.PeakVehicles(l) - G.VehiclesOf(l).Count;
        var word = l.Mode == ModeId.Tram ? ("Tram fehlt", "Trams fehlen") : ("Bus fehlt", "Busse fehlen");
        if (missing > 0) w.Add($"{missing} {(missing == 1 ? word.Item1 : word.Item2)} für den Fahrplan");
        if (l.DriverShortage) w.Add("Fahrermangel – Fahrzeuge bleiben im Betriebshof");
        return w;
    }

    // ---------------------------------------------------------------- tools

    private void DraftPanel()
    {
        var color = new Color(Util.LineColors[G.Lines.Count % Util.LineColors.Length]);
        Header($"{Modes.Get(UI.Mode).Name}linie zeichnen", Ui.Badge("neu", color), closable: false);
        Hint("Klicke nacheinander auf Haltestellen oder Straßen. Neue Haltestellen werden automatisch gesetzt.");
        double len = UI.DraftPaths.Sum(p => Enumerable.Range(0, p.Xs.Count - 1).Sum(i => Util.Hypot(p.Xs[i + 1] - p.Xs[i], p.Ys[i + 1] - p.Ys[i])));
        double time = UI.DraftPaths.Sum(p => p.Time);
        var kv = new Ui.KeyValues(_box);
        kv.Add("Haltestellen", UI.DraftStops.Count.ToString());
        kv.Add("Länge (eine Richtung)", Util.Dist(len));
        kv.Add("Fahrzeit (ohne Halte)", Util.Duration(time / 0.9));
        int i = 1;
        foreach (var s in UI.DraftStops) Ui.Row(_box, $"{i++}.  {s.Name}");
        var circ = new CheckBox { Text = "Ringlinie", ButtonPressed = UI.DraftCircular };
        circ.Toggled += UI.SetDraftCircular;
        Add(circ);
        var a = Actions();
        a.AddChild(Ui.Btn("✓ Fertig (Enter)", UI.FinishDraft, primary: true, disabled: UI.DraftStops.Count < 2));
        a.AddChild(Ui.Btn("Abbrechen", UI.CancelDraft));
    }

    private void TrackPanel()
    {
        Header("Gleise bauen", closable: false);
        Hint("Gleise liegen auf Straßen und können in beide Richtungen befahren werden. Klicke einen Startpunkt, dann weitere Punkte – das Gleis folgt dem kürzesten Straßenweg.");
        var kv = new Ui.KeyValues(_box);
        kv.Add("Kosten", $"{Util.Money(Modes.Get(ModeId.Tram).TrackCostPerM * 1000)}/km");
        kv.Add("Gleisnetz gesamt", Util.Dist(G.TrackLength));
        if (UI.TrackStart != null)
        {
            var plan = UI.TrackPlan;
            kv.Add("Geplant (neu)", plan == null ? "–" : Util.Dist(plan.NewLength));
            var cost = kv.Add("Baukosten", plan == null ? "–" : Util.Money(plan.Cost));
            if (plan != null && !G.CanAfford(plan.Cost)) cost.AddThemeColorOverride("font_color", Ui.Danger);
        }
        Hint(UI.TrackStart != null ? "Klicke den nächsten Punkt. Rechtsklick oder Esc setzt neu an." : "Klicke auf eine Straße, um zu beginnen.");
        Hint("Danach: Tram-Haltestellen (H) und einen Tram-Betriebshof (B) auf die Gleise setzen, dann eine Linie (L) zeichnen.");
    }

    private void EditPanel(TransitLine l)
    {
        Header("Strecke bearbeiten", Ui.Badge(l.Name, LineColor(l)), closable: false);
        Hint("Klick auf eine Straße oder Haltestelle fügt sie an der passenden Stelle ein. Klick auf eine Haltestelle der Linie nimmt sie heraus. Fahrzeuge fahren ohne Unterbrechung weiter.");
        var kv = new Ui.KeyValues(_box);
        kv.Add("Haltestellen", l.StopIds.Count.ToString());
        kv.Add("Streckenlänge (Umlauf)", Util.Dist(l.Runs.Sum(r => r.Path.Length)));
        kv.Add("Umlaufzeit", Util.Duration(l.CycleTime));
        var circ = new CheckBox { Text = "Ringlinie", ButtonPressed = l.Circular };
        circ.Toggled += v => G.SetCircular(l, v);
        Add(circ);
        int n = l.StopIds.Count;
        for (int i = 0; i < n; i++)
        {
            int idx = i;
            var row = new HBoxContainer();
            var name = Ui.Label($"{i + 1}.  {(G.Stops.TryGetValue(l.StopIds[i], out var s) ? s.Name : "?")}", 13);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            name.ClipText = true;
            row.AddChild(name);
            row.AddChild(SmallBtn("↑", () => UI.EditStopAt(idx, -1), i == 0));
            row.AddChild(SmallBtn("↓", () => UI.EditStopAt(idx, 1), i == n - 1));
            row.AddChild(SmallBtn("×", () => UI.EditStopAt(idx, 0), n <= 2));
            Add(row);
        }
        Actions().AddChild(Ui.Btn("✓ Fertig (Enter)", () => UI.SetTool(Tool.Select), primary: true));
    }

    private static Button SmallBtn(string text, Action a, bool disabled)
    {
        var b = new Button { Text = text, Disabled = disabled, CustomMinimumSize = new Vector2(28, 0) };
        b.Pressed += a;
        return b;
    }

    // ---------------------------------------------------------------- stop

    private void StopPanel(Stop s)
    {
        Header(s.Mode == ModeId.Tram ? "Tram-Haltestelle" : "Haltestelle");
        NameField(s.Name, v => s.Name = v);
        var kv = new Ui.KeyValues(_box);
        Bind(kv.Add("Wartende Fahrgäste"), () => s.Waiting.Count.ToString());
        Bind(kv.Add("Einstiege heute"), () => Util.Num(s.BoardedToday));
        Space();
        Add(Ui.Section("Linien"));
        var lines = G.LinesAtStop(s.Id);
        if (lines.Count == 0) Hint("Keine Linie hält hier.");
        foreach (var l in lines)
            Bind(Ui.Row(_box, "", "", () => UI.Select(new("line", l.Id)), Ui.Badge(l.Name, LineColor(l))), () => $"alle {Util.Duration(G.Headway(l))}");
        Actions().AddChild(Ui.Btn("Abreißen", UI.DeleteSelected, danger: true));
    }

    // ---------------------------------------------------------------- line

    private void LinePanel(TransitLine l)
    {
        Header($"{Modes.Get(l.Mode).Name}linie", Ui.Badge(l.Name, LineColor(l)));
        foreach (var w in LineWarnings(l)) Warn(w);
        var tabs = new HBoxContainer();
        tabs.AddThemeConstantOverride("separation", 4);
        foreach (var (id, label) in new[] { ("overview", "Übersicht"), ("timetable", "Fahrplan"), ("stops", "Strecke") })
        {
            var b = new Button { Text = label, ToggleMode = true, ButtonPressed = _lineTab == id, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            b.Pressed += () => { _lineTab = id; _dirty = true; };
            tabs.AddChild(b);
        }
        Add(tabs);
        switch (_lineTab)
        {
            case "timetable": LineTimetable(l); break;
            case "stops": LineStops(l); break;
            default: LineOverview(l); break;
        }
    }

    private void LineOverview(TransitLine l)
    {
        NameField(l.Name, v => { l.Name = v; _dirty = true; });
        var swatches = new HFlowContainer();
        swatches.AddThemeConstantOverride("h_separation", 4);
        foreach (string c in Util.LineColors)
        {
            var b = new Button { CustomMinimumSize = new Vector2(24, 24), TooltipText = c };
            var st = Ui.ButtonStyle(new Color(c));
            st.SetCornerRadiusAll(12);
            if (c == l.Color) { st.BorderColor = Ui.Text; st.SetBorderWidthAll(3); }
            b.AddThemeStyleboxOverride("normal", st);
            b.AddThemeStyleboxOverride("hover", st);
            b.AddThemeStyleboxOverride("pressed", st);
            b.Pressed += () => { l.Color = c; _dirty = true; };
            swatches.AddChild(b);
        }
        Add(swatches);

        var kv = new Ui.KeyValues(_box);
        Bind(kv.Add("Jetzt"), () => l.Timetable.PeriodAt(G.Time) is int pi and >= 0 ? Timetable.Periods[pi].Name : "Betriebsruhe");
        Bind(kv.Add("Takt aktuell"), () => double.IsFinite(G.Headway(l)) ? "alle " + Util.Duration(G.Headway(l)) : "–");
        Bind(kv.Add("Im Einsatz"), () => $"{G.ActiveVehicles(l)} / {G.RequiredVehicles(l)} benötigt");
        int cap = Modes.Get(l.Mode).Vehicle.Capacity;
        Bind(kv.Add("Kapazität"), () => $"{Util.Num(double.IsFinite(G.Headway(l)) ? 3600 / G.Headway(l) * cap : 0)} Plätze/h");
        kv.Add("Umlaufzeit", Util.Duration(l.CycleTime));
        kv.Add("Streckenlänge (Umlauf)", Util.Dist(l.Runs.Sum(r => r.Path.Length)));
        Bind(kv.Add("Einstiege heute"), () => Util.Num(l.PassengersToday));
        Bind(kv.Add("Einnahmen heute"), () => Util.Money(l.RevenueToday));

        Space();
        Add(Ui.Section("Fahrzeugpool"));
        var pool = G.VehiclesOf(l);
        var step = new HBoxContainer();
        step.AddThemeConstantOverride("separation", 8);
        step.AddChild(Ui.Btn("−", () => G.RemoveVehicleFromLine(l), disabled: pool.Count == 0));
        step.AddChild(Ui.Label(pool.Count.ToString(), 16, bold: true));
        step.AddChild(Ui.Btn("+", () => G.AddVehicleToLine(l)));
        step.AddChild(Ui.Label($"Spitzenbedarf: {G.PeakVehicles(l)}", 13, Ui.Muted));
        Add(step);
        foreach (var v in pool)
            Bind(Ui.Row(_box, v.Name, "", () => UI.Select(new("vehicle", v.Id))),
                () => v.State == VehicleState.Depot ? "Reserve" : $"{v.Passengers.Count}/{cap} · {VehicleStatus(v)}");
        Actions().AddChild(Ui.Btn("Linie löschen", UI.DeleteSelected, danger: true));
    }

    private void LineTimetable(TransitLine l)
    {
        var tt = l.Timetable;
        var hours = new HBoxContainer();
        hours.AddThemeConstantOverride("separation", 6);
        hours.AddChild(Ui.Label("Betrieb von", 13));
        double[] starts = { 4, 4.5, 5, 5.5, 6, 6.5, 7, 8 };
        double[] ends = { 19, 20, 21, 22, 23, 24, 24.5, 25, 26 };
        hours.AddChild(Choice(starts.Select(h => Timetable.FmtHour(h)).ToArray(), Array.IndexOf(starts, tt.Start), i => SetTt(l, t => t.Start = starts[i])));
        hours.AddChild(Ui.Label("bis", 13));
        hours.AddChild(Choice(ends.Select(h => Timetable.FmtHour(h) + (h >= 24 ? " (+1)" : "")).ToArray(), Array.IndexOf(ends, tt.End), i => SetTt(l, t => t.End = ends[i])));
        Add(hours);

        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 4);
        foreach (var h in new[] { "ZEITRAUM", "TAKT", "FAHRZ." }) grid.AddChild(Ui.Label(h, 11, Ui.Muted, bold: true));
        int cur = tt.PeriodAt(G.Time);
        var opts = new[] { "kein Betrieb" }.Concat(Timetable.HeadwayOptions.Select(s => $"alle {(s % 60 == 0 ? (s / 60).ToString() : (s / 60).ToString("0.0"))} min")).ToArray();
        for (int i = 0; i < Timetable.Periods.Length; i++)
        {
            if (tt.PeriodBounds(i) is not { } b) continue;
            int pi = i;
            var name = Ui.Label($"{Timetable.Periods[i].Name}\n{Timetable.FmtHour(b.From)}–{Timetable.FmtHour(b.To)}", 12, i == cur ? Ui.Accent : Ui.Text);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            grid.AddChild(name);
            int sel = tt.Headways[i] == 0 ? 0 : Array.IndexOf(Timetable.HeadwayOptions, tt.Headways[i]) + 1;
            grid.AddChild(Choice(opts, sel, k => SetTt(l, t => t.Headways[pi] = k == 0 ? 0 : Timetable.HeadwayOptions[k - 1])));
            var n = Ui.Label(tt.Headways[i] > 0 ? Timetable.VehiclesFor(l.CycleTime, tt.Headways[i]).ToString() : "–", 13, bold: true);
            n.HorizontalAlignment = HorizontalAlignment.Center;
            grid.AddChild(n);
        }
        Add(grid);

        var kv = new Ui.KeyValues(_box);
        kv.Add("Umlaufzeit", Util.Duration(l.CycleTime));
        kv.Add("Spitzenbedarf", $"{G.PeakVehicles(l)} Fahrzeuge");
        kv.Add("Im Pool", $"{G.VehiclesOf(l).Count} Fahrzeuge");
        kv.Add("Fahrzeugstunden/Tag", $"{Util.Num(tt.VehicleHours(l.CycleTime))} h");
        int missing = G.PeakVehicles(l) - G.VehiclesOf(l).Count;
        var a = Actions();
        if (missing > 0)
            a.AddChild(Ui.Btn(missing == 1 ? "Fehlendes Fahrzeug zuweisen" : $"{missing} fehlende Fahrzeuge zuweisen", () => G.AssignMissing(l), primary: true));
        a.AddChild(Ui.Btn("Fehlende Fahrer einstellen", () =>
        {
            int n = G.HireMissingDrivers();
            Toast(n > 0 ? $"{n} Fahrer eingestellt" : "Es fehlen keine Fahrer");
        }));
        Hint("Die Disposition schickt nur so viele Fahrzeuge aus dem Pool los, wie der Takt braucht. Überzählige fahren an der Endhaltestelle zurück in den Betriebshof.");
    }

    private void SetTt(TransitLine l, Action<Timetable> change)
    {
        var tt = l.Timetable.Clone();
        change(tt);
        if (tt.End <= tt.Start)
        {
            Toast("Das Betriebsende muss nach dem Betriebsbeginn liegen", true);
            _dirty = true;
            return;
        }
        G.SetTimetable(l, tt);
    }

    private static OptionButton Choice(string[] items, int selected, Action<int> onSelect)
    {
        var o = new OptionButton();
        foreach (var s in items) o.AddItem(s);
        o.Selected = Math.Max(selected, 0);
        o.ItemSelected += i => onSelect((int)i);
        return o;
    }

    private void LineStops(TransitLine l)
    {
        var circ = new CheckBox { Text = "Ringlinie", ButtonPressed = l.Circular };
        circ.Toggled += v => G.SetCircular(l, v);
        Add(circ);
        Actions().AddChild(Ui.Btn("✎ Strecke bearbeiten", () => UI.StartEdit(l.Id)));
        int i = 1;
        foreach (int sid in l.StopIds)
        {
            if (!G.Stops.TryGetValue(sid, out var s)) continue;
            Bind(Ui.Row(_box, $"{i++}.  {s.Name}", "", () => { UI.Select(new("stop", sid)); UI.Map.CenterOn(new Vector2((float)s.Pos.X, (float)s.Pos.Y)); }),
                () => $"{s.Waiting.Count} wartend");
        }
    }

    // ---------------------------------------------------------------- vehicle

    private static string VehicleStatus(Vehicle v) => v.State switch
    {
        VehicleState.Depot => "Im Betriebshof",
        VehicleState.Deadhead => v.DeadheadTo switch
        {
            DeadheadTarget.Depot => "Fährt zum Betriebshof",
            DeadheadTarget.Rejoin => "Umleitung",
            _ => "Fährt zum Linienstart",
        },
        VehicleState.Layover => "Wendezeit",
        VehicleState.Moving => "Unterwegs",
        _ => "An Haltestelle",
    };

    private void VehiclePanel(Vehicle v)
    {
        var def = Modes.Get(v.Mode).Vehicle;
        Header("Fahrzeug");
        NameField(v.Name, n => v.Name = n);
        var kv = new Ui.KeyValues(_box);
        kv.Add("Typ", def.Name);
        Bind(kv.Add("Status"), () =>
        {
            var line = v.LineId is int lid && G.Lines.TryGetValue(lid, out var l) ? l : null;
            if (v.State == VehicleState.Depot && line != null) return "Reserve im Betriebshof";
            string st = VehicleStatus(v);
            if (line != null && v.State is VehicleState.Moving or VehicleState.Dwell && v.RunIdx < line.Runs.Count
                && v.StopIdx < line.Runs[v.RunIdx].StopIds.Count && G.Stops.TryGetValue(line.Runs[v.RunIdx].StopIds[v.StopIdx], out var next))
                st += $" – {next.Name}";
            return st;
        });
        Bind(kv.Add("Fahrer"), () => v.DriverId is int did && G.Drivers.TryGetValue(did, out var d) ? $"{d.Name} ({Shift(d.Worked)})" : "–");
        Bind(kv.Add("Geschwindigkeit"), () => $"{Math.Round(v.V * 3.6)} km/h");
        Bind(kv.Add("Fahrgäste"), () => $"{v.Passengers.Count} / {def.Capacity}");
        Bind(kv.Add("Laufleistung"), () => Util.Dist(v.KmTotal * 1000));
        kv.Add("Betriebshof", G.Depots.TryGetValue(v.DepotId, out var dep) ? dep.Name : "–");

        Space();
        Add(Ui.Section("Linie"));
        var lines = G.Lines.Values.Where(l => l.Mode == v.Mode).OrderBy(l => l.Id).ToList();
        var names = new[] { "– keine (im Betriebshof) –" }.Concat(lines.Select(l => $"Linie {l.Name}")).ToArray();
        int sel = v.LineId is int cur ? lines.FindIndex(l => l.Id == cur) + 1 : 0;
        Add(Choice(names, sel, i => G.AssignVehicle(v.Id, i == 0 ? null : lines[i - 1].Id)));
        var a = Actions();
        if (v.LineId != null) a.AddChild(Ui.Btn("Zum Betriebshof", () => G.AssignVehicle(v.Id, null)));
        if (v.State == VehicleState.Depot)
            a.AddChild(Ui.Btn($"Verkaufen ({Util.Money(def.Price * 0.5)})", () => { G.SellVehicle(v.Id); UI.Select(null); }, danger: true));
    }

    private static string Shift(double s) => $"{(int)(s / 3600)}:{(int)(s % 3600 / 60):00} h";

    // ---------------------------------------------------------------- depot

    private void DepotPanel(Depot d)
    {
        var def = Modes.Get(d.Mode).Vehicle;
        Header(d.Mode == ModeId.Tram ? "Tram-Betriebshof" : "Betriebshof");
        NameField(d.Name, n => { d.Name = n; _dirty = true; });
        var vs = G.Vehicles.Values.Where(v => v.DepotId == d.Id).OrderBy(v => v.Id).ToList();
        var kv = new Ui.KeyValues(_box);
        var spots = kv.Add(d.Mode == ModeId.Tram ? "Abstellgleise (belegt)" : "Stellplätze (belegt)", $"{vs.Count} / {d.Layout.VehicleCapacity(d.Mode)}");
        if (vs.Count >= d.Layout.VehicleCapacity(d.Mode)) spots.AddThemeColorOverride("font_color", Ui.Danger);
        Bind(kv.Add("Davon im Hof"), () => vs.Count(v => v.State == VehicleState.Depot).ToString());
        kv.Add("Fahrerplätze", $"{G.DriversOf(d.Id).Count} / {d.Layout.DriverCapacity()}");
        Actions().AddChild(Ui.Btn("Betriebshof bearbeiten (3D)", () => UI.EditDepot(d.Id)));
        Actions().AddChild(Ui.Btn($"+ {def.Name} kaufen ({Util.Money(def.Price)})", () =>
        {
            if (G.BuyVehicle(d.Id) is Vehicle nv) Toast($"{nv.Name} gekauft");
        }, primary: true, disabled: !G.CanAfford(def.Price) || G.FreeSpots(d) <= 0));

        // Staff
        Space();
        Add(Ui.Section("Personal"));
        var ds = G.DriversOf(d.Id);
        int demand = G.DriverDemand(d.Id);
        var skv = new Ui.KeyValues(_box);
        skv.Add("Fahrer", ds.Count.ToString());
        Bind(skv.Add("Im Dienst / frei / Feierabend"), () =>
        {
            int on = ds.Count(x => x.VehicleId != null);
            int done = ds.Count(x => x.VehicleId == null && x.Worked >= Game.ShiftMax - Game.ShiftMinLeft);
            return $"{on} / {ds.Count - on - done} / {done}";
        });
        var dem = skv.Add("Bedarf (Schätzung)", demand.ToString());
        if (demand > ds.Count) dem.AddThemeColorOverride("font_color", Ui.Danger);
        skv.Add("Lohnkosten", $"{Util.Money(ds.Count * Game.DriverWageDay)}/Tag");
        var a = Actions();
        a.AddChild(Ui.Btn("+1 Fahrer", () => G.HireDrivers(d.Id, 1)));
        a.AddChild(Ui.Btn("+5", () => G.HireDrivers(d.Id, 5)));
        if (demand > ds.Count) a.AddChild(Ui.Btn($"{demand - ds.Count} fehlende einstellen", () => G.HireMissingDrivers(d.Id), primary: true));
        a.AddChild(Ui.Btn("−1 entlassen", () => G.FireDriver(d.Id), disabled: ds.Count == 0));
        Hint($"Einstellung {Util.Money(Game.DriverHireCost)} · Schicht max. 8 h · Ablösung an der Endhaltestelle");
        foreach (var dr in ds.OrderByDescending(x => x.VehicleId != null).Take(25))
            Bind(Ui.Row(_box, dr.Name), () =>
            {
                string st = dr.VehicleId is int vid ? $"im Dienst · {(G.Vehicles.TryGetValue(vid, out var vv) ? vv.Name : "")}"
                    : dr.Worked >= Game.ShiftMax - Game.ShiftMinLeft ? "Feierabend" : dr.Worked > 0 ? "Pause" : "verfügbar";
                return $"{st} · {Shift(dr.Worked)}";
            });
        if (ds.Count > 25) Hint($"… und {ds.Count - 25} weitere");

        Space();
        Add(Ui.Section("Fahrzeuge"));
        if (vs.Count == 0) Hint("Keine Fahrzeuge. Kaufe eins oder nutze „+“ im Linien-Panel.");
        foreach (var v in vs)
        {
            var line = v.LineId is int lid && G.Lines.TryGetValue(lid, out var l) ? l : null;
            Ui.Row(_box, v.Name, line == null ? "frei" : "", () => UI.Select(new("vehicle", v.Id)), null);
            if (line != null) ((HBoxContainer)_box.GetChild(_box.GetChildCount() - 1).GetChild(0)).AddChild(Ui.Badge(line.Name, LineColor(line)));
        }
        Actions().AddChild(Ui.Btn("Abreißen", UI.DeleteSelected, danger: true));
    }

    private void Toast(string msg, bool error = false) => UI.EmitNotify(msg, error);
}
