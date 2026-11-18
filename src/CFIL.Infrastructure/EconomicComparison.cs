using System.Globalization;
using System.Text.Json;
using CFIL.Domain;

namespace CFIL.Infrastructure;

/// <summary>Paired, all-planned-truck evaluation. Monetary aggregation stays in decimal/.NET.</summary>
public static class EconomicComparison
{
    private static JsonElement Read(string path) => JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    private static decimal Number(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? decimal.Parse(value.GetString()!, CultureInfo.InvariantCulture) : value.GetDecimal();
    private static decimal Total(JsonElement report, string key) => Number(report.GetProperty("truckTotals").GetProperty(key).GetProperty("amount"));
    private static decimal Metric(JsonElement result, string kind, string key, bool mean = false)
    {
        var value = result.GetProperty("operational").GetProperty("byKind").GetProperty(kind).GetProperty(key);
        if (mean) value = value.GetProperty("mean");
        // A missing observed wait is unknown, not an invented zero-second wait.
        if (value.ValueKind == JsonValueKind.Null) throw new InvalidDataException($"Missing operational metric {kind}/{key}.");
        return Number(value);
    }

    public static object Evaluate(string root)
    {
        var frozen = Read(Path.Combine(root, "frozen-plan.json"));
        var plan = frozen.GetProperty("plan");
        var gates = plan.GetProperty("qualityGates");
        var seeds = plan.GetProperty("evaluationSeeds").EnumerateArray().Select(s => s.GetInt32()).ToArray();
        var rows = new Dictionary<(string Strategy, int Seed), (JsonElement Result, JsonElement Report)>();
        foreach (var item in frozen.GetProperty("matrix").EnumerateArray().Where(i => i.GetProperty("phase").GetString() == "evaluation"))
        {
            var result = Read(Path.Combine(root, "logs", item.GetProperty("name").GetString() + ".result.json"));
            var report = Read(Path.Combine(result.GetProperty("rawDirectory").GetString()!, "economic-report.json"));
            rows.Add((item.GetProperty("strategy").GetString()!, item.GetProperty("seed").GetInt32()), (result, report));
        }
        var comparisons = new Dictionary<string, object>();
        foreach (var contrast in plan.GetProperty("comparisons").EnumerateArray().Select(v => v.GetString()!))
        {
            var arms = contrast.Split("-vs-");
            var pairs = new List<object>();
            var monetary = new List<Dictionary<string, decimal>>();
            var allViolations = new List<object>();
            var complete = true;
            foreach (var seed in seeds)
            {
                var a = rows[(arms[1], seed)]; var b = rows[(arms[0], seed)];
                bool Usable((JsonElement Result, JsonElement Report) r) => r.Result.GetProperty("passed").GetBoolean()
                    && r.Result.GetProperty("operational").GetProperty("horizonSeconds").GetInt32() == plan.GetProperty("durationSeconds").GetInt32()
                    && r.Report.GetProperty("runStatus").GetString() == "completed"
                    && r.Report.GetProperty("allPlannedTrucks").GetInt32() == plan.GetProperty("truckCount").GetInt32()
                    && r.Report.GetProperty("observedTerminalTrucks").GetInt32() == plan.GetProperty("truckCount").GetInt32()
                    && r.Report.GetProperty("pricingComplete").GetBoolean();
                var usable = Usable(a) && Usable(b);
                complete &= usable;
                var differences = new Dictionary<string, decimal>();
                foreach (var key in new[] { "rawChargingBill", "energyFees", "sessionFees", "chargingTimeFees", "occupancyFees",
                    "valuedTime", "nonEnergyDistance", "incrementalLate", "inventoryAdjustment", "adjustedEnergyComparison", "adjustedEconomicCost" })
                    differences[key] = Total(b.Report, key) - Total(a.Report, key);
                monetary.Add(differences);
                var operational = new Dictionary<string, decimal>();
                foreach (var key in new[] { "arrived", "punctual", "stranded", "unsuccessful", "incomplete" })
                    operational[key] = Metric(b.Result, "Truck", key) - Metric(a.Result, "Truck", key);
                operational["truckHorizonSeconds"] = Metric(b.Result, "Truck", "horizonLimitedArrivalSeconds", true) - Metric(a.Result, "Truck", "horizonLimitedArrivalSeconds", true);
                operational["carArrived"] = Metric(b.Result, "Car", "arrived") - Metric(a.Result, "Car", "arrived");
                operational["carHorizonSeconds"] = Metric(b.Result, "Car", "horizonLimitedArrivalSeconds", true) - Metric(a.Result, "Car", "horizonLimitedArrivalSeconds", true);
                operational["carObservedWaitSeconds"] = Metric(b.Result, "Car", "queueWaitObservedSeconds", true) - Metric(a.Result, "Car", "queueWaitObservedSeconds", true);
                var violations = new List<object>();
                foreach (var (metric, gate, lower) in new[] {
                    ("arrived", "maxTruckArrivalLossPerSeed", true), ("punctual", "maxTruckPunctualLossPerSeed", true),
                    ("stranded", "maxAdditionalTruckStrandingsPerSeed", false), ("unsuccessful", "maxAdditionalUnsuccessfulTrucksPerSeed", false),
                    ("incomplete", "maxAdditionalIncompleteTrucksPerSeed", false), ("truckHorizonSeconds", "maxTruckHorizonIncreaseSecondsPerSeed", false),
                    ("carArrived", "maxCarArrivalLossPerSeed", true), ("carHorizonSeconds", "maxCarHorizonIncreaseSecondsPerSeed", false),
                    ("carObservedWaitSeconds", "maxCarObservedWaitIncreaseSecondsPerSeed", false) })
                {
                    var threshold = Number(gates.GetProperty(gate)) * (lower ? -1 : 1);
                    if (lower ? operational[metric] < threshold : operational[metric] > threshold)
                        violations.Add(new { seed, metric, difference = operational[metric], threshold, requiredOperator = lower ? ">=" : "<=" });
                }
                foreach (var (arm, row) in new[] { (arms[1], a), (arms[0], b) })
                {
                    var invariants = Read(Path.Combine(row.Result.GetProperty("rawDirectory").GetString()!, "manifest.json"));
                    // RunExecutor completes successfully only after every hard invariant check.
                    if (!row.Result.GetProperty("passed").GetBoolean() || invariants.GetProperty("status").GetString() != "completed")
                        violations.Add(new { seed, arm, metric = "hardInvariantOrRunFailure" });
                    if (row.Report.GetProperty("unresolvedSessions").GetInt32() > gates.GetProperty("unresolvedPrices").GetInt32())
                        violations.Add(new { seed, arm, metric = "unresolvedPrices" });
                }
                allViolations.AddRange(violations);
                pairs.Add(new { seed, dataComplete = usable, moneyDifferences = differences.ToDictionary(p => p.Key, p => new Money(p.Value)),
                    operationalDifferences = operational, billedGridKwhDifference = Number(b.Report.GetProperty("truckTotals").GetProperty("billedGridKwh")) - Number(a.Report.GetProperty("truckTotals").GetProperty("billedGridKwh")),
                    gatesPassed = violations.Count == 0, violations });
            }
            var means = monetary[0].Keys.ToDictionary(k => k, k => monetary.Average(m => m[k]));
            var sensitivity = plan.GetProperty("sensitivity").GetProperty("timeValueMultipliers").EnumerateArray().Select(Number).Select(multiplier =>
            {
                // Revalue observed time only. This deliberately does not simulate a new
                // policy or manufacture outcomes for an unvisited alternative park.
                var paired = monetary.Select((m, i) => new { seed = seeds[i], difference = new Money(m["adjustedEconomicCost"] + (multiplier - 1) * m["valuedTime"]) }).ToArray();
                var mean = paired.Average(p => p.difference.Amount);
                return new { timeValueMultiplier = multiplier, pairedSeeds = paired, meanDifference = new Money(mean),
                    verdict = !complete ? "INSUFFICIENT_DATA" : mean < 0 && allViolations.Count == 0 ? "DEMONSTRATED" : "NOT_DEMONSTRATED" };
            }).ToArray();
            comparisons[contrast] = new { dataComplete = complete,
                invoiceEffect = !complete ? "UNKNOWN" : means["rawChargingBill"] < 0 ? "LOWER" : means["rawChargingBill"] > 0 ? "HIGHER" : "UNCHANGED",
                economicBenefit = !complete ? "INSUFFICIENT_DATA" : means["adjustedEconomicCost"] < 0 && allViolations.Count == 0 ? "DEMONSTRATED" : "NOT_DEMONSTRATED",
                economicMetricImproved = means["adjustedEconomicCost"] < 0, operationalGatesPassed = allViolations.Count == 0,
                pairedSeeds = pairs, meanMoneyDifferences = means.ToDictionary(p => p.Key, p => new Money(p.Value)),
                unfavorableCostSeeds = seeds.Where((_, i) => monetary[i]["adjustedEconomicCost"] > 0).ToArray(),
                violations = allViolations, sensitivity };
        }
        return new { version = "CFIL05-ECONOMIC-QUALITY-V1", comparisons,
            population = "All 50 planned trucks per arm and seed, including unsuccessful and incomplete trips.",
            interpretation = "Treatment minus control; negative financial differences are lower costs. Five paired seeds are not proof of statistical significance.",
            sensitivityMeaning = "Revaluation of observed outcomes only; no alternative operational results or confidence intervals.",
            counterfactualAlternativeLabels = "UNAVAILABLE", scope = "Synthetic simulation, not real fleet approval." };
    }
}
