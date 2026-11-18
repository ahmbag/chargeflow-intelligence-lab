"""Queue-time observer. No scenario reader, traffic model or charging policy exists here."""
import json
from .contracts import (FEATURE_VERSION, TARGET, TARGET_VERSION, canonical, digest, number, require, validate_features)
from .learner_contract import deliver


class Projector:
    def __init__(self, store):
        self.store, self.db = store, store.db

    def profile(self, vehicle):
        row = self.db.execute("SELECT data FROM profiles WHERE id=?", (vehicle,)).fetchone()
        require(row is not None, "Vehicle has no observable registration")
        return json.loads(row[0])

    def park(self, park):
        row = self.db.execute("SELECT data FROM parks WHERE id=?", (park,)).fetchone()
        require(row is not None, "Unknown observable park")
        return json.loads(row[0])

    def base(self, e, attempt, vehicle, message_type):
        oid = attempt + ":queue-v1"
        return dict(schemaVersion="1.0", messageId=oid + (":opened" if message_type == "ObservationOpened" else ":resolved"),
                    messageType=message_type, runId=e["runId"], observationId=oid, chargingAttemptId=attempt, vehicleId=vehicle,
                    target=TARGET, targetVersion=TARGET_VERSION, featureSchemaVersion=FEATURE_VERSION,
                    occurredAtSimTimeMs=e["simulationTimeMs"], availableAtSimTimeMs=e["simulationTimeMs"],
                    sourceCutoffSequence=e["sequence"])

    def resolve(self, e, attempt, status, reason):
        row = self.db.execute("SELECT * FROM observations WHERE attempt=?", (attempt,)).fetchone()
        require(row is not None, "Outcome without queue observation")
        require(self.db.execute("SELECT 1 FROM outcomes WHERE observation=?", (row["id"],)).fetchone() is None,
                "Attempt resolved twice")
        seconds = (e["simulationTimeMs"] - row["opened"]) / 1000
        require(seconds >= 0, "Negative waiting interval")
        m = self.base(e, attempt, row["vehicle"], "OutcomeResolved")
        m.update(status=status, targetSeconds=seconds if status == "OBSERVED" else None,
                 lowerBoundSeconds=None if status == "OBSERVED" else seconds, reason=reason)
        self.db.execute("INSERT INTO outcomes VALUES(?,?)", (row["id"], canonical(m)))
        deliver(self.db, m)

    def finalize(self, e, complete):
        # Terminal evidence, never a consumer timeout, closes the observation horizon. Already
        # observed starts stay OBSERVED even when their charging session is still in progress.
        for row in self.db.execute("""SELECT attempt FROM observations o WHERE NOT EXISTS
            (SELECT 1 FROM outcomes r WHERE r.observation=o.id) ORDER BY o.opened,o.id""").fetchall():
            self.resolve(e, row[0], "RIGHT_CENSORED" if complete else "INCOMPLETE_RUN",
                         "regular-observation-horizon" if complete else "producer-terminated")
        if not complete:
            for row in self.db.execute("""SELECT a.id,a.vehicle FROM attempts a WHERE NOT EXISTS
                (SELECT 1 FROM observations o WHERE o.attempt=a.id) AND NOT EXISTS
                (SELECT 1 FROM exclusions x WHERE x.attempt=a.id)""").fetchall():
                self.db.execute("INSERT INTO exclusions VALUES(?,?,?,?)",
                                (row[0], row[1], self.profile(row[1])["kind"], "incomplete-before-queue"))
        self.store.put("terminal", "completed" if complete else "incomplete")

    def apply(self, e):
        p, typ, vehicle, now = e["payload"], e["eventType"], e["entityId"], e["simulationTimeMs"]
        if typ == "SimulationStarted":
            require(e["sequence"] == 1 and self.store.get("scenario") is None, "Invalid stream start")
            require(isinstance(p.get("id"), str) and isinstance(p.get("scenarioHash"), str), "Missing stream provenance")
            self.store.put("scenario", p["id"])
            self.store.put("scenarioHash", p["scenarioHash"])
        elif typ == "ObservationContractPublished":
            require(e["sequence"] == 2 and set(p) == {"contractVersion", "parks"}
                    and p["contractVersion"] == "1.0", "Unsupported observable contract (legacy logs require explicit migration)")
            require(isinstance(p["parks"], list) and 0 < len(p["parks"]) <= 100, "Invalid park registry")
            for park in p["parks"]:
                require(set(park) == {"id", "availableGridKw", "bays", "blockedBays"} and number(park["availableGridKw"]), "Invalid park data")
                require(isinstance(park["id"], str) and isinstance(park["bays"], list) and len(set(park["bays"])) == len(park["bays"])
                        and all(isinstance(b, str) for b in park["bays"]) and set(park["blockedBays"]) <= set(park["bays"]), "Invalid bays")
                self.db.execute("INSERT INTO parks VALUES(?,?)", (park["id"], canonical(park)))
                self.db.execute("INSERT INTO history VALUES(?,0,0)", (park["id"],))
            self.store.put("observerVersion", "1.0")
        elif typ == "ObservableVehicleRegistered":
            require(set(p) == {"contractVersion", "kind", "capacityKwh", "maxGridChargingKw", "compatibleBays"}
                    and p["contractVersion"] == "1.0" and p["kind"] in ("Car", "Truck")
                    and number(p["capacityKwh"]) and p["capacityKwh"] > 0
                    and number(p["maxGridChargingKw"]) and p["maxGridChargingKw"] > 0, "Invalid observable profile")
            require(set(p["compatibleBays"]) == {r[0] for r in self.db.execute("SELECT id FROM parks")}, "Incomplete compatibility registry")
            for park, bays in p["compatibleBays"].items():
                require(isinstance(bays, list) and len(set(bays)) == len(bays) and set(bays) <= set(self.park(park)["bays"]), "Unknown compatible bay")
            self.db.execute("INSERT INTO profiles VALUES(?,?)", (vehicle, canonical(p)))
        elif typ == "ChargingParkCapacityChanged":
            park = self.park(vehicle)
            require(set(p) == {"availableGridKw", "blockedBays"} and number(p["availableGridKw"])
                    and set(p["blockedBays"]) <= set(park["bays"]), "Invalid announced capacity")
            park.update(p)
            self.db.execute("UPDATE parks SET data=? WHERE id=?", (canonical(park), vehicle))
        elif typ == "ChargingDecisionMade":
            self.profile(vehicle)
            self.park(p["parkId"])
            require(p["vehicleId"] == vehicle and p["decisionTimeMs"] == now
                    and p["decisionId"] == f'{e["runId"]}:decision:{e["sequence"]:09d}', "Invalid attempt identity")
            self.db.execute("INSERT INTO attempts VALUES(?,?,?,NULL)", (p["decisionId"], vehicle, p["parkId"]))
        elif typ == "QueueEntered":
            require("chargingAttemptId" in p and "soc" in p, "Legacy QueueEntered lacks required observer fields")
            attempt, park_id = p["chargingAttemptId"], p["parkId"]
            a = self.db.execute("SELECT * FROM attempts WHERE id=?", (attempt,)).fetchone()
            require(a is not None and a["vehicle"] == vehicle and a["park"] == park_id and a["outcome"] is None, "Queue/attempt mismatch")
            profile, park = self.profile(vehicle), self.park(park_id)
            compatible = set(profile["compatibleBays"][park_id])
            ahead = sum(bool(compatible & set(self.profile(r[0])["compatibleBays"][park_id]))
                        for r in self.db.execute("SELECT vehicle FROM queue WHERE park=? ORDER BY sequence", (park_id,)))
            occupied = sum(r[0] in compatible for r in self.db.execute("SELECT bay FROM occupants WHERE park=?", (park_id,)))
            # Closed-right window (t-900s,t], ordered by source sequence. The current arrival
            # is excluded; later events at exactly t are not yet visible either.
            self.db.execute("DELETE FROM arrivals WHERE time<=?", (now - 900000,))
            arrivals = self.db.execute("SELECT count(*) FROM arrivals WHERE park=?", (park_id,)).fetchone()[0]
            hist = self.db.execute("SELECT count,total_seconds FROM history WHERE park=?", (park_id,)).fetchone()
            features = dict(vehicleKind=profile["kind"], soc=p["soc"], batteryCapacityKwh=profile["capacityKwh"],
                            maxGridChargingKw=profile["maxGridChargingKw"], vehiclesAhead=ahead, occupiedCompatibleBays=occupied,
                            compatibleBayCount=len(compatible), blockedCompatibleBays=len(compatible & set(park["blockedBays"])),
                            availableGridKw=park["availableGridKw"], arrivalsLast900Seconds=arrivals, completedChargeCount=hist[0],
                            meanCompletedChargeSeconds=hist[1] / hist[0] if hist[0] else None, timeOfDaySeconds=(now / 1000) % 86400)
            validate_features(features)
            m = self.base(e, attempt, vehicle, "ObservationOpened")
            m.update(features=features, featureHash=digest(features))
            self.db.execute("INSERT INTO observations VALUES(?,?,?,?,?,?)", (m["observationId"], attempt, vehicle, profile["kind"], now, canonical(m)))
            deliver(self.db, m)
            self.db.execute("INSERT INTO queue VALUES(?,?,?,?)", (attempt, vehicle, park_id, e["sequence"]))
            self.db.execute("INSERT INTO arrivals VALUES(?,?,?)", (e["sequence"], park_id, now))
        elif typ == "VehicleStateObserved" and p["state"] == "Connecting":
            require(p["bayId"] in self.profile(vehicle)["compatibleBays"][p["parkId"]], "Incompatible reservation")
            require(self.db.execute("DELETE FROM queue WHERE vehicle=? AND park=?", (vehicle, p["parkId"])).rowcount == 1, "Reservation without queue")
            self.db.execute("INSERT INTO occupants VALUES(?,?,?)", (p["parkId"], p["bayId"], vehicle))
        elif typ == "ChargingStarted":
            attempt = p["chargingAttemptId"]
            a = self.db.execute("SELECT * FROM attempts WHERE id=?", (attempt,)).fetchone()
            require(a is not None and a["vehicle"] == vehicle and a["park"] == p["parkId"], "Start/attempt mismatch")
            require(self.db.execute("SELECT 1 FROM occupants WHERE park=? AND bay=? AND vehicle=?",
                                    (p["parkId"], p["bayId"], vehicle)).fetchone() is not None, "Charging without reservation")
            self.resolve(e, attempt, "OBSERVED", "charging-start-observed")
            self.db.execute("INSERT INTO sessions VALUES(?,?,?)", (p["id"], p["parkId"], now))
        elif typ == "ChargingCompleted":
            session = self.db.execute("SELECT * FROM sessions WHERE id=?", (p["id"],)).fetchone()
            require(session is not None, "Completion without start")
            self.db.execute("UPDATE history SET count=count+1,total_seconds=total_seconds+? WHERE park=?",
                            ((now - session["started"]) / 1000, session["park"]))
            self.db.execute("DELETE FROM sessions WHERE id=?", (p["id"],))
        elif typ == "VehicleDepartedPark":
            if p.get("bayId") is not None:
                require(self.db.execute("DELETE FROM occupants WHERE park=? AND bay=? AND vehicle=?",
                                       (p["parkId"], p["bayId"], vehicle)).rowcount == 1, "Departure occupancy mismatch")
        elif typ == "ChargingOutcomeObserved":
            attempt, status = p["decisionId"], p["status"]
            a = self.db.execute("SELECT * FROM attempts WHERE id=?", (attempt,)).fetchone()
            require(a is not None and a["vehicle"] == vehicle and a["outcome"] is None and p["outcomeTimeMs"] == now, "Invalid operational outcome")
            require(status in {"observed", "rejected-overflow", "censored-abandoned", "censored-stranded", "censored-end-of-run", "censored-run-failure"}, "Unknown operational status")
            o = self.db.execute("SELECT id FROM observations WHERE attempt=?", (attempt,)).fetchone()
            if status == "observed":
                require(o is not None, "Observed outcome without queue")
                r = self.db.execute("SELECT message FROM outcomes WHERE observation=?", (o[0],)).fetchone()
                require(r is not None and json.loads(r[0])["status"] == "OBSERVED"
                        and p["waitingSeconds"] == json.loads(r[0])["targetSeconds"], "Label without matching ChargingStarted")
            else:
                require(p["waitingSeconds"] is None, "Non-observed operational label must be null")
                if o and status in {"censored-abandoned", "censored-stranded"}:
                    self.resolve(e, attempt, "COMPETING_EVENT", status)
                    self.db.execute("DELETE FROM queue WHERE attempt=?", (attempt,))
                elif not o:
                    self.db.execute("INSERT INTO exclusions VALUES(?,?,?,?)", (attempt, vehicle, self.profile(vehicle)["kind"], status))
            self.db.execute("UPDATE attempts SET outcome=? WHERE id=?", (status, attempt))
        elif typ in ("SimulationCompleted", "SimulationFailed"):
            require(self.store.get("observerVersion") == "1.0", "Missing observation contract")
            require(self.db.execute("SELECT count(*) FROM attempts WHERE outcome IS NULL").fetchone()[0] == 0, "Unexplained operational attempts at terminal")
            require(p.get("status") == "completed" if typ == "SimulationCompleted" else p.get("status") in ("failed", "cancelled"), "Invalid terminal proof")
            self.finalize(e, typ == "SimulationCompleted")
