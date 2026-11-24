namespace Stations.Core.Sim;

/// <summary>A research project: costs points to start, then takes researcher-hours to finish.</summary>
public sealed record ResearchDef(string Id, string Name, string Category, double Points, double Hours, string Effect, string? Requires);

public static class ResearchTree
{
    public static readonly ResearchDef[] All =
    {
        new("pay2", "Kartenzahlung am Automaten", "Bezahlen", 60, 24,
            "Stufe 2: An den Automaten auch mit Karte zahlen – weniger Bargeld beim Fahrer, schnellerer Einstieg, +3 % Fahrgäste, Schalter 15 % schneller", null),
        new("pay3", "Kontaktlos am Automaten, Automaten im Fahrzeug", "Bezahlen", 150, 60,
            "Stufe 3: Kontaktlos am Automaten, Bargeld-Automaten in den Fahrzeugen – Einstieg an allen Türen, +6 % Fahrgäste", "pay2"),
        new("pay4", "Kontaktlos im Fahrzeug", "Bezahlen", 300, 120,
            "Stufe 4: Kontaktlos direkt im Fahrzeug zahlen – schnellster Einstieg, +10 % Fahrgäste", "pay3"),
        new("diag", "Werkstatt-Diagnose", "Betriebshof", 80, 36,
            "Diagnosegeräte in den Werkstätten: Reparaturen 50 % schneller", null),
        new("clean", "Reinigungsplan", "Betriebshof", 60, 24,
            "Feste Reinigungspläne: Innenreinigung geparkter Fahrzeuge doppelt so schnell", null),
        new("market", "Abo-Marketing", "Kunden", 100, 48,
            "Werbung für Abos: 50 % mehr Kunden in den Kundencentern", "pay2"),
    };

    public static ResearchDef Get(string id) => All.First(r => r.Id == id);

    public static readonly string[] PaymentNames =
    {
        "Bargeld beim Fahrer",
        "Karte am Automaten",
        "Kontaktlos am Automaten, Bargeld-Automat im Fahrzeug",
        "Kontaktlos im Fahrzeug",
    };
}

/// <summary>
/// Research: points are earned in service (boardings, served customers) and spent to start a project.
/// The project then needs researcher-hours; every researcher at a desk in a planning office adds one hour
/// per hour, so more researchers finish sooner. One project runs at a time.
/// </summary>
public sealed partial class Game
{
    public const double PointsPerBoarding = 0.02;
    public const double PointsPerCustomer = 0.5;

    /// <summary>Score points available for research.</summary>
    public double Points;
    public readonly HashSet<string> Researched = new();
    public string? ActiveResearch;
    /// <summary>Researcher-hours done on the active project.</summary>
    public double ResearchProgress;
    /// <summary>Researchers at their desks right now (all depots).</summary>
    public int ResearchersAtWork { get; private set; }

    public bool Has(string id) => Researched.Contains(id);

    /// <summary>1 = cash at the driver … 4 = contactless in the vehicle.</summary>
    public int PaymentLevel => 1 + (Has("pay2") ? 1 : 0) + (Has("pay3") ? 1 : 0) + (Has("pay4") ? 1 : 0);

    /// <summary>Seconds a paying passenger needs to board (others 1.2 s).</summary>
    public double BoardSeconds => PaymentLevel switch { 1 => 2.4, 2 => 1.9, 3 => 1.4, _ => 1.1 };

    /// <summary>Extra demand from easier payment.</summary>
    public double PaymentBoost => PaymentLevel switch { 1 => 0, 2 => 0.03, 3 => 0.06, _ => 0.10 };

    /// <summary>Time at the customer-center counter relative to cash only.</summary>
    public double CounterTimeFactor => PaymentLevel switch { 1 => 1, 2 => 0.85, 3 => 0.75, _ => 0.7 };

    /// <summary>Why a project cannot be started now, or null.</summary>
    public string? ResearchBlocker(ResearchDef r)
    {
        if (Has(r.Id)) return "bereits erforscht";
        if (ActiveResearch == r.Id) return "läuft";
        if (r.Requires is string req && !Has(req)) return $"benötigt „{ResearchTree.Get(req).Name}“";
        if (ActiveResearch != null) return "es läuft schon ein Projekt";
        if (Points < r.Points) return $"{r.Points - Points:F0} Punkte fehlen";
        return null;
    }

    public string? StartResearch(string id)
    {
        var r = ResearchTree.Get(id);
        if (ResearchBlocker(r) is string err) return err;
        Points -= r.Points;
        ActiveResearch = id;
        ResearchProgress = 0;
        OnChange();
        return null;
    }

    /// <summary>Stop the active project; half of the points come back.</summary>
    public void CancelResearch()
    {
        if (ActiveResearch == null) return;
        Points += ResearchTree.Get(ActiveResearch).Points / 2;
        ActiveResearch = null;
        ResearchProgress = 0;
        OnChange();
    }

    private void StepResearch(double dt)
    {
        ResearchersAtWork = People.Values.Count(p => p.Role == StaffRole.Researcher && p.Act == PersonAct.Working && p.Post != null);
        if (ActiveResearch == null) return;
        ResearchProgress += ResearchersAtWork * dt / 3600;
        var r = ResearchTree.Get(ActiveResearch);
        if (ResearchProgress < r.Hours) return;
        Researched.Add(r.Id);
        ActiveResearch = null;
        ResearchProgress = 0;
        Notify($"Forschung abgeschlossen: {r.Name}");
        OnChange();
    }

    /// <summary>Game hours until the active project is done at the current staffing (∞ without researchers).</summary>
    public double ResearchEta => ActiveResearch is string id && ResearchersAtWork > 0
        ? (ResearchTree.Get(id).Hours - ResearchProgress) / ResearchersAtWork
        : double.PositiveInfinity;

    /// <summary>Researchers on duty to the desks of the planning offices (spares rest in the break room).</summary>
    private void AssignDesks(Depot d, YardInfo yard, bool instant)
    {
        var L = d.Layout;
        var desks = yard.Desks;
        var staff = People.Values.Where(p => p.DepotId == d.Id && p.Role == StaffRole.Researcher && !Leaving(p)).OrderBy(p => p.Id).ToList();
        for (int i = 0; i < staff.Count; i++) staff[i].Post = i < desks.Count ? desks[i] : null;
        foreach (var p in staff)
        {
            if (Arriving(p)) continue;
            if (p.Post is { } post && ModuleAt(d, post.X, post.Y) is { } m && yard.DeskPlans.TryGetValue(m, out var plan))
            {
                var door = Door(d, m).Dir;
                var (dx, dz) = plan.Desks[post.Bay];
                var (x, y) = L.OfficePoint(m, door, dx, dz + DeskPlan.ChairOffset);
                double face = DepotLayout.OfficeAngle(door, 0, -1); // toward the desk
                p.Seat = post.Bay;
                if (instant)
                {
                    Place(p, (x, y, face), PersonAct.Working);
                    p.Office = p.TargetOffice = m;
                    p.Seat = post.Bay;
                }
                else if (Far(p, (x, y, face))) { WalkTo(p, PersonGoal.Post, (x, y), m, face); p.Seat = post.Bay; }
                else if (p.Act != PersonAct.Walking) p.Act = PersonAct.Working;
                continue;
            }
            if (p.Goal == PersonGoal.None && (p.Act != PersonAct.Resting || p.Office == null || !yard.Offices.Contains(p.Office)))
            {
                if (instant) PlaceAtRest(d, p);
                else GoRest(d, p);
            }
        }
    }
}
