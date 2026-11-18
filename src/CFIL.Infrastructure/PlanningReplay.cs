using System.Text.Json;
using CFIL.Domain;
using CFIL.Simulation;

namespace CFIL.Infrastructure;

public static class PlanningReplay
{
    public static object Validate(string directory)
    {
        var options = JsonDefaults.Deserialize<PlanningOptions>(File.ReadAllText(Path.Combine(directory, "planning-config.json")));
        var economicPath=Path.Combine(directory,"economic-inputs.json");
        var economic=File.Exists(economicPath)?EconomicJson.Read<EconomicHeader>(File.ReadAllText(economicPath)):null;
        long sequence = 0; var decisions = 0; var applied = 0;
        var applying = new Dictionary<string, PlanApplication>();
        foreach (var line in File.ReadLines(Path.Combine(directory, "decisions.jsonl")))
        {
            using var document = JsonDocument.Parse(line); var row = document.RootElement;
            if (row.GetProperty("sequence").GetInt64() != ++sequence) throw new InvalidDataException("Decision trace sequence gap.");
            var payload = row.GetProperty("payload").GetRawText();
            switch (row.GetProperty("type").GetString())
            {
                case "Decision":
                    var proposal = JsonDefaults.Deserialize<PlanProposal>(payload);
                    var scores = ChargingPlanningRules.Score(proposal.Context, proposal.Candidates, proposal.Forecast, options);
                    var selected = ChargingPlanningRules.Select(proposal.Context, scores, options);
                    if (proposal.Context.PreviousParkId is { } old &&
                        (proposal.Context.Parks.Single(p => p.Id == old).PositionKm - proposal.Context.Vehicle.PositionKm) * 1000 <= options.CommitmentDistanceM)
                        selected = scores.Any(s => s.ParkId == old) ? old : null;
                    if (economic?.Options.Mode==EconomicMode.ECONOMIC_CONTROLLED)
                    {
                        var fleet=economic.Fleets.Profiles.Single(p=>p.Id==economic.Assignments.Single(a=>a.VehicleId==proposal.Context.VehicleId).ProfileId);
                        var definition=economic.Population.Single(v=>v.Id==proposal.Context.VehicleId);
                        var technical=proposal with { SelectedParkId=selected };
                        var (costOptions,_,_)=EconomicPlanningRules.Options(technical,definition,fleet,new TariffResolver(economic.Tariffs),economic.Options,EconomicService.CompatibleConnectors(economic,technical));
                        selected=EconomicPlanningRules.Select(technical,costOptions,economic.Options,options).Park;
                    }
                    if (JsonDefaults.Serialize(scores) != JsonDefaults.Serialize(proposal.Scores) || selected != proposal.SelectedParkId)
                        throw new InvalidDataException("Decision replay differs from frozen evidence.");
                    decisions++; break;
                case "APPLYING":
                    var command = JsonDefaults.Deserialize<PlanApplication>(payload);
                    if (!applying.TryAdd(command.PlanId, command)) throw new InvalidDataException("Duplicate command intent.");
                    break;
                case "APPLIED":
                    var receipt = JsonDefaults.Deserialize<PlanReceipt>(payload);
                    if (!applying.Remove(receipt.PlanId, out var intent) || receipt.Revision != intent.Revision ||
                        receipt.RouteIndex < 0 || receipt.RouteIndex >= receipt.ActualRoute.Length ||
                        receipt.ActualRoute[receipt.RouteIndex] != receipt.CurrentEdge ||
                        !receipt.ActualRoute.Skip(receipt.RouteIndex).SequenceEqual(intent.Route.RouteEdges) ||
                        !receipt.ActualStops.SequenceEqual([intent.Route.ParkId + "-Q"]))
                        throw new InvalidDataException("Applied receipt lacks matching real route/stop evidence.");
                    applied++; break;
                case "APPLY_FAILED": case "REJECTED":
                    applying.Remove(JsonDefaults.Deserialize<PlanReceipt>(payload).PlanId); break;
            }
        }
        if (applying.Count > 0) throw new InvalidDataException("Unreconciled external command intent.");
        return new { passed = true, decisions, applied, records = sequence,
            meaning = "Frozen decision replay; no counterfactual outcome inference." };
    }
}
