namespace CFIL.Domain;

public sealed record MultiDaySchedule(string Version, int Days, string DemandProfile, string TimeZone = "UTC-fixed")
{
    public void Validate(Scenario scenario)
    {
        if (Version != "MULTIDAY-V1" || Days is < 2 or > 7 || scenario.DurationSeconds != Days * 86400 ||
            scenario.TruckCount % Days != 0 || scenario.CarCount % Days != 0 ||
            DemandProfile is not ("stable" or "gradual") || TimeZone != "UTC-fixed")
            throw new ArgumentException("Invalid multi-day schedule; population counts describe the entire horizon.");
    }
}

public sealed record JourneyIdentity(string VehicleId, string JourneyId, string? TransportJobId, int PlannedDay,
    int PlannedDepartureSeconds);

public static class MultiDayPopulation
{
    // Independent named streams prevent demand or charging changes from shifting
    // vehicle characteristics. The arithmetic is versioned and runtime independent.
    private static int Seed(int seed, int day, int stream) => (int)(((long)seed * 1000003 + day * 9176L + stream * 104729L) % int.MaxValue);

    public static VehicleDefinition[] Create(Scenario scenario)
    {
        var schedule = scenario.MultiDay ?? throw new ArgumentException("Missing multi-day schedule.");
        schedule.Validate(scenario);
        var result = new List<VehicleDefinition>();
        for (var day = 0; day < schedule.Days; day++)
        {
            var daily = scenario with { MultiDay = null, Seed = Seed(scenario.Seed, day, 1),
                TruckCount = scenario.TruckCount / schedule.Days, CarCount = scenario.CarCount / schedule.Days,
                DepartureWindowSeconds = 86400 };
            var characteristics = PopulationFactory.Create(daily);
            var demand = PopulationFactory.Create(daily with { Seed = Seed(scenario.Seed, day, 2) });
            var charging = PopulationFactory.Create(daily with { Seed = Seed(scenario.Seed, day, 3) });
            for (var i = 0; i < characteristics.Length; i++)
            {
                var v = characteristics[i];
                var u = demand[i].DepartureSeconds / 86400d;
                // Three recurring waves, including a late wave that carries real
                // journeys and charging attempts across midnight. Gradual drift
                // moves departures only; it never changes an active vehicle.
                var local = u < .45 ? 21600 + u / .45 * 14400 :
                    u < .8 ? 50400 + (u - .45) / .35 * 14400 : 79200 + (u - .8) / .2 * 7000;
                if (schedule.DemandProfile == "gradual") local = Math.Min(86399, local + day * 600);
                var depart = day * 86400 + (int)local;
                var id = $"D{day + 1:D2}-{v.Id}";
                result.Add(v with { Id = id, DepartureSeconds = depart, InitialSoc = charging[i].InitialSoc,
                    Logistics = v.Logistics is null ? null : v.Logistics with {
                        TargetArrivalMs = v.Logistics.TargetArrivalMs + (depart - v.DepartureSeconds) * 1000L } });
            }
        }
        // Each scheduled journey deliberately gets a new physical vehicle in V1.
        // No vehicle is reused or teleported; future fleet rotations need their own
        // availability model. SUMO independently sorts these by departure time.
        return result.OrderBy(v => v.Id, StringComparer.Ordinal).ToArray();
    }

    public static JourneyIdentity[] Identities(IEnumerable<VehicleDefinition> population) => population.Select(v =>
        new JourneyIdentity(v.Id, v.Id + ":trip-1", v.Kind == VehicleKind.Truck ? "ORDER-" + v.Id : null,
            v.DepartureSeconds / 86400 + 1, v.DepartureSeconds)).ToArray();
}
