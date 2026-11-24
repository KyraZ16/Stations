using Stations.Core.Map;

namespace Stations.Core.Sim;

public sealed class Stop
{
    public int Id;
    public string Name = "";
    public ModeId Mode;
    public RoadPos Pos;
    public List<Passenger> Waiting = new();
    public int BoardedToday;
}

public sealed class Depot
{
    public int Id;
    public string Name = "";
    public ModeId Mode;
    public RoadPos Pos;
}

/// <summary>One direction of a line: a path through an ordered list of stops.</summary>
public sealed class Run
{
    public required RoutePath Path;
    public required List<int> StopIds;
    public required List<double> Offsets; // distance of each stop along the path
    public required List<double> Times; // scheduled cumulative time at each stop (incl. dwell)
    public double LastDeparture = double.NegativeInfinity;
}

public sealed class TransitLine
{
    public int Id;
    public string Name = "";
    public string Color = "";
    public ModeId Mode;
    public List<int> StopIds = new();
    public bool Circular;
    public List<Run> Runs = new();
    public double CycleTime;
    /// <summary>Incremented when the route changes; passenger plans from older versions are invalid.</summary>
    public int Version;
    public Timetable Timetable = Timetable.Default();
    /// <summary>Set by the dispatcher when a vehicle could not leave for lack of a driver.</summary>
    public bool DriverShortage;
    public int PassengersToday;
    public double RevenueToday;
}

public sealed class Driver
{
    public int Id;
    public string Name = "";
    public int DepotId;
    /// <summary>Seconds worked in the current shift day.</summary>
    public double Worked;
    public int? VehicleId;
}

public enum VehicleState { Depot, Deadhead, Layover, Moving, Dwell }

public enum DeadheadTarget { Line, Depot, Rejoin }

public sealed class Vehicle
{
    public int Id;
    public string Name = "";
    public ModeId Mode;
    public int DepotId;
    /// <summary>Line the vehicle is assigned to (its pool). It only runs when the timetable needs it.</summary>
    public int? LineId;
    public int? DriverId;
    public VehicleState State = VehicleState.Depot;
    /// <summary>For deadhead trips: where the trip ends.</summary>
    public DeadheadTarget DeadheadTo = DeadheadTarget.Depot;
    public RoutePath? Path;
    public double S;
    public int Seg;
    public double V;
    public int RunIdx;
    public int StopIdx;
    public double DwellUntil;
    public double ArrivedAt;
    public List<Passenger> Passengers = new();
    public double X, Y, Ang;
    public double KmTotal;
}

public sealed class Leg
{
    public int Line;
    public int Run;
    public int Board; // stop index within run
    public int Alight;
    public int BoardStop;
    public int AlightStop;
    public double WalkBefore; // seconds walking to the boarding stop (transfer walks)
    public int Ver; // line version the plan was made for
}

public sealed class Passenger
{
    public int Id;
    public required List<Leg> Legs;
    public int LegIdx;
    public double ReadyAt;
    public double WaitingSince;
    public double SpawnedAt;
}

public sealed class DayStats
{
    public int Day { get; set; }
    public int Trips { get; set; } // completed journeys
    public int Boardings { get; set; }
    public int Lost { get; set; } // gave up waiting
    public double Revenue { get; set; }
    public double Costs { get; set; }
    public double WaitSum { get; set; }
}
