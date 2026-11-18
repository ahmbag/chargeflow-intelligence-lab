"""Planning-only TraCI operations; the legacy step/command path is unchanged."""
import hashlib
import json
import traci
import sumolib


class PlanningBridge:
    def legal_path(self, start, end, vehicle):
        # SUMO 1.25 findRoute constructs a temporary vehicle and can consume the
        # shared vehicle-parsing RNG. Even an ADVISORY getter then changes future
        # traffic. Route on the actual netconvert network with SUMO's sumolib
        # permission-aware graph instead; the live simulator remains untouched.
        # This is a free-flow estimate, not a forecast of future congestion.
        if not hasattr(self, "planning_net"):
            self.planning_net = sumolib.net.readNet(str(self.planning_network_path), withInternal=True)
        definition = self.definitions[vehicle]
        vehicle_class = "truck" if definition["kind"] == "Truck" else "passenger"
        path, _ = self.planning_net.getFastestPath(self.planning_net.getEdge(start), self.planning_net.getEdge(end),
                                                vClass=vehicle_class, withInternal=True)
        if not path:
            raise ValueError("No connected vehicle-compatible route")
        distance = sum(edge.getLength() for edge in path)
        seconds = sum(edge.getLength()/min(definition["desiredSpeedMps"],edge.getSpeed()) for edge in path)
        return [edge.getID() for edge in path if edge.getFunction() != "internal"], distance, seconds

    def route_candidate(self, vehicle, park_id):
        current = traci.vehicle.getRoadID(vehicle)
        position = traci.vehicle.getLanePosition(vehicle)
        current_route = list(traci.vehicle.getRoute(vehicle))
        result = dict(parkId=park_id, legal=False, rejection=None, currentEdge=current,
            currentLanePositionM=position, currentRoute=current_route, routeEdges=[], approachDistanceM=0,
            approachSeconds=0, continuationDistanceM=0, continuationSeconds=0, queriedAtMs=self.time)
        park = self.parks[park_id]
        definition = self.definitions[vehicle]
        # Only highway decisions upstream of a legal exit are supported. No
        # teleport, reversal, future weight file or scenario disturbance is read.
        if not current.startswith("H") or traci.vehicle.getPosition(vehicle)[0] >= park["positionKm"] * 1000:
            result["rejection"] = "EXIT_ALREADY_PASSED"
            return result
        if park["positionKm"] >= definition["routeEndKm"]:
            result["rejection"] = "BEYOND_DESTINATION"
            return result
        destination = self.remaining_highway(vehicle, 0)[-1]
        try:
            inbound, inbound_length, inbound_seconds = self.legal_path(current, park_id+"-queue", vehicle)
            outbound, outbound_length, outbound_seconds = self.legal_path(park_id+"-queue", destination, vehicle)
            if not inbound or not outbound or inbound[0] != current:
                raise ValueError("No connected vehicle-compatible route")
            edges = inbound + outbound[1:]
            if len(edges) != len(set(edges)):
                raise ValueError("Loop or backward route")
            # The actual queue stop is at endPos=550 on its 600 m lane. Remove
            # the current lane prefix and the final 50 m, rather than pretending
            # the vehicle starts at the beginning of its current edge.
            tail = traci.lane.getLength(park_id+"-queue_0") - 550
            distance = max(0, inbound_length - position - tail)
            speed = min(definition["desiredSpeedMps"], traci.lane.getMaxSpeed(current+"_0"))
            seconds = max(0, inbound_seconds - position/speed - tail/8.33)
            result.update(legal=True, routeEdges=edges, approachDistanceM=distance, approachSeconds=seconds,
                continuationDistanceM=outbound_length-550, continuationSeconds=max(0,outbound_seconds-550/8.33))
        except (traci.TraCIException, ValueError) as error:
            result["rejection"] = "NO_LEGAL_ROUTE: " + str(error)
        return result

    def planning_routes(self, request):
        vehicle = request["vehicleId"]
        if vehicle not in self.active:
            raise ValueError("Planning requires an active SUMO vehicle")
        return [self.route_candidate(vehicle, park) for park in request["parkIds"]]

    def plan_state(self, application, status, error, operations, duplicate=False):
        vehicle = application["vehicleId"]
        active = vehicle in self.active
        return dict(planId=application["planId"], vehicleId=vehicle, revision=application["revision"],
            status=status, error=error, currentEdge=traci.vehicle.getRoadID(vehicle) if active else "",
            actualRoute=list(traci.vehicle.getRoute(vehicle)) if active else [],
            actualStops=[stop.stoppingPlaceID for stop in traci.vehicle.getStops(vehicle)] if active else [],
            completedOperations=operations, duplicate=duplicate,
            routeIndex=traci.vehicle.getRouteIndex(vehicle) if active else -1)

    def apply_plan(self, application):
        if not hasattr(self, "plan_receipts"):
            self.plan_receipts, self.plan_revisions = {}, {}
        plan, vehicle = application["planId"], application["vehicleId"]
        fingerprint = hashlib.sha256(json.dumps(application,sort_keys=True,separators=(",", ":")).encode()).hexdigest()
        if plan in self.plan_receipts:
            old_hash, receipt = self.plan_receipts[plan]
            if old_hash != fingerprint:
                return self.plan_state(application,"REJECTED","Conflicting repeated command",[])
            return dict(receipt, duplicate=True)
        route = application["route"]
        error = None
        if self.time > application["validUntilMs"]:
            error = "Expired plan"
        elif application["expectedRevision"] != self.plan_revisions.get(vehicle,0) or application["revision"] != application["expectedRevision"]+1:
            error = "Stale plan revision"
        elif vehicle not in self.active or traci.vehicle.isStopped(vehicle):
            error = "Vehicle not controllable"
        else:
            current = self.route_candidate(vehicle,route["parkId"])
            if not current["legal"] or current != route:
                error = "Route or position changed since validation"
        if error:
            return self.plan_state(application,"REJECTED",error,[])
        completed = []
        try:
            # These calls are deliberately not described as atomic. After each
            # mutation the receipt records the real state, even on partial failure.
            for index in reversed(range(len(traci.vehicle.getStops(vehicle)))):
                traci.vehicle.replaceStop(vehicle,index,"",teleport=0)
            completed.append("old-stops-cleared")
            traci.vehicle.setRoute(vehicle,route["routeEdges"])
            completed.append("route-set")
            if application.get("fault") == "after-route":
                raise RuntimeError("Injected failure after actual SUMO setRoute")
            traci.vehicle.setParkingAreaStop(vehicle,route["parkId"]+"-Q",duration=1e9)
            completed.append("stop-set")
            self.stops[vehicle] = route["parkId"]+"-Q"
            self.remember_total(vehicle)
            receipt = self.plan_state(application,"APPLIED",None,completed)
            # getRoute retains already-travelled edges after setRoute. Compare the
            # authoritative suffix at SUMO's current route index, while keeping
            # the complete observed route in the receipt for reconciliation.
            if receipt["actualRoute"][receipt["routeIndex"]:] != route["routeEdges"] or receipt["actualStops"] != [route["parkId"]+"-Q"]:
                raise RuntimeError("SUMO did not retain requested route/stop")
            self.plan_revisions[vehicle] = application["revision"]
        except Exception as error:
            receipt = self.plan_state(application,"APPLY_FAILED",f"{type(error).__name__}: {error}",completed)
        self.plan_receipts[plan] = (fingerprint,receipt)
        return receipt
