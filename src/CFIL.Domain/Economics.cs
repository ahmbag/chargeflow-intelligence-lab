using System.Globalization;
using System.Text.Json.Serialization;

namespace CFIL.Domain;

public readonly record struct Money
{
    public decimal Amount { get; }
    public string Currency { get; }
    [JsonConstructor]
    public Money(decimal amount, string currency = "EUR")
    {
        if (currency is not ("EUR" or "USD")) throw new ArgumentException("Unsupported currency.");
        Amount = amount; Currency = currency;
    }
    public static Money operator +(Money a, Money b) => a.Currency == b.Currency
        ? new(checked(a.Amount + b.Amount), a.Currency) : throw new InvalidOperationException("Currency mismatch.");
    public static Money operator -(Money a, Money b) => a.Currency == b.Currency
        ? new(checked(a.Amount - b.Amount), a.Currency) : throw new InvalidOperationException("Currency mismatch.");
    public override string ToString() => Amount.ToString("0.00##########################", CultureInfo.InvariantCulture) + " " + Currency;
}

public enum EconomicMode { OFF, ACCOUNTING_ONLY, ECONOMIC_ADVISORY, ECONOMIC_CONTROLLED }
public enum EconomicStrategy { E0, E1, E2 }
public enum CostRecordStatus { FINAL, OPEN_AT_HORIZON, INCOMPLETE_RUN, PRICING_UNRESOLVED }
public sealed record ChargingOperator(string Id, string Name, string ExternalReference, string[] ParkIds);
public sealed record PaymentRoute(string Id, string Kind, string ExternalReference);
public sealed record FleetContract(string Id, string Version, string FleetId, string OperatorId, string PaymentRouteId,
    long PublishedAtMs, long ValidFromMs, long ValidToMs, string ExternalReference);
public sealed record RoundingPolicy(string Version = "ROUND-EUR-V1", int MoneyDecimals = 2,
    int EnergyDecimals = 9, string Mode = "ToEven");
public sealed record TariffComponents(decimal EnergyEurPerKwh, decimal SessionEur,
    decimal ChargingEurPerHour, decimal OccupancyEurPerHour, int ChargingStepSeconds = 1,
    int OccupancyStepSeconds = 1, int OccupancyFreeSeconds = 300);
public sealed record ChargingTariffVersion(string TariffId, string Version, string OperatorId, string PaymentRouteId,
    string Currency, string PriceBasis, long PublishedAtMs, long ValidFromMs, long ValidToMs,
    string[] ApplicableParks, string[] Connectors, string Eligibility, TariffComponents PriceComponents,
    RoundingPolicy RoundingPolicy, string SourceClassification = "SYNTHETIC",
    string BindingRule = "SESSION_START_LOCKED");
public sealed record FleetEconomicProfile(string Id, string Version, string FleetId, string[] AllowedPaymentRoutes,
    decimal TimeValuePerHour, decimal NonEnergyDistanceCostPerKm, decimal IncrementalLateCostPerHour,
    string ExternalReference);
public sealed record TransportEconomicContext(string VehicleId, string FleetProfileId, string OrderId,
    long TargetArrivalMs, string ExternalReference);
public sealed record EconomicAssignment(string VehicleId, string ProfileId, TransportEconomicContext Order);
public sealed record TariffCatalog(string Version, string SimulationTimeOrigin, ChargingOperator[] Operators,
    PaymentRoute[] PaymentRoutes, ChargingTariffVersion[] Tariffs, FleetContract[] Contracts);
public sealed record FleetEconomicCatalog(string Version, FleetEconomicProfile[] Profiles);
public sealed record PricingSnapshot(string TariffId, string Version, string OperatorId, string PaymentRouteId,
    string? ContractId, string FleetId, string ParkId, string Connector, long KnownAtMs, long SessionStartMs,
    long ValidFromMs, long ValidToMs, long? ContractValidToMs, TariffComponents Components,
    RoundingPolicy Rounding, string Currency = "EUR", string PriceBasis = "NET",
    string SourceClassification = "SYNTHETIC", string BindingRule = "SESSION_START_LOCKED");
public sealed record BillingQuantities(decimal MeteredGridKwh, long ChargingMilliseconds,
    long OccupancyAfterChargingMilliseconds, bool SessionStarted);
public sealed record CostLine(string Component, decimal Quantity, string Unit, decimal UnitRate, Money Amount);
public sealed record ChargingBill(PricingSnapshot Pricing, BillingQuantities Quantities, CostLine[] Lines, Money Total);
public sealed record EconomicCostEstimate(ChargingBill EstimatedChargingBill, Money ValuedTimeCost,
    Money NonEnergyDistanceCost, Money IncrementalLateCost, Money EconomicScore,
    decimal RemainingValuedSeconds, decimal RemainingDistanceKm, decimal TimeValuePerHour);

public interface ITariffResolver
{
    PricingSnapshot[] Resolve(string parkId, IEnumerable<string> connectors, FleetEconomicProfile fleet,
        long knownAtMs, long sessionStartMs);
}
public interface IChargingCostCalculator { ChargingBill Calculate(PricingSnapshot pricing, BillingQuantities quantities); }
public interface IEconomicCostEvaluator
{
    EconomicCostEstimate Evaluate(ChargingBill bill, FleetEconomicProfile fleet, decimal remainingSeconds,
        decimal distanceKm, long nowMs, long targetArrivalMs, decimal timeValueMultiplier = 1);
}

public sealed class TariffResolver : ITariffResolver
{
    private readonly TariffCatalog catalog;
    public TariffResolver(TariffCatalog catalog) { Validate(catalog); this.catalog = catalog; }
    public PricingSnapshot[] Resolve(string parkId, IEnumerable<string> connectors, FleetEconomicProfile fleet,
        long knownAtMs, long sessionStartMs)
    {
        if (knownAtMs < 0 || sessionStartMs < knownAtMs) throw new ArgumentException("Invalid simulation timestamp.");
        var connectorSet = connectors.ToHashSet(StringComparer.Ordinal);
        var result = new List<PricingSnapshot>();
        foreach (var tariff in catalog.Tariffs.OrderBy(t => t.TariffId, StringComparer.Ordinal).ThenBy(t => t.Version, StringComparer.Ordinal))
        {
            // Access to the entire scenario file does not confer knowledge of unpublished
            // tariffs. Both publication time and half-open validity must pass independently.
            if (tariff.PublishedAtMs > knownAtMs || sessionStartMs < tariff.ValidFromMs || sessionStartMs >= tariff.ValidToMs ||
                !tariff.ApplicableParks.Contains(parkId) || !fleet.AllowedPaymentRoutes.Contains(tariff.PaymentRouteId)) continue;
            var connector = tariff.Connectors.Intersect(connectorSet).Order(StringComparer.Ordinal).FirstOrDefault();
            if (connector is null) continue;
            var route = catalog.PaymentRoutes.Single(r => r.Id == tariff.PaymentRouteId);
            var contract = catalog.Contracts.Where(c => c.FleetId == fleet.FleetId && c.OperatorId == tariff.OperatorId &&
                c.PaymentRouteId == route.Id && c.PublishedAtMs <= knownAtMs && c.ValidFromMs <= sessionStartMs && sessionStartMs < c.ValidToMs)
                .OrderBy(c => c.Id, StringComparer.Ordinal).FirstOrDefault();
            if (route.Kind == "CONTRACT" && contract is null) continue;
            result.Add(new(tariff.TariffId, tariff.Version, tariff.OperatorId, route.Id, contract?.Id, fleet.FleetId,
                parkId, connector, knownAtMs, sessionStartMs, tariff.ValidFromMs, tariff.ValidToMs, contract?.ValidToMs,
                tariff.PriceComponents with { }, tariff.RoundingPolicy with { }));
        }
        return result.ToArray();
    }

    public static void Validate(TariffCatalog c)
    {
        if (c.Version != "TARIFFS-V1" || !DateTimeOffset.TryParse(c.SimulationTimeOrigin, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out _) || c.Operators.Length == 0 || c.PaymentRoutes.Length == 0 ||
            c.Operators.Select(x => x.Id).Distinct().Count() != c.Operators.Length ||
            c.Operators.SelectMany(x => x.ParkIds).Distinct().Count() != c.Operators.Sum(x => x.ParkIds.Length) ||
            c.PaymentRoutes.Select(x => x.Id).Distinct().Count() != c.PaymentRoutes.Length ||
            c.PaymentRoutes.Any(r => r.Kind is not ("DIRECT" or "CONTRACT")) ||
            c.Tariffs.Select(t => (t.TariffId,t.Version)).Distinct().Count() != c.Tariffs.Length ||
            c.Contracts.Select(t => (t.Id,t.Version)).Distinct().Count() != c.Contracts.Length)
            throw new ArgumentException("Invalid catalog identity or simulation origin.");
        foreach (var t in c.Tariffs)
        {
            var p = t.PriceComponents;
            if (string.IsNullOrWhiteSpace(t.TariffId) || string.IsNullOrWhiteSpace(t.Version) || t.Currency != "EUR" ||
                t.PriceBasis != "NET" || t.SourceClassification != "SYNTHETIC" || t.BindingRule != "SESSION_START_LOCKED" ||
                t.PublishedAtMs < 0 || t.ValidFromMs < 0 || t.ValidToMs <= t.ValidFromMs ||
                !c.Operators.Any(o => o.Id == t.OperatorId && t.ApplicableParks.All(o.ParkIds.Contains)) ||
                !c.PaymentRoutes.Any(r => r.Id == t.PaymentRouteId && r.Kind == t.Eligibility) ||
                t.ApplicableParks.Length == 0 || t.Connectors.Length == 0 ||
                p.EnergyEurPerKwh < 0 || p.SessionEur < 0 || p.ChargingEurPerHour < 0 || p.OccupancyEurPerHour < 0 ||
                p.ChargingStepSeconds is < 1 or > 3600 || p.OccupancyStepSeconds is < 1 or > 3600 || p.OccupancyFreeSeconds < 0 ||
                t.RoundingPolicy != new RoundingPolicy()) throw new ArgumentException("Invalid synthetic tariff contract.");
        }
        // No implicit 'last import wins': overlapping prices for the same physical
        // connector and payment entitlement are rejected before a run can start.
        for (var i=0;i<c.Tariffs.Length;i++) for (var j=i+1;j<c.Tariffs.Length;j++)
        {
            var a=c.Tariffs[i]; var b=c.Tariffs[j];
            if (a.PaymentRouteId==b.PaymentRouteId && a.OperatorId==b.OperatorId &&
                a.ApplicableParks.Intersect(b.ApplicableParks).Any() && a.Connectors.Intersect(b.Connectors).Any() &&
                a.ValidFromMs < b.ValidToMs && b.ValidFromMs < a.ValidToMs) throw new ArgumentException("Overlapping tariff versions.");
        }
        foreach (var contract in c.Contracts)
            if (contract.ValidFromMs < 0 || contract.ValidToMs <= contract.ValidFromMs || contract.PublishedAtMs < 0 ||
                !c.Operators.Any(o=>o.Id==contract.OperatorId) ||
                !c.PaymentRoutes.Any(r=>r.Id==contract.PaymentRouteId && r.Kind=="CONTRACT"))
                throw new ArgumentException("Invalid fleet contract.");
        for (var i=0;i<c.Contracts.Length;i++) for (var j=i+1;j<c.Contracts.Length;j++)
        {
            var a=c.Contracts[i]; var b=c.Contracts[j];
            if (a.FleetId==b.FleetId && a.OperatorId==b.OperatorId && a.PaymentRouteId==b.PaymentRouteId &&
                a.ValidFromMs < b.ValidToMs && b.ValidFromMs < a.ValidToMs) throw new ArgumentException("Overlapping contract rights.");
        }
    }
}

public sealed class ChargingCostCalculator : IChargingCostCalculator
{
    public static decimal Meter(double cumulativeGridKwh)
    {
        if (!double.IsFinite(cumulativeGridKwh) || cumulativeGridKwh < 0) throw new ArgumentOutOfRangeException(nameof(cumulativeGridKwh));
        // Quantize the cumulative physics meter, not individual ticks. This keeps
        // billing invariant to consumer batching at the declared 10^-9 kWh resolution.
        return decimal.Round((decimal)cumulativeGridKwh, 9, MidpointRounding.ToEven);
    }
    public ChargingBill Calculate(PricingSnapshot pricing, BillingQuantities quantities)
    {
        if (pricing.Currency != "EUR" || pricing.PriceBasis != "NET" || pricing.Rounding != new RoundingPolicy() ||
            quantities.MeteredGridKwh < 0 || quantities.ChargingMilliseconds < 0 || quantities.OccupancyAfterChargingMilliseconds < 0 ||
            !quantities.SessionStarted && (quantities.MeteredGridKwh != 0 || quantities.ChargingMilliseconds != 0 || quantities.OccupancyAfterChargingMilliseconds != 0))
            throw new ArgumentException("Invalid price or physical billing quantities.");
        var p=pricing.Components;
        if (p.EnergyEurPerKwh < 0 || p.SessionEur < 0 || p.ChargingEurPerHour < 0 || p.OccupancyEurPerHour < 0 ||
            p.ChargingStepSeconds <= 0 || p.OccupancyStepSeconds <= 0 || p.OccupancyFreeSeconds < 0)
            throw new ArgumentException("Invalid price components.");
        static decimal Hours(long milliseconds, int step) => decimal.Ceiling(milliseconds / (step * 1000m)) * step / 3600;
        var q=quantities with { MeteredGridKwh=decimal.Round(quantities.MeteredGridKwh,9,MidpointRounding.ToEven) };
        CostLine Line(string name,decimal quantity,string unit,decimal price) => new(name,quantity,unit,price,
            new(decimal.Round(checked(quantity*price),2,MidpointRounding.ToEven)));
        CostLine[] lines = [Line("ENERGY",q.MeteredGridKwh,"kWh",p.EnergyEurPerKwh),
            Line("SESSION",q.SessionStarted?1:0,"session",p.SessionEur),
            Line("CHARGING_TIME",Hours(q.ChargingMilliseconds,p.ChargingStepSeconds),"h",p.ChargingEurPerHour),
            Line("POST_CHARGE_OCCUPANCY",Hours(Math.Max(0,q.OccupancyAfterChargingMilliseconds-p.OccupancyFreeSeconds*1000L),p.OccupancyStepSeconds),"h",p.OccupancyEurPerHour)];
        return new(pricing,q,lines,new(lines.Sum(l=>l.Amount.Amount)));
    }
}

public sealed class EconomicCostEvaluator : IEconomicCostEvaluator
{
    public EconomicCostEstimate Evaluate(ChargingBill bill, FleetEconomicProfile fleet, decimal remainingSeconds,
        decimal distanceKm, long nowMs, long targetArrivalMs, decimal timeValueMultiplier=1)
    {
        if (remainingSeconds < 0 || distanceKm < 0 || fleet.TimeValuePerHour < 0 || fleet.NonEnergyDistanceCostPerKm < 0 ||
            fleet.IncrementalLateCostPerHour < 0 || timeValueMultiplier < 0 || bill.Total.Currency != "EUR") throw new ArgumentException("Invalid economic valuation.");
        static Money Round(decimal amount) => new(decimal.Round(amount,2,MidpointRounding.ToEven));
        var time=Round(remainingSeconds/3600*fleet.TimeValuePerHour*timeValueMultiplier);
        var distance=Round(distanceKm*fleet.NonEnergyDistanceCostPerKm);
        // Lateness already incurred is sunk. Only the increment belongs in a new
        // decision; the separate trip ledger charges each full interval only once.
        var lateSeconds=Math.Max(0,(nowMs-targetArrivalMs)/1000m+remainingSeconds)-Math.Max(0,(nowMs-targetArrivalMs)/1000m);
        var late=Round(lateSeconds/3600*fleet.IncrementalLateCostPerHour);
        return new(bill,time,distance,late,bill.Total+time+distance+late,remainingSeconds,distanceKm,fleet.TimeValuePerHour*timeValueMultiplier);
    }
}
