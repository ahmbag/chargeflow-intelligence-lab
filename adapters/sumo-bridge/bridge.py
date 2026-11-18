"""Strict JSON-lines protocol: stdout is exclusively protocol; diagnostics go to stderr.

SUMO owns every position, lane change, stop and re-entry. No moveTo/remove calls are used.
The bridge intentionally has no battery, charging-power or admission policy.
"""
from __future__ import annotations

import contextlib
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import sys
import xml.etree.ElementTree as ET

import sumo
import traci
import traci.constants as tc
import psutil


def write_xml(path, root):
    ET.indent(root)
    ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)


def binary(name):
    suffix = ".exe" if os.name == "nt" else ""
    return str(Path(sumo.__file__).parent / "bin" / (name + suffix))


def build_network(scenario, population, directory):
    directory.mkdir(parents=True, exist_ok=True)
    nodes, edges, additional = ET.Element("nodes"), ET.Element("edges"), ET.Element("additional")
    network = scenario["network"]
    positions = {0.0, network["lengthKm"] * 1000}
    positions.update(v["routeEndKm"] * 1000 for v in population)
    for park in scenario["parks"]:
        positions.update([park["positionKm"] * 1000, park["positionKm"] * 1000 + 1200])
    positions = sorted(positions)
    for i, x in enumerate(positions):
        ET.SubElement(nodes, "node", id=f"H{i}", x=str(x), y="0", type="priority")
    highway = []
    for i in range(len(positions) - 1):
        eid = f"H{i}"
        ET.SubElement(edges, "edge", id=eid, **{"from": f"H{i}", "to": f"H{i+1}"},
                      numLanes=str(network["lanes"]), speed=str(network["speedLimitMps"]), priority="3")
        highway.append((eid, positions[i], positions[i+1]))
    for park in scenario["parks"]:
        p, x = park["id"], park["positionKm"] * 1000
        start, end = positions.index(x), positions.index(x + 1200)
        for name, dx, y in [("A", 100, -200), ("B", 700, -200), ("D", 1000, -200)]:
            ET.SubElement(nodes, "node", id=p+name, x=str(x+dx), y=str(y), type="priority")
        def edge(eid, source, target, speed="8.33"):
            ET.SubElement(edges, "edge", id=eid, **{"from": source, "to": target}, numLanes="1", speed=speed, priority="1")
        edge(p+"-in", f"H{start}", p+"A", "13.89")
        edge(p+"-queue", p+"A", p+"B")
        edge(p+"-bypass", p+"B", p+"D")
        edge(p+"-out", p+"D", f"H{end}", "13.89")
        ET.SubElement(additional, "parkingArea", id=p+"-Q", lane=p+"-queue_0", startPos="50", endPos="550",
                      roadsideCapacity=str(park["queueCapacity"]), length="25", width="3.5", onRoad="false")
        for i, bay in enumerate(park["bays"]):
            b = p+"-"+bay["id"]
            ET.SubElement(nodes, "node", id=b, x=str(x+850), y=str(-260-i*35), type="priority")
            edge(b+"-in", p+"B", b)
            edge(b+"-out", b, p+"D")
            ET.SubElement(additional, "parkingArea", id=b, lane=b+"-in_0", startPos="50", endPos="100",
                          roadsideCapacity="1", length=str(bay["lengthM"]), width=str(bay["widthM"]), onRoad="false")
    write_xml(directory / "nodes.nod.xml", nodes)
    write_xml(directory / "edges.edg.xml", edges)
    write_xml(directory / "parking.add.xml", additional)
    subprocess.run([binary("netconvert"), "--node-files", str(directory / "nodes.nod.xml"), "--edge-files",
                    str(directory / "edges.edg.xml"), "--output-file", str(directory / "network.net.xml"),
                    "--no-turnarounds", "true", "--junctions.corner-detail", "0"],
                   stdout=sys.stderr, stderr=sys.stderr, check=True, timeout=60)
    routes = ET.Element("routes")
    for v in population:
        ET.SubElement(routes, "vType", id=v["id"], vClass="truck" if v["kind"] == "Truck" else "passenger",
                      length=str(v["lengthM"]), width=str(v["widthM"]), maxSpeed=str(v["desiredSpeedMps"]),
                      accel="1.2" if v["kind"] == "Truck" else "2.6", decel="4.5", sigma="0.5", speedFactor="1")
    for v in sorted(population, key=lambda v: (v["departureSeconds"], v["id"])):
        vehicle = ET.SubElement(routes, "vehicle", id=v["id"], type=v["id"], depart=str(v["departureSeconds"]),
                                departLane="best", departSpeed="0")
        ET.SubElement(vehicle, "route", edges=" ".join(e for e, _, end in highway if end <= v["routeEndKm"]*1000))
    write_xml(directory / "vehicles.rou.xml", routes)
    return highway


from planning_bridge import PlanningBridge


class Bridge(PlanningBridge):
    def __init__(self):
        self.connected = False
        self.time = 0
        self.active = set()
        self.stops = {}
        self.last = {}
        self.total_distance = {}
        self.final_edges = {}
        self.lane_lengths = {}
        self.reinserting = set()
        self.working_set = 0
        self.minimal = set()
        self.stationary_steps = {}
        self.stop_states = {}
        self.full_variables = [tc.VAR_DISTANCE, tc.VAR_POSITION, tc.VAR_SPEED, tc.VAR_ROAD_ID,
                               tc.VAR_STOPSTATE, tc.VAR_LANEPOSITION, tc.VAR_LANE_ID]

    def initialize(self, request):
        if self.connected:
            raise ValueError("Already initialized")
        self.scenario = request["scenario"]
        self.definitions = {v["id"]: v for v in request["population"]}
        self.parks = {p["id"]: p for p in self.scenario["parks"]}
        directory = Path(request["outputDirectory"]).resolve() / "sumo"
        self.highway = build_network(self.scenario, request["population"], directory)
        self.planning_network_path = directory / "network.net.xml"
        with contextlib.redirect_stdout(sys.stderr):
            traci.start([binary("sumo"), "--net-file", str(directory / "network.net.xml"),
                         "--route-files", str(directory / "vehicles.rou.xml"), "--additional-files", str(directory / "parking.add.xml"),
                         "--seed", str(self.scenario["seed"]), "--step-length", "1", "--time-to-teleport", "-1",
                         "--collision.action", "warn", "--no-step-log", "true", "--duration-log.disable", "true",
                         "--xml-validation", "never", "--log", str(directory / "sumo.log")], numRetries=5)
        self.connected = True
        return {"provider": "SUMO/TraCI", "version": traci.getVersion()[1], "pythonVersion": platform.python_version(),
                "platform": platform.platform(), "networkHash": hashlib.sha256(ET.tostring(ET.parse(directory / "network.net.xml").getroot())).hexdigest()}

    def remaining_highway(self, vehicle, after):
        end = self.definitions[vehicle]["routeEndKm"] * 1000
        return [e for e, start, finish in self.highway if start >= after and finish <= end]

    def remember_total(self, vehicle):
        last_edge = traci.vehicle.getRoute(vehicle)[-1]
        self.final_edges[vehicle] = last_edge

    def command(self, command):
        v, kind = command["vehicleId"], command["type"]
        if v not in self.active:
            raise ValueError(f"Command for inactive vehicle {v}")
        if v in self.minimal:
            traci.vehicle.unsubscribe(v)
            traci.vehicle.subscribe(v, self.full_variables)
            self.minimal.remove(v)
        self.stationary_steps[v] = 0
        if kind == "strand":
            # SUMO's safety/deceleration checks remain enabled. .NET reserves enough energy for
            # the braking distance and marks Stranded only after this physical stop is observed.
            traci.vehicle.setSpeed(v, 0)
            return
        if kind == "approach":
            p = command["parkId"]
            x = self.parks[p]["positionKm"] * 1000
            current = traci.vehicle.getRoadID(v)
            route = list(traci.vehicle.getRoute(v))[traci.vehicle.getRouteIndex(v):]
            before = [e for e in route if e in {e for e, _, end in self.highway if end <= x}]
            if not before or before[0] != current:
                raise ValueError("Park is not reachable ahead on the current route")
            traci.vehicle.setRoute(v, before + [p+"-in", p+"-queue", p+"-bypass", p+"-out"] + self.remaining_highway(v, x+1200))
            traci.vehicle.setParkingAreaStop(v, p+"-Q", duration=1e9)
            self.stops[v] = p+"-Q"
        elif kind == "bay":
            p, b = command["parkId"], command["parkId"]+"-"+command["bayId"]
            if not traci.vehicle.isStoppedParking(v):
                raise ValueError("Cannot admit a vehicle before physical queue arrival")
            traci.vehicle.resume(v)
            self.reinserting.add(v)
            traci.vehicle.setRoute(v, [p+"-queue", b+"-in", b+"-out", p+"-out"] +
                                   self.remaining_highway(v, self.parks[p]["positionKm"]*1000+1200))
            traci.vehicle.setParkingAreaStop(v, b, duration=1e9)
            self.stops[v] = b
        elif kind == "release":
            if not traci.vehicle.isStoppedParking(v):
                raise ValueError("Release requires a physical stop")
            traci.vehicle.resume(v)
            self.reinserting.add(v)
            self.stops.pop(v, None)
        else:
            raise ValueError(f"Unknown command: {kind}")
        self.remember_total(v)

    def step(self, request):
        if not self.connected or request["simulationTimeMs"] != self.time+1000 or request["elapsedSeconds"] != 1:
            raise ValueError("Non-monotonic or unsupported simulation step")
        for command in request["commands"]:
            self.command(command)
        traci.simulationStep(request["simulationTimeMs"]/1000)
        self.time = request["simulationTimeMs"]
        if self.time % 60000 == 0:
            parent = psutil.Process()
            self.working_set = sum(p.memory_info().rss for p in [parent] + parent.children(recursive=True) if p.is_running())
        entered = sorted(traci.simulation.getDepartedIDList())
        arrived = sorted(traci.simulation.getArrivedIDList())
        for v in entered:
            self.active.add(v)
            traci.vehicle.subscribe(v, self.full_variables)
            self.remember_total(v)
        observations = []
        unchanged = []
        for v in arrived:
            prior = self.last[v]
            observations.append({"vehicleId": v, "distanceM": self.total_distance[v],
                                 "positionKm": self.definitions[v]["routeEndKm"], "speedMps": prior["speedMps"],
                                 "edgeId": prior["edgeId"], "stoppedAt": None, "arrived": True})
            self.active.remove(v)
            self.minimal.discard(v)
        values = traci.vehicle.getAllSubscriptionResults()
        for v in sorted(self.active):
            data = values[v]
            if v in self.minimal:
                if (data[tc.VAR_SPEED] == 0 and data[tc.VAR_DISTANCE] == self.last[v]["distanceM"]
                        and data[tc.VAR_STOPSTATE] == self.stop_states[v]):
                    unchanged.append(v)
                    continue
                # subscribe returns the full CURRENT state immediately; there is no extra simulation
                # step and hence no lost motion when a queued vehicle starts moving again.
                traci.vehicle.unsubscribe(v)
                traci.vehicle.subscribe(v, self.full_variables)
                data = traci.vehicle.getSubscriptionResults(v)
                self.minimal.remove(v)
                self.stationary_steps[v] = 0
            if data[tc.VAR_SPEED] < 0:
                # Parked vehicles can report INVALID_DOUBLE_VALUE for speed during SUMO's
                # off-road parking transition. Only an explicitly stopped vehicle may map to zero.
                if data[tc.VAR_STOPSTATE] & 1:
                    data[tc.VAR_SPEED] = 0.0
                elif v in self.reinserting and abs(data[tc.VAR_DISTANCE] - self.last[v]["distanceM"]) < 1e-6:
                    # After resume SUMO may wait off-road for a safe insertion gap. During that
                    # interval position/speed are unavailable, but its odometer is unchanged.
                    # Retain the last SUMO position; never synthesize forward movement.
                    observation = dict(self.last[v], speedMps=0.0, stoppedAt=None)
                    observations.append(observation)
                    continue
                else:
                    raise ValueError(f"Invalid moving-vehicle subscription {v}: {data}")
            else:
                self.reinserting.discard(v)
            if data[tc.VAR_ROAD_ID] == self.final_edges[v]:
                # SUMO removes arrived vehicles before subscriptions can return their last odometer.
                # On the final lane we can obtain the exact remaining geometric distance instead.
                lane = data[tc.VAR_LANE_ID]
                if lane not in self.lane_lengths:
                    self.lane_lengths[lane] = traci.lane.getLength(lane)
                self.total_distance[v] = data[tc.VAR_DISTANCE] + self.lane_lengths[lane] - data[tc.VAR_LANEPOSITION]
            observation = {"vehicleId": v, "distanceM": data[tc.VAR_DISTANCE], "positionKm": data[tc.VAR_POSITION][0]/1000,
                           "speedMps": data[tc.VAR_SPEED], "edgeId": data[tc.VAR_ROAD_ID],
                           "stoppedAt": self.stops.get(v) if data[tc.VAR_STOPSTATE] & 3 == 3 else None, "arrived": False}
            if observation["speedMps"] == 0 and observation == self.last.get(v):
                unchanged.append(v)
                self.stationary_steps[v] = self.stationary_steps.get(v, 0) + 1
            else:
                observations.append(observation)
                self.stationary_steps[v] = 0
            self.last[v] = observation
            self.stop_states[v] = data[tc.VAR_STOPSTATE]
            if self.stationary_steps[v] >= 5 and (observation["edgeId"].startswith("H") or observation["stoppedAt"] is not None):
                # Static parking geometry and straight highway lanes cannot change longitudinal
                # position/edge without odometer, speed or stop-state changes. Keep those three
                # authoritative SUMO observations live; avoid repeatedly decoding four redundant fields.
                # Internal/curved access edges retain full subscriptions. Teleports are always fatal.
                # SUMO merges repeated subscriptions rather than replacing their variable sets.
                # Unsubscribe first, otherwise the supposedly reduced request still decodes all fields.
                traci.vehicle.unsubscribe(v)
                traci.vehicle.subscribe(v, [tc.VAR_DISTANCE, tc.VAR_SPEED, tc.VAR_STOPSTATE])
                self.minimal.add(v)
        return {"simulationTimeMs": self.time, "observations": sorted(observations, key=lambda v: v["vehicleId"]),
                "entered": entered, "arrived": arrived, "teleports": traci.simulation.getStartingTeleportNumber(),
                "collisions": traci.simulation.getCollidingVehiclesNumber(), "providerWorkingSetBytes": self.working_set,
                "unchangedVehicleIds": unchanged}

    def close(self):
        if self.connected:
            with contextlib.redirect_stdout(sys.stderr):
                traci.close()
            self.connected = False


def main():
    bridge = Bridge()
    try:
        for line in sys.stdin:
            request = json.loads(line)
            try:
                operation = request["operation"]
                if operation == "initialize":
                    result = bridge.initialize(request)
                elif operation == "step":
                    result = bridge.step(request)
                elif operation == "planning-routes":
                    result = bridge.planning_routes(request)
                elif operation == "apply-plan":
                    result = bridge.apply_plan(request["application"])
                elif operation == "close":
                    bridge.close()
                    result = {"closed": True}
                else:
                    raise ValueError("Unknown operation")
                print(json.dumps({"ok": True, "result": result}, allow_nan=False, separators=(",", ":")), flush=True)
            except Exception as error:
                print(json.dumps({"ok": False, "error": f"{type(error).__name__}: {error}"}), flush=True)
                return 1
    finally:
        bridge.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
