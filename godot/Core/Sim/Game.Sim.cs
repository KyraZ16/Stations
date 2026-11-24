namespace Stations.Core.Sim;

public sealed partial class Game
{
    /// <summary>Time-of-day demand factor (average over a day ≈ 0.9).</summary>
    private static double DemandFactor(double t)
    {
        double h = t % 86400 / 3600;
        return h switch
        {
            < 5 => 0.08, < 6 => 0.4, < 7 => 1.2, < 9 => 2.0, < 15 => 0.9,
            < 18 => 1.7, < 20 => 1.0, < 22 => 0.5, _ => 0.25,
        };
    }

    /// <summary>Share of trips going home → work at time t.</summary>
    private static double OutboundShare(double t)
    {
        double h = t % 86400 / 3600;
        return h < 11 ? 0.8 : h < 14 ? 0.5 : 0.2;
    }

    /// <summary>Advance by real seconds at the current speed, in steps of at most 1 game second.</summary>
    public void Update(double realDt)
    {
        double dt = Math.Min(realDt, 0.25) * Speeds[Speed];
        while (dt > 0)
        {
            double step = Math.Min(dt, 1);
            Step(step);
            dt -= step;
        }
    }

    /// <summary>Advance by game seconds regardless of the speed setting (tests, fast-forward).</summary>
    public void Advance(double gameSeconds)
    {
        while (gameSeconds > 0)
        {
            double step = Math.Min(gameSeconds, 1);
            Step(step);
            gameSeconds -= step;
        }
    }

    private void Step(double dt)
    {
        int dayBefore = Day;
        double shiftDayBefore = Math.Floor((Time - 3 * 3600) / 86400);
        Time += dt;
        if (Day != dayBefore) NewDay();
        // Shift day starts at 03:00: everyone gets a fresh 8 h
        if (Math.Floor((Time - 3 * 3600) / 86400) != shiftDayBefore)
            foreach (var d in Drivers.Values) d.Worked = 0;

        double wage = Drivers.Count * DriverWageDay * dt / 86400;
        Money -= wage;
        Today.Costs += wage;

        _dispatchTimer += dt;
        if (_dispatchTimer >= DispatchInterval)
        {
            _dispatchTimer = 0;
            RunDispatcher();
        }

        if (_routerDirty)
        {
            Router = new Router(Map.Cells, Stops.Values, Lines.Values, Headway, Rng);
            _routerDirty = false;
            NetworkVersion++;
        }

        GenerateDemand(dt);
        foreach (var v in Vehicles.Values) StepVehicle(v, dt);

        _expireTimer += dt;
        if (_expireTimer > 60)
        {
            _expireTimer = 0;
            foreach (var s in Stops.Values)
            {
                int before = s.Waiting.Count;
                s.Waiting.RemoveAll(p => Time - p.WaitingSince >= MaxWait);
                Today.Lost += before - s.Waiting.Count;
            }
        }
    }

    private void NewDay()
    {
        History.Add(Today);
        if (History.Count > 60) History.RemoveAt(0);
        Today = new DayStats { Day = Day };
        foreach (var l in Lines.Values)
        {
            l.PassengersToday = 0;
            l.RevenueToday = 0;
        }
        foreach (var s in Stops.Values) s.BoardedToday = 0;
    }

    private void GenerateDemand(double dt)
    {
        var R = Router;
        if (R == null || R.CoveredRes <= 0 || R.CoveredJobs <= 0) return;
        _demandAcc += R.CoveredRes * TripsPerResident / 86400 * DemandFactor(Time) * dt;
        double destShare = R.CoveredJobs / Math.Max(Map.TotalJobs, 1);
        var cells = Map.Cells;
        while (_demandAcc >= 1)
        {
            _demandAcc -= 1;
            if (Rng.NextDouble() > destShare) continue;
            bool outbound = Rng.NextDouble() < OutboundShare(Time);
            int o = R.SampleCell(!outbound);
            int d = R.SampleCell(outbound);
            if (o == d) continue;
            double dist = Util.Hypot(cells[o].X - cells[d].X, cells[o].Y - cells[d].Y);
            if (dist < 300) continue;
            var j = R.Find(o, d);
            if (j == null) continue;
            double walkT = dist * 1.3 / 1.3;
            double carT = dist * 1.4 / 9 + 240;
            double alt = Math.Min(walkT, carT);
            double p = 0.75 / (1 + Math.Exp((j.Time - alt) / 400));
            if (Rng.NextDouble() > p) continue;
            if (!Stops.TryGetValue(j.FirstStop, out var stop)) continue;
            // Legs are cached per cell pair; each passenger gets its own copy because they may be remapped.
            var legs = j.Legs.Select(l => new Leg
            {
                Line = l.Line, Run = l.Run, Board = l.Board, Alight = l.Alight, BoardStop = l.BoardStop,
                AlightStop = l.AlightStop, WalkBefore = l.WalkBefore, Ver = l.Ver,
            }).ToList();
            stop.Waiting.Add(new Passenger
            {
                Id = _passengerId++, Legs = legs, ReadyAt = Time + legs[0].WalkBefore, WaitingSince = Time, SpawnedAt = Time,
            });
        }
    }

    private void StepVehicle(Vehicle v, double dt)
    {
        if (v.State == VehicleState.Depot) return;
        var def = Modes.Get(v.Mode).Vehicle;
        double cost = def.CostPerHour * dt / 3600;
        Money -= cost;
        Today.Costs += cost;
        if (v.DriverId is int did && Drivers.TryGetValue(did, out var drvWork)) drvWork.Worked += dt;

        TransitLine? line = v.LineId is int lid && Lines.TryGetValue(lid, out var l0) ? l0 : null;

        if (v.State == VehicleState.Deadhead)
        {
            if (!Drive(v, v.Path!.Length, dt)) return;
            switch (v.DeadheadTo)
            {
                case DeadheadTarget.Depot:
                    ParkInDepot(v);
                    break;
                case DeadheadTarget.Rejoin:
                {
                    var run = line != null && v.RunIdx < line.Runs.Count ? line.Runs[v.RunIdx] : null;
                    if (line != null && run != null && v.StopIdx < run.StopIds.Count)
                    {
                        v.Path = run.Path;
                        v.S = run.Offsets[v.StopIdx];
                        v.Seg = run.Path.SegAt(v.S);
                        ServeStop(v, line, run);
                    }
                    else
                    {
                        Dispatch(v, line != null ? DeadheadTarget.Line : DeadheadTarget.Depot);
                    }
                    break;
                }
                default:
                    if (line != null) EnterLayover(v, line, 0);
                    else Dispatch(v, DeadheadTarget.Depot);
                    break;
            }
            return;
        }

        if (line == null)
        {
            Dispatch(v, DeadheadTarget.Depot);
            return;
        }
        var r = line.Runs[v.RunIdx];

        switch (v.State)
        {
            case VehicleState.Layover:
            {
                // Driver relief at the terminus when the next trip would exceed the shift
                Driver? drv = v.DriverId is int id && Drivers.TryGetValue(id, out var dd) ? dd : null;
                double tripTime = line.CycleTime / line.Runs.Count;
                if (drv != null && drv.Worked + tripTime > ShiftMax)
                {
                    var fresh = AvailableDriver(v.DepotId);
                    if (fresh != null)
                    {
                        drv.VehicleId = null;
                        fresh.VehicleId = v.Id;
                        v.DriverId = fresh.Id;
                    }
                    else
                    {
                        line.DriverShortage = true;
                        Dispatch(v, DeadheadTarget.Depot);
                        break;
                    }
                }
                // More vehicles out than the timetable needs: this one goes home
                if (ActiveVehicles(line) > RequiredVehicles(line))
                {
                    Dispatch(v, DeadheadTarget.Depot);
                    break;
                }
                double hw = line.Timetable.TargetHeadway(Time);
                if (double.IsFinite(hw) && Time - v.ArrivedAt >= MinLayover && Time - r.LastDeparture >= hw * 0.95)
                {
                    r.LastDeparture = Time;
                    v.StopIdx = 0;
                    ServeStop(v, line, r);
                }
                break;
            }
            case VehicleState.Moving:
                if (Drive(v, r.Offsets[v.StopIdx], dt)) ServeStop(v, line, r);
                break;
            case VehicleState.Dwell:
                if (Time >= v.DwellUntil)
                {
                    if (v.StopIdx >= r.StopIds.Count - 1)
                    {
                        EnterLayover(v, line, (v.RunIdx + 1) % line.Runs.Count);
                    }
                    else
                    {
                        v.StopIdx++;
                        v.State = VehicleState.Moving;
                    }
                }
                break;
        }
    }

    private void EnterLayover(Vehicle v, TransitLine line, int runIdx)
    {
        v.RunIdx = runIdx;
        v.Path = line.Runs[runIdx].Path;
        v.S = 0;
        v.Seg = 0;
        v.V = 0;
        v.StopIdx = 0;
        v.State = VehicleState.Layover;
        v.ArrivedAt = Time;
        var p = v.Path.PointAt(0, 0);
        v.X = p.X;
        v.Y = p.Y;
    }

    /// <summary>Move along the current path towards distance target. Returns true when it is reached.</summary>
    private bool Drive(Vehicle v, double target, double dt)
    {
        var path = v.Path!;
        var def = Modes.Get(v.Mode).Vehicle;
        double remaining = target - v.S;
        if (remaining <= 0.01)
        {
            v.V = 0;
            return true;
        }
        v.Seg = path.SegAt(v.S, v.Seg);
        double limit = Math.Min(v.Seg < path.Spd.Count ? path.Spd[v.Seg] : def.MaxSpeed, def.MaxSpeed);
        double vt = Math.Min(limit, Math.Sqrt(2 * Brake * remaining) + 0.3);
        v.V = v.V < vt ? Math.Min(vt, v.V + Accel * dt) : vt;
        double ds = v.V * dt;
        bool arrived = false;
        if (ds >= remaining)
        {
            ds = remaining;
            arrived = true;
        }
        v.S += ds;
        v.KmTotal += ds / 1000;
        double c = def.CostPerKm * ds / 1000;
        Money -= c;
        Today.Costs += c;
        v.Seg = path.SegAt(v.S, v.Seg);
        var p = path.PointAt(v.S, v.Seg);
        v.X = p.X;
        v.Y = p.Y;
        if (ds > 0.001) v.Ang = p.Ang;
        if (arrived) v.V = 0;
        return arrived;
    }

    private void ServeStop(Vehicle v, TransitLine line, Run run)
    {
        int k = v.StopIdx;
        int moves = 0;
        if (Stops.TryGetValue(run.StopIds[k], out var stop))
        {
            // Alighting
            var keep = new List<Passenger>();
            foreach (var p in v.Passengers)
            {
                if (p.Legs[p.LegIdx].Alight != k)
                {
                    keep.Add(p);
                    continue;
                }
                moves++;
                p.LegIdx++;
                if (p.LegIdx >= p.Legs.Count)
                {
                    Today.Trips++;
                    continue;
                }
                var next = p.Legs[p.LegIdx];
                if (!Stops.TryGetValue(next.BoardStop, out var nextStop) ||
                    !Lines.TryGetValue(next.Line, out var nextLine) || nextLine.Version != next.Ver)
                {
                    Today.Lost++;
                    continue;
                }
                p.ReadyAt = Time + next.WalkBefore;
                p.WaitingSince = Time;
                nextStop.Waiting.Add(p);
            }
            v.Passengers = keep;

            // Boarding
            int cap = Modes.Get(v.Mode).Vehicle.Capacity;
            if (k < run.StopIds.Count - 1)
            {
                var remaining = new List<Passenger>();
                foreach (var p in stop.Waiting)
                {
                    var leg = p.Legs[p.LegIdx];
                    if (v.Passengers.Count < cap && leg.Line == line.Id && leg.Run == v.RunIdx && leg.Board == k && p.ReadyAt <= Time)
                    {
                        moves++;
                        v.Passengers.Add(p);
                        stop.BoardedToday++;
                        Today.Boardings++;
                        Today.WaitSum += Time - p.WaitingSince;
                        line.PassengersToday++;
                        if (p.LegIdx == 0)
                        {
                            Money += Fare;
                            Today.Revenue += Fare;
                            line.RevenueToday += Fare;
                        }
                    }
                    else
                    {
                        remaining.Add(p);
                    }
                }
                stop.Waiting = remaining;
            }
        }
        v.State = VehicleState.Dwell;
        v.V = 0;
        v.DwellUntil = Time + (k == 0 ? 12 : 7) + moves * 1.2;
    }

    // ---------------------------------------------------------------- stats

    public int WaitingCount => Stops.Values.Sum(s => s.Waiting.Count);
    public int RidingCount => Vehicles.Values.Sum(v => v.Passengers.Count);
}
