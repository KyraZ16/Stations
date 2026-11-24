namespace Stations.Core.Sim;

/// <summary>
/// Customer centers: passengers come in from the street to buy monthly passes. They wait on the chairs
/// (or stand when all are taken) until a counter with a service employee on duty is free, are served for
/// a few minutes and leave again. Whoever waits too long gives up. Passes bring money right away and,
/// while they last (about a month), a few more passengers.
/// </summary>
public sealed partial class Game
{
    public const double AboPrice = 49;
    private const double CustomerShare = 0.01; // customer-center visits per boarding
    public const double MaxCustomerWait = 20 * 60;

    /// <summary>Monthly passes in circulation.</summary>
    public double Subscribers;
    private double _customerAcc;
    private int _nextCustomerId = -1;

    public static bool ServiceOpen(double time)
    {
        double h = time % 86400 / 3600;
        return h >= 6 && h < 22;
    }

    /// <summary>Extra demand from passes in circulation (up to +25 %).</summary>
    public double AboBoost => Math.Min(0.25, Subscribers * 0.0001);

    /// <summary>Called for every boarding: now and then a passenger decides to visit a customer center.</summary>
    private void CustomerDemand()
    {
        if (!ServiceOpen(Time)) return;
        _customerAcc += CustomerShare;
        if (_customerAcc < 1) return;
        _customerAcc -= 1;
        var depots = Depots.Values.Where(d => Yard(d).Centers.Count > 0).ToList();
        if (depots.Count == 0) return;
        var d = depots[Rng.Next(depots.Count)];
        var centers = Yard(d).Centers;
        SpawnCustomer(d, centers[Rng.Next(centers.Count)]);
    }

    /// <summary>A customer walks in from the street (left of the gate) to the center's waiting area.</summary>
    public Person SpawnCustomer(Depot d, PlacedModule center)
    {
        var (sx, sy) = d.Layout.PedestrianStart();
        var p = new Person
        {
            Id = _nextCustomerId--, Role = StaffRole.Customer, DepotId = d.Id, X = -sx - Rng.NextDouble() * 4, Y = sy,
            Ang = -Math.PI / 2, Act = PersonAct.Resting, Until = Time + MaxCustomerWait,
        };
        People[p.Id] = p;
        GoQueue(d, p, center);
        return p;
    }

    private void GoQueue(Depot d, Person p, PlacedModule center)
    {
        var y = Yard(d);
        if (!y.ServicePlans.TryGetValue(center, out var plan)) return;
        var door = Door(d, center).Dir;
        var used = People.Values.Where(o => o != p && (o.Office == center || o.TargetOffice == center) && o.Role == StaffRole.Customer)
            .Select(o => o.Seat).ToHashSet();
        int seat = Enumerable.Range(0, plan.Seats.Count).FirstOrDefault(s => !used.Contains(s), -1);
        p.Seat = seat;
        var (lx, lz) = seat >= 0 ? plan.Seats[seat] : plan.Standing[Math.Abs(p.Id) % plan.Standing.Count];
        WalkTo(p, PersonGoal.Queue, d.Layout.OfficePoint(center, door, lx, lz), center, DepotLayout.OfficeAngle(door, 0, -1));
    }

    /// <summary>Service staff to the counters; waiting customers to free staffed counters; served customers leave.</summary>
    private void ServeCustomers(Depot d, YardInfo yard, bool instant)
    {
        var L = d.Layout;
        var counters = yard.Counters;
        d.Counters = counters.Count;
        var staff = People.Values.Where(p => p.DepotId == d.Id && p.Role == StaffRole.Service && !Leaving(p)).OrderBy(p => p.Id).ToList();
        for (int i = 0; i < staff.Count; i++) staff[i].Post = i < counters.Count ? counters[i] : null;

        foreach (var p in staff)
        {
            if (Arriving(p)) continue;
            if (p.Post is { } post && ModuleAt(d, post.X, post.Y) is { } m && yard.ServicePlans.TryGetValue(m, out var plan))
            {
                var door = Door(d, m).Dir;
                var (x, y) = L.OfficePoint(m, door, plan.Counters[post.Bay], plan.StaffZ);
                double face = DepotLayout.OfficeAngle(door, 0, 1); // toward the customers
                if (instant)
                {
                    Place(p, (x, y, face), PersonAct.Working);
                    p.Office = p.TargetOffice = m;
                }
                else if (Far(p, (x, y, face))) WalkTo(p, PersonGoal.Post, (x, y), m, face);
                else if (p.Act != PersonAct.Walking) p.Act = PersonAct.Working;
                continue;
            }
            // no counter for this one: break room
            if (p.Goal == PersonGoal.None && (p.Act != PersonAct.Resting || p.Office == null || !yard.Offices.Contains(p.Office)))
            {
                if (instant) PlaceAtRest(d, p);
                else GoRest(d, p);
            }
        }
        d.CountersOpen = counters.Count(c => AtWork(d, c.X, c.Y, c.Bay));

        var customers = People.Values.Where(p => p.DepotId == d.Id && p.Role == StaffRole.Customer).ToList();
        bool open = ServiceOpen(Time);
        foreach (var c in customers)
        {
            if (c.Goal == PersonGoal.Gate) continue;
            bool inCenter = (c.Office ?? c.TargetOffice) is { } cm && yard.Centers.Contains(cm);
            if (c.Act == PersonAct.Working && c.Post != null && Time >= c.Until)
            {
                // served: a monthly pass sold
                Money += AboPrice;
                Today.Revenue += AboPrice;
                Today.Customers++;
                d.ServedToday++;
                Subscribers += 1;
                c.Post = null;
                WalkTo(c, PersonGoal.Gate, StreetPointLeft(d));
            }
            else if (c.Post == null && (!inCenter || !open || (c.Act == PersonAct.Waiting && Time >= c.Until)))
            {
                // waited too long (or the center closed / was torn down)
                if (open && inCenter)
                {
                    Today.CustomersLost++;
                    d.LostToday++;
                }
                WalkTo(c, PersonGoal.Gate, StreetPointLeft(d));
            }
        }

        // Free counters with someone at work call the customer who has waited longest
        foreach (var counter in counters)
        {
            if (!AtWork(d, counter.X, counter.Y, counter.Bay)) continue;
            if (customers.Any(c => c.Post == counter && c.Goal != PersonGoal.Gate)) continue;
            var next = customers.Where(c => c.Act == PersonAct.Waiting && c.Post == null && c.Goal == PersonGoal.None &&
                    c.Office is { } o && o.X == counter.X && o.Y == counter.Y)
                .OrderBy(c => c.Until).FirstOrDefault();
            if (next == null || ModuleAt(d, counter.X, counter.Y) is not { } m) continue;
            var plan = yard.ServicePlans[m];
            var door = Door(d, m).Dir;
            next.Post = counter;
            next.Seat = -1;
            WalkTo(next, PersonGoal.Counter, L.OfficePoint(m, door, plan.Counters[counter.Bay], plan.CustomerZ), m, DepotLayout.OfficeAngle(door, 0, -1));
        }
        d.Queue = customers.Count(c => c.Post == null && c.Goal != PersonGoal.Gate);
    }

    /// <summary>Customers come and go on the pavement left of the gate.</summary>
    private static (double X, double Y) StreetPointLeft(Depot d)
    {
        var (x, y) = d.Layout.PedestrianStart();
        return (-x, y);
    }
}
