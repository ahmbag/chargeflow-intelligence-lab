namespace CFIL.Domain;

public sealed record TrafficObservation(string VehicleId, double DistanceM, double PositionKm, double SpeedMps,
    string EdgeId, string? StoppedAt, bool Arrived = false);
public sealed record EnergyStepResult(double ConsumedKwh, double SpecificConsumptionKwhPerKm);
public interface IVehicleEnergyModel
{
    EnergyStepResult Calculate(Vehicle vehicle, TrafficObservation observation, TimeSpan elapsed);
}
public sealed class VehicleEnergyModel : IVehicleEnergyModel
{
    public EnergyStepResult Calculate(Vehicle vehicle, TrafficObservation observation, TimeSpan elapsed)
    {
        var distance = observation.DistanceM - vehicle.DistanceM;
        if (!double.IsFinite(distance) || distance < -1e-6 || !double.IsFinite(observation.SpeedMps) || observation.SpeedMps < 0 || elapsed.TotalSeconds < 0)
            throw new InvalidOperationException($"Invalid traffic observation {vehicle.Id}: distance {vehicle.DistanceM:R} -> {observation.DistanceM:R}, speed {observation.SpeedMps:R}, arrived {observation.Arrived}.");
        var profile = vehicle.Definition.Energy;
        var specific = profile.ConsumptionAt(observation.SpeedMps, vehicle.Definition.GrossMassKg);
        var moving = vehicle.Status is VehicleStatus.Driving or VehicleStatus.ApproachingPark or VehicleStatus.Departing or VehicleStatus.Connecting or VehicleStatus.Stopping;
        return new(Math.Max(0, distance) / 1000 * specific + (moving ? profile.AuxiliaryKw * elapsed.TotalHours : 0), specific);
    }
}
