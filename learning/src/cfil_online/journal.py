"""Versioned read-only adapter over the CFIL-02 journal, not a second projector.

Park identity is absent from the v1 feature message. This adapter exposes it as
evaluation/grouping metadata from the already observed charging attempt. It never
adds it to the regression vector or exposes future simulation configuration.
"""
import hashlib
import json
import sqlite3
from pathlib import Path
from cfil_stream.contracts import canonical, require, validate_message

READER_VERSION = "1.0"


class Journal:
    def __init__(self, path):
        self.path = Path(path).resolve()
        self.db = sqlite3.connect(self.path.as_uri() + "?mode=ro", uri=True, isolation_level=None, timeout=10)
        self.db.execute("PRAGMA query_only=ON")
        self.db.execute("PRAGMA cache_size=-4096")

    def metadata(self):
        values = {k: json.loads(v) for k, v in self.db.execute("SELECT key,value FROM meta")}
        binding = values.get("binding", {})
        return dict(readerVersion=READER_VERSION, runId=values.get("runId"), scenario=values.get("scenario"),
                    scenarioHash=values.get("scenarioHash"), binding=binding,
                    checkpoint=values.get("checkpoint"), terminal=values.get("terminal"),
                    verified=values.get("verified", False),
                    lastPosition=self.db.execute("SELECT coalesce(max(position),0) FROM journal").fetchone()[0])

    def records(self, after=0, limit=64):
        require(1 <= limit <= 64, "Journal batch exceeds bound")
        # A short cursor avoids keeping a WAL read transaction alive while the
        # consumer appends. Only 64 bounded contract messages enter RAM at once.
        rows = self.db.execute("SELECT position,message,processed_at_utc FROM journal WHERE position>? ORDER BY position LIMIT ?",
                               (after, limit)).fetchall()
        result = []
        for position, raw, processed in rows:
            message = validate_message(json.loads(raw))
            park = self.db.execute("SELECT park FROM attempts WHERE id=?", (message["chargingAttemptId"],)).fetchone()
            require(park is not None, "Journal attempt metadata missing")
            result.append(dict(position=position, message=message, park=park[0], publishedAtUtc=processed))
        return result

    def prefix_hash(self, position):
        checksum = hashlib.sha256()
        count = 0
        while count < position:
            records = self.records(count, min(64, position-count))
            require(records, "Journal truncated")
            for record in records:
                require(record["position"] == count+1, "Journal sequence gap")
                checksum.update(record_bytes(record)); count += 1
        return checksum.hexdigest()

    def exclusions(self):
        return [dict(kind=k, park=p, reason=r, count=n) for k,p,r,n in self.db.execute(
            "SELECT e.kind,a.park,e.reason,count(*) FROM exclusions e JOIN attempts a ON a.id=e.attempt GROUP BY e.kind,a.park,e.reason")]

    def close(self):
        self.db.close()


def record_bytes(record):
    return (canonical({k: record[k] for k in ("position", "message", "park")})+"\n").encode()
