"""Bounded binary JSONL tailing and atomic delivery; EOF is not a live completion proof."""
import hashlib
import json
import os
from pathlib import Path
import sqlite3
import time
from datetime import datetime, timezone
import psutil
from . import VERSION
from .contracts import IntegrityError, canonical, decode, digest, envelope, require
from .persistence import Store
from .projection import Projector

MAX_RECORD = 1024 * 1024
MAX_BATCH_BYTES = 4 * 1024 * 1024
BATCH_SIZE = 256


def shared_json(path):
    if os.name != "nt":
        return decode(Path(path).read_bytes())
    # Python's ordinary Windows open does not grant FILE_SHARE_DELETE. Atomic sidecar
    # replacement needs that flag in addition to read/write sharing, on BOTH sides.
    import ctypes
    import msvcrt
    from ctypes import wintypes
    create = ctypes.WinDLL("kernel32", use_last_error=True).CreateFileW
    create.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
                       wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    create.restype = wintypes.HANDLE
    handle = create(str(Path(path).resolve()), 0x80000000, 7, None, 3, 0x80, None)
    if handle == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    fd = msvcrt.open_osfhandle(handle, os.O_RDONLY | os.O_BINARY)
    with os.fdopen(fd, "rb") as stream:
        return decode(stream.read(MAX_RECORD + 1))


def atomic_json(path, value):
    path = Path(path)
    temp = path.with_suffix(path.suffix + ".tmp")
    temp.write_text(json.dumps(value, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    for attempt in range(10):
        try:
            os.replace(temp, path)
            return
        except PermissionError:
            if attempt == 9:
                raise
            time.sleep(.01)


class Consumer:
    def __init__(self, source, database, experiment, batch_size=BATCH_SIZE, crash_at=None, crash_phase="before-commit"):
        try:
            self._initialize(source, database, experiment, batch_size, crash_at, crash_phase)
        except BaseException:
            self.close()
            raise

    def _initialize(self, source, database, experiment, batch_size, crash_at, crash_phase):
        require(isinstance(experiment, str) and experiment.strip(), "An explicit experiment context is required")
        require(1 <= batch_size <= BATCH_SIZE, "Batch size exceeds bounded configuration")
        self.source, self.database = Path(source).resolve(), Path(database).resolve()
        self.database.parent.mkdir(parents=True, exist_ok=True)
        self.lock = open(str(self.database) + ".lock", "a+b")
        self.lock.seek(0)
        if os.name == "nt":
            import msvcrt
            if self.lock.read(1) == b"":
                self.lock.write(b"0"); self.lock.flush()
            self.lock.seek(0)
            msvcrt.locking(self.lock.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(self.lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        self.store = Store(database)
        self.file = open(self.source, "rb", buffering=65536)
        self.identity = self.file_identity()
        self.batch_size, self.crash_at, self.crash_phase = batch_size, crash_at, crash_phase
        self.start = time.monotonic()
        self.process = psutil.Process()
        self.source_size = self.source.stat().st_size
        self.hash = hashlib.sha256()
        self.last_report = 0
        self.milestone = None
        code_hash = hashlib.sha256()
        for module in sorted(Path(__file__).parent.glob("*.py")):
            code_hash.update(module.name.encode()); code_hash.update(module.read_bytes())
        expected = dict(path=str(self.source), fileIdentity=self.identity, projectionVersion=VERSION,
                        projectionSourceHash=code_hash.hexdigest(), experiment=experiment)
        previous = self.store.get("binding")
        if previous is None:
            self.store.db.execute("BEGIN IMMEDIATE")
            self.store.put("binding", expected)
            self.store.put("checkpoint", dict(sequence=0, timeMs=0, offset=0, prefixHash=self.hash.hexdigest()))
            self.store.db.execute("COMMIT")
        else:
            require(previous == expected, "Source identity, projection version or experiment differs from checkpoint")
            self.store.increment("restarts")
        cp = self.store.get("checkpoint")
        require(self.source_size >= cp["offset"], "Source truncated before checkpoint")
        remaining = cp["offset"]
        # Restart verifies every consumed byte with bounded memory. A same-name file, altered
        # prefix or different physical source cannot silently inherit another experiment's state.
        while remaining:
            block = self.file.read(min(131072, remaining))
            require(bool(block), "Truncated checkpoint prefix")
            self.hash.update(block); remaining -= len(block)
        require(self.hash.hexdigest() == cp["prefixHash"], "Source prefix changed since checkpoint")
        if self.store.get("inflight"):
            self.store.increment("repeatedBatches")
        self.store.put("inflight", False)

    def file_identity(self):
        info = os.fstat(self.file.fileno())
        return [info.st_dev, info.st_ino]

    def verify_source(self):
        info = self.source.stat()
        require([info.st_dev, info.st_ino] == self.identity, "Source replaced")
        require(info.st_size >= self.source_size, "Source shortened")
        self.source_size = info.st_size

    def read_batch(self):
        self.verify_source()
        records, size = [], 0
        while len(records) < self.batch_size and size < MAX_BATCH_BYTES:
            offset = self.file.tell()
            line = self.file.readline(MAX_RECORD + 1)
            require(len(line) <= MAX_RECORD, "Record exceeds maximum bytes")
            if not line:
                break
            if not line.endswith(b"\n"):
                self.file.seek(offset)  # A partial UTF-8 character is safe: decode only after newline.
                break
            if size + len(line) > MAX_BATCH_BYTES and records:
                self.file.seek(offset)
                break
            records.append(line); size += len(line)
        return records

    def process_batch(self, records):
        if not records:
            return
        db, store = self.store.db, self.store
        store.put("inflight", True)
        old_cp = store.get("checkpoint")
        new_hash = self.hash.copy()
        cp = dict(old_cp)
        projector = Projector(store)
        try:
            db.execute("BEGIN IMMEDIATE")
            for raw in records:
                e = envelope(decode(raw))
                h = digest(e)
                run = store.get("runId")
                if run is None:
                    require(e["sequence"] == 1 and e["eventType"] == "SimulationStarted", "Missing stream beginning")
                    store.put("runId", e["runId"])
                else:
                    require(run == e["runId"], "Mixed source streams")
                receipt = db.execute("SELECT hash FROM receipts WHERE sequence=?", (e["sequence"],)).fetchone()
                if receipt:
                    require(receipt[0] == h, "Conflicting duplicate event")
                    store.increment("duplicates")
                else:
                    require(not store.get("terminal"), "Event after terminal proof")
                    require(e["sequence"] == cp["sequence"] + 1, "Source sequence gap or reordering")
                    require(e["simulationTimeMs"] >= cp["timeMs"], "Source time moved backwards")
                    require(e["sequence"] != 2 or e["eventType"] == "ObservationContractPublished", "Legacy stream has no observer v1 contract")
                    if e["causationId"] is not None:
                        last = db.execute("SELECT event_id FROM entities WHERE id=?", (e["entityId"],)).fetchone()
                        require(last is not None and last[0] == e["causationId"], "Invalid source causation")
                    projector.apply(e)
                    db.execute("INSERT INTO receipts VALUES(?,?)", (e["sequence"], h))
                    db.execute("INSERT INTO entities VALUES(?,?) ON CONFLICT(id) DO UPDATE SET event_id=excluded.event_id", (e["entityId"], e["eventId"]))
                    cp.update(sequence=e["sequence"], timeMs=e["simulationTimeMs"])
                cp["offset"] += len(raw)
                new_hash.update(raw)
            cp["prefixHash"] = new_hash.hexdigest()
            store.put("checkpoint", cp)
            store.put("processedAtUtc", datetime.now(timezone.utc).isoformat())
            store.increment("readEvents", len(records))
            store.increment("committedBatches")
            store.put("maxBatchRecords", max(store.get("maxBatchRecords", 0), len(records)))
            store.put("maxBatchBytes", max(store.get("maxBatchBytes", 0), sum(map(len, records))))
            store.put("inflight", False)
            if self.crash_at and old_cp["sequence"] < self.crash_at <= cp["sequence"] and self.crash_phase == "before-commit":
                os._exit(91)  # Explicit fault-injection option used only by the recovery tests.
            db.execute("COMMIT")
            self.hash = new_hash
            if self.crash_at and old_cp["sequence"] < self.crash_at <= cp["sequence"] and self.crash_phase == "after-commit":
                os._exit(92)
        except BaseException:
            if db.in_transaction:
                db.execute("ROLLBACK")
            self.file.seek(old_cp["offset"])
            raise  # No RAM projection survives: all mutable business state is transactional SQL.

    def report(self):
        from .diagnostics import status
        store = self.store
        cp = store.get("checkpoint")
        store.put("peakRssBytes", max(store.get("peakRssBytes", 0), self.process.memory_info().rss))
        progress_path = self.source.parent / "producer-progress.json"
        if progress_path.exists():
            progress = shared_json(progress_path)
            store.put("producerProgress", progress)
            store.put("maxSequenceLag", max(store.get("maxSequenceLag", 0), max(0, progress["sequence"] - cp["sequence"])))
            store.put("maxSimulationLagMs", max(store.get("maxSimulationLagMs", 0), max(0, progress["simulationTimeMs"] - cp["timeMs"])))
            if progress["sequence"] > cp["sequence"] and self.milestone is None:
                self.milestone = progress
            if self.milestone and cp["sequence"] >= self.milestone["sequence"]:
                latency = max(0, (datetime.now(timezone.utc) - datetime.fromisoformat(self.milestone["flushedAtUtc"])).total_seconds())
                store.put("maxObservedWatermarkLatencySeconds", max(store.get("maxObservedWatermarkLatencySeconds", 0), latency))
                self.milestone = None
        store.put("sessionWallSeconds", time.monotonic() - self.start)
        try:
            atomic_json(str(self.database) + ".status.json", status(store))
        except OSError as exc:
            # A dashboard/editor may open this diagnostic sidecar without delete sharing.
            # Never turn that reader's lock into an integrity failure or stop ingestion;
            # the authoritative status remains queryable in SQLite until the next publish.
            store.increment("statusPublishFailures")
            store.put("lastStatusPublishError", str(exc))
        self.last_report = time.monotonic()

    def verify_prefix(self):
        self.verify_source()
        checksum = hashlib.sha256()
        with open(self.source, "rb") as source:
            remaining = self.store.get("checkpoint")["offset"]
            while remaining:
                block = source.read(min(131072, remaining))
                require(bool(block), "Truncated source during final verification")
                checksum.update(block); remaining -= len(block)
        require(checksum.hexdigest() == self.hash.hexdigest(), "Source prefix modified during processing")

    def run(self, live=False, producer_pid=None, aborted=False, delay=0):
        producer_missing = False
        try:
            producer = psutil.Process(producer_pid) if producer_pid else None
        except psutil.NoSuchProcess:
            producer, producer_missing = None, True
        try:
            while True:
                records = self.read_batch()
                self.process_batch(records)
                if time.monotonic() - self.last_report >= .5:
                    self.report()
                if records:
                    if delay:
                        time.sleep(delay)
                    continue
                cp = self.store.get("checkpoint")
                try:
                    dead = producer_missing or producer is not None and (not producer.is_running() or producer.status() == psutil.STATUS_ZOMBIE)
                except psutil.NoSuchProcess:
                    # The process may disappear AFTER is_running() returned True.
                    # Treat this race as the same positive abort evidence as an
                    # already missing PID, so pending labels become INCOMPLETE_RUN.
                    dead = True
                terminal = self.store.get("terminal")
                if terminal or not live or dead:
                    partial = self.source.stat().st_size > cp["offset"]
                    require(not partial or dead or aborted, "Incomplete final line without producer-abort evidence")
                    if not terminal:
                        require(dead or aborted, "No terminal proof; EOF/timeout does not finish a run")
                        require(cp["sequence"] > 0 and self.store.get("observerVersion") == "1.0", "Producer failed before observable contract")
                        self.store.db.execute("BEGIN IMMEDIATE")
                        try:
                            Projector(self.store).finalize(dict(runId=self.store.get("runId"), simulationTimeMs=cp["timeMs"], sequence=cp["sequence"]), False)
                            self.store.put("producerAbortEvidence", {"kind": "process-exited" if dead else "operator-confirmed", "partialTailBytes": self.source.stat().st_size - cp["offset"]})
                            self.store.db.execute("COMMIT")
                        except BaseException:
                            self.store.db.execute("ROLLBACK")
                            raise
                    self.verify_prefix()
                    self.store.put("verified", True)
                    self.report()
                    return
                time.sleep(.05)  # Idle EOF remains open even if a simulation is paused for hours.
        except (IntegrityError, KeyError, TypeError, sqlite3.Error, OSError) as exc:
            self.store.put("verified", False)
            self.store.increment("integrityErrors")
            if "gap" in str(exc):
                self.store.increment("sequenceGaps")
            self.store.put("lastError", str(exc))
            self.report()
            raise

    def close(self):
        for name in ("file", "store", "lock"):
            value = getattr(self, name, None)
            if value is not None:
                value.close()
                setattr(self, name, None)

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()
