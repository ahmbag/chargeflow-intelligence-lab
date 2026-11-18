"""Diagnostic wall time is deliberately excluded from canonical business exports."""
import hashlib
import json
from . import VERSION
from .contracts import STATUSES, canonical, require


def counts(store):
    db = store.db
    result = {kind: dict(opened=0, statuses={s: 0 for s in STATUSES}, excluded={}, excludedTotal=0) for kind in ("Car", "Truck")}
    for row in db.execute("SELECT o.kind,r.message FROM observations o LEFT JOIN outcomes r ON r.observation=o.id"):
        group = result[row[0]]
        group["opened"] += 1
        group["statuses"][json.loads(row[1])["status"] if row[1] else "PENDING"] += 1
    for row in db.execute("SELECT kind,reason,count(*) FROM exclusions GROUP BY kind,reason"):
        result[row[0]]["excluded"][row[1]] = row[2]
        result[row[0]]["excludedTotal"] += row[2]
    for group in result.values():
        require(group["opened"] == sum(group["statuses"].values()), "Observation balance mismatch")
    return dict(scenario=store.get("scenario"), byKind=result,
                attempts=db.execute("SELECT count(*) FROM attempts").fetchone()[0],
                learningMessages=db.execute("SELECT count(*) FROM journal").fetchone()[0])


def status(store):
    cp = store.get("checkpoint", {})
    producer = store.get("producerProgress")
    return dict(schemaVersion="1.0", projectionVersion=VERSION, experiment=store.get("binding", {}).get("experiment"),
                runId=store.get("runId"), terminal=store.get("terminal"), verified=store.get("verified", False), checkpoint=cp,
                processedEvents=cp.get("sequence", 0), readEvents=store.get("readEvents", 0),
                producerSequence=producer["sequence"] if producer else None,
                sequenceLag=max(0, producer["sequence"] - cp.get("sequence", 0)) if producer else None,
                simulationLagMs=max(0, producer["simulationTimeMs"] - cp.get("timeMs", 0)) if producer else None,
                maxSequenceLag=store.get("maxSequenceLag", 0), maxSimulationLagMs=store.get("maxSimulationLagMs", 0),
                maxObservedWatermarkLatencySeconds=store.get("maxObservedWatermarkLatencySeconds", 0),
                peakRssBytes=store.get("peakRssBytes", 0), maxBatchRecords=store.get("maxBatchRecords", 0),
                maxBatchBytes=store.get("maxBatchBytes", 0), inMemoryQueueCapacity=0,
                duplicates=store.get("duplicates", 0), sequenceGaps=store.get("sequenceGaps", 0),
                integrityErrors=store.get("integrityErrors", 0), lastError=store.get("lastError"),
                statusPublishFailures=store.get("statusPublishFailures", 0), lastStatusPublishError=store.get("lastStatusPublishError"),
                restarts=store.get("restarts", 0), repeatedBatches=store.get("repeatedBatches", 0),
                committedBatches=store.get("committedBatches", 0), processedAtUtc=store.get("processedAtUtc"),
                sessionWallSeconds=store.get("sessionWallSeconds", 0), observations=counts(store))


def export(store, path):
    require(store.get("verified") and store.get("terminal"), "Export requires verified terminal source")
    checksum = hashlib.sha256()
    with open(path, "wb") as output:
        def write(value):
            line = (canonical(value) + "\n").encode("utf-8")
            output.write(line); checksum.update(line)
        write(dict(type="Context", runId=store.get("runId"), projectionVersion=VERSION,
                   scenario=store.get("scenario"), scenarioHash=store.get("scenarioHash"), terminal=store.get("terminal")))
        for row in store.db.execute("SELECT position,message FROM journal ORDER BY position"):
            write(dict(type="LearningMessage", position=row[0], message=json.loads(row[1])))
        for row in store.db.execute("SELECT attempt,vehicle,kind,reason FROM exclusions ORDER BY attempt"):
            write(dict(type="ExcludedAttempt", **dict(row)))
        write(dict(type="Counters", **counts(store)))
    return checksum.hexdigest()
