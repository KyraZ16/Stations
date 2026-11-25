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
    /// <summary>Regional exit at the map edge: stands for the region beyond (not built, not removable).</summary>
    public bool Portal;
    /// <summary>Expansion level 1–3: more bays/tracks, room for more waiting passengers, a larger catchment.</summary>
    public int Level = 1;
}

public sealed class Depot
{
    public int Id;
    public string Name = "";
    public ModeId Mode;
    public RoadPos Pos;
    /// <summary>Yard layout; its parking spots and offices limit vehicles and drivers.</summary>
    public DepotLayout Layout = new();

    // Runtime status of the yard services (updated by the simulation, not saved)
    public int Repairing, Washing, Charging, Workshops, WashBays, Chargers;
    /// <summary>Workshop lanes / wash bays with someone on duty right now; staff on the grounds.</summary>
    public int LanesStaffed, WashStaffed, OnSite;
    /// <summary>Customer center: counters with staff at work, customers waiting, served and lost today.</summary>
    public int CountersOpen, Counters, Queue, ServedToday, LostToday;
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
    /// <summary>Set when vehicles are in the pool but none is ready (repair, charging).</summary>
    public bool MaintenanceShortage;
    /// <summary>Model bought when vehicles are added to this line.</summary>
    public string? PreferredModel;
    /// <summary>Average cleanliness of the vehicles in service (0..1); dirty vehicles deter passengers.</summary>
    public double Comfort = 1;
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

/// <summary>Who a person on the depot grounds is: staff, or a customer of the customer center.</summary>
public enum StaffRole { Driver, Mechanic, Cleaner, Service, Customer, Researcher }

/// <summary>Work shifts of yard staff: early 06–14, late 14–22, night 22–06.</summary>
public enum ShiftId { Early, Late, Night }

/// <summary>Yard staff besides drivers: mechanics (workshop) and cleaners (wash bay, interior cleaning).</summary>
public sealed class Worker
{
    public int Id;
    public string Name = "";
    public int DepotId;
    public StaffRole Role;
    public ShiftId Shift;
}

/// <summary>What a person in the yard is doing.</summary>
public enum PersonAct
{
    Walking, // on the way (see Goal)
    Changing, // at the locker (start or end of the shift)
    Resting, // break room: seated or standing in the office
    Working, // at the workplace (workshop lane, wash bay, inside a vehicle)
    Waiting, // at the workplace without work (workbench)
}

/// <summary>Where a walking person is heading.</summary>
public enum PersonGoal { None, Locker, Seat, Vehicle, Post, Clean, Gate, Queue, Counter }

/// <summary>
/// A member of staff who is on the depot grounds right now (runtime only, rebuilt after loading).
/// Positions are yard meters (origin = yard center), like vehicles in the yard.
/// </summary>
public sealed class Person
{
    public int Id; // staff id (driver or worker)
    public StaffRole Role;
    public int DepotId;
    public double X, Y, Ang;
    public List<(double X, double Y)> Path = new();
    public int PathIdx;
    public PersonAct Act;
    public PersonGoal Goal;
    /// <summary>Goal after the locker (shift start: to work; shift end: home).</summary>
    public PersonGoal AfterLocker;
    public double Until;
    /// <summary>Office the person is in (null when outdoors).</summary>
    public PlacedModule? Office;
    /// <summary>Office the person is walking into (becomes <see cref="Office"/> on arrival).</summary>
    public PlacedModule? TargetOffice;
    /// <summary>End point of the current walk.</summary>
    public (double X, double Y) Target;
    /// <summary>Heading to take on arrival (NaN: keep walking direction).</summary>
    public double FaceAng = double.NaN;
    /// <summary>Seat in the office's break room, or -1.</summary>
    public int Seat = -1;
    /// <summary>Vehicle the person walks to (driver) or cleans (cleaner).</summary>
    public int? VehicleId;
    /// <summary>Workplace (module position and lane) of mechanics and cleaners.</summary>
    public (int X, int Y, int Bay)? Post;
    /// <summary>Inside a vehicle: not drawn.</summary>
    public bool Hidden;
    /// <summary>Total distance walked (drives the walking animation).</summary>
    public double Walked;
}

public enum VehicleState { Depot, Deadhead, Layover, Moving, Dwell, Broken, Yard }

/// <summary>Movement inside the depot yard. WaitDriver: dispatched, waiting for the driver to walk over and board.</summary>
public enum YardTask { None, Enter, Exit, ToWorkshop, ToWash, ToSpot, WaitDriver }

/// <summary>Where a vehicle stands in the yard.</summary>
public enum YardBay { Spot, Workshop, Wash }

public enum DeadheadTarget { Line, Depot, Rejoin }

public sealed class Vehicle
{
    public int Id;
    public string Name = "";
    public ModeId Mode;
    /// <summary>Vehicle model id (see Modes.Model).</summary>
    public string ModelId = "";
    public VehicleDef Def => Modes.Model(Mode, ModelId);
    /// <summary>Technical condition 0..1; wears with distance, restored in a workshop.</summary>
    public double Condition = 1;
    /// <summary>Cleanliness 0..1; drops while in service, restored in a wash bay.</summary>
    public double Cleanliness = 1;
    /// <summary>Battery charge 0..1 (electric vehicles only).</summary>
    public double Charge = 1;
    public int Breakdowns;
    public double BoughtAt;
    public double BrokenUntil;

    // Yard: home spot, current bay, movement along a path (yard meters, origin = yard center)
    public int SpotX = -1, SpotY = -1;
    public YardBay Bay = YardBay.Spot;
    public int BayX = -1, BayY = -1, BayIndex;
    public YardTask Task = YardTask.None;
    public List<(double X, double Y)>? YardPath;
    public int YardIdx;
    public double YardWait;
    public double YardX, YardY, YardAng;
    /// <summary>Where to go after leaving the yard.</summary>
    public DeadheadTarget ExitThen = DeadheadTarget.Line;
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
    /// <summary>Waiting in front of a stop whose bays/tracks are all taken (runtime).</summary>
    public bool Queued;
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
    public int Customers { get; set; } // served at a customer center
    public int Regional { get; set; } // trips to or from the region beyond the map
    public int CustomersLost { get; set; } // left the customer center after waiting too long
    public double WaitSum { get; set; }
}
