"""Strict, versioned boundaries between producer, projector and future learner."""
import hashlib
import json
import math

TARGET = "QUEUE_TO_CHARGE_START_SECONDS"
TARGET_VERSION = "1.0"
FEATURE_VERSION = "1.0"
STATUSES = ("PENDING", "OBSERVED", "RIGHT_CENSORED", "COMPETING_EVENT", "INCOMPLETE_RUN")
FEATURES = {"vehicleKind", "soc", "batteryCapacityKwh", "maxGridChargingKw", "vehiclesAhead",
            "occupiedCompatibleBays", "compatibleBayCount", "blockedCompatibleBays", "availableGridKw",
            "arrivalsLast900Seconds", "completedChargeCount", "meanCompletedChargeSeconds", "timeOfDaySeconds"}
EVENT_TYPES = {"SimulationStarted", "ObservationContractPublished", "ObservableVehicleRegistered",
               "ChargingParkCapacityChanged", "VehicleEnteredHighway", "VehicleStranded",
               "VehicleReachedDestination", "ChargingDecisionMade", "QueueAbandoned", "VehicleStateObserved",
               "ChargingParkApproached", "QueueEntered", "ChargingStarted", "ChargingOutcomeObserved",
               "VehicleDepartedPark", "ChargingPowerChanged", "ChargingCompleted", "SimulationCompleted", "SimulationFailed"}


class IntegrityError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise IntegrityError(message)


def canonical(value):
    return json.dumps(value, sort_keys=True, ensure_ascii=False, separators=(",", ":"), allow_nan=False)


def digest(value):
    return hashlib.sha256(canonical(value).encode("utf-8")).hexdigest()


def decode(raw):
    def finite_float(text):
        value = float(text)
        require(math.isfinite(value), "Nonfinite JSON number")
        return value
    def pairs(items):
        result = {}
        for key, value in items:
            require(key not in result, "Duplicate JSON property")
            result[key] = value
        return result
    try:
        return json.loads(raw, object_pairs_hook=pairs, parse_float=finite_float,
                          parse_constant=lambda _: require(False, "Nonfinite JSON number"))
    except (ValueError, UnicodeError) as exc:
        raise IntegrityError(f"Invalid complete JSON record: {exc}") from exc


def number(value, minimum=0):
    return type(value) in (int, float) and math.isfinite(value) and value >= minimum


def envelope(e):
    require(isinstance(e, dict) and set(e) == {"schemaVersion", "runId", "sequence", "eventId", "simulationTimeMs",
            "eventType", "entityId", "causationId", "payload"}, "Invalid envelope fields")
    require(e["schemaVersion"] == "1.0", "Unsupported source envelope version")
    require(type(e["sequence"]) is int and e["sequence"] > 0 and type(e["simulationTimeMs"]) is int
            and e["simulationTimeMs"] >= 0, "Invalid sequence/time units (integer milliseconds required)")
    require(all(isinstance(e[k], str) and e[k] for k in ("runId", "entityId", "eventId")), "Invalid identity")
    require(e["eventId"] == f'{e["runId"]}:{e["sequence"]:09d}', "Noncanonical event identity")
    require(e["eventType"] in EVENT_TYPES and isinstance(e["payload"], dict), "Unsupported event type/payload")
    require(e["causationId"] is None or isinstance(e["causationId"], str), "Invalid causation")
    return e


def validate_features(f):
    require(set(f) == FEATURES, "Unexpected feature (possible simulator knowledge/leakage)")
    require(f["vehicleKind"] in ("Car", "Truck"), "Unknown vehicle kind")
    require(f["soc"] is None or number(f["soc"]) and f["soc"] <= 1, "Invalid SoC")
    for key in FEATURES - {"vehicleKind", "soc", "meanCompletedChargeSeconds"}:
        require(number(f[key]), f"Invalid feature {key}")
    require(f["meanCompletedChargeSeconds"] is None or number(f["meanCompletedChargeSeconds"]), "Invalid historical mean")
    for key in ("vehiclesAhead", "occupiedCompatibleBays", "compatibleBayCount", "blockedCompatibleBays",
                "arrivalsLast900Seconds", "completedChargeCount"):
        require(type(f[key]) is int, "Count must be integer")
    require((f["completedChargeCount"] == 0) == (f["meanCompletedChargeSeconds"] is None), "Missing history is null, not zero")
    require(f["batteryCapacityKwh"] > 0 and f["maxGridChargingKw"] > 0 and f["timeOfDaySeconds"] < 86400, "Feature bounds")


COMMON = {"schemaVersion", "messageId", "messageType", "runId", "observationId", "chargingAttemptId", "vehicleId",
          "target", "targetVersion", "featureSchemaVersion", "occurredAtSimTimeMs", "availableAtSimTimeMs", "sourceCutoffSequence"}


def validate_message(m):
    require(isinstance(m, dict) and m.get("messageType") in ("ObservationOpened", "OutcomeResolved"), "Unknown learning message")
    opened = m["messageType"] == "ObservationOpened"
    expected = COMMON | ({"features", "featureHash"} if opened else {"status", "targetSeconds", "lowerBoundSeconds", "reason"})
    require(set(m) == expected, "Unexpected learning fields")
    require(m["schemaVersion"] == "1.0" and m["targetVersion"] == TARGET_VERSION
            and m["featureSchemaVersion"] == FEATURE_VERSION and m["target"] == TARGET, "Unsupported learning version/target")
    require(type(m["sourceCutoffSequence"]) is int and m["sourceCutoffSequence"] > 0, "Invalid cutoff")
    require(type(m["occurredAtSimTimeMs"]) is int and m["occurredAtSimTimeMs"] >= 0
            and m["availableAtSimTimeMs"] == m["occurredAtSimTimeMs"], "Observer v1 has immediate availability")
    for key in ("messageId", "runId", "observationId", "chargingAttemptId", "vehicleId"):
        require(isinstance(m[key], str) and m[key], "Missing learning identity")
    require(m["observationId"] == m["chargingAttemptId"] + ":queue-v1", "Observation/attempt mismatch")
    require(m["messageId"] == m["observationId"] + (":opened" if opened else ":resolved"), "Unstable message identity")
    if opened:
        validate_features(m["features"])
        require(digest(m["features"]) == m["featureHash"], "Feature hash mismatch")
    else:
        require(m["status"] in STATUSES[1:], "Outcome cannot be pending")
        require(isinstance(m["reason"], str) and m["reason"], "Missing outcome reason")
        if m["status"] == "OBSERVED":
            require(number(m["targetSeconds"]) and m["lowerBoundSeconds"] is None, "Invalid observed target")
        else:
            require(m["targetSeconds"] is None and number(m["lowerBoundSeconds"]), "Non-observed outcome must never have a target")
    return m
