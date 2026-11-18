"""RecordingLearner is a test sink, NOT a forecasting model or training loop."""
import json
from datetime import datetime, timezone
from .contracts import canonical, digest, require, validate_message


class RecordingLearner:
    def __init__(self, db):
        self.db = db

    def accept(self, message):
        m = validate_message(message)
        previous = self.db.execute("SELECT hash FROM recording WHERE message_id=?", (m["messageId"],)).fetchone()
        if previous:
            require(previous[0] == digest(m), "Conflicting learning duplicate")
            return False
        if m["messageType"] == "ObservationOpened":
            self.db.execute("INSERT INTO recorded_observations VALUES(?,?)", (m["observationId"], canonical(m)))
        else:
            row = self.db.execute("SELECT message FROM recorded_observations WHERE id=?", (m["observationId"],)).fetchone()
            require(row is not None, "Learner received outcome before observation")
            opened = json.loads(row[0])
            require(m["sourceCutoffSequence"] >= opened["sourceCutoffSequence"]
                    and m["availableAtSimTimeMs"] >= opened["availableAtSimTimeMs"], "Future label released early")
            require(all(m[k] == opened[k] for k in ("runId", "vehicleId", "chargingAttemptId", "targetVersion", "featureSchemaVersion")),
                    "Outcome identity mismatch")
            require(digest(opened["features"]) == opened["featureHash"], "Learner features mutated")
            if m["status"] == "OBSERVED":
                require(m["targetSeconds"] == (m["occurredAtSimTimeMs"] - opened["occurredAtSimTimeMs"]) / 1000,
                        "Target is not queue-to-charge-start duration")
            self.db.execute("INSERT INTO recorded_outcomes VALUES(?,?)", (m["observationId"], canonical(m)))
        self.db.execute("INSERT INTO recording VALUES(?,?)", (m["messageId"], digest(m)))
        return True


def deliver(db, message):
    # The local recording effect and outbox message share the projection transaction. A real
    # external learner will need its own checkpoint protocol in CFIL-03; this is not that promise.
    validate_message(message)
    db.execute("INSERT INTO journal(message_id,message,processed_at_utc) VALUES(?,?,?)",
               (message["messageId"], canonical(message), datetime.now(timezone.utc).isoformat()))
    RecordingLearner(db).accept(message)


def read_journal(db, after_position=0):
    """Streaming pull interface: journal order, never observation-ID or outcome-time sorting."""
    for row in db.execute("SELECT position,message FROM journal WHERE position>? ORDER BY position", (after_position,)):
        yield row[0], validate_message(json.loads(row[1]))
