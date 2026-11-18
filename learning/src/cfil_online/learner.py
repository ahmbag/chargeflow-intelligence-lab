"""Persist prediction -> evaluate original -> update -> atomically checkpoint."""
import hashlib
import importlib.metadata
import json
import os
import platform
import time
from datetime import datetime,timezone
from pathlib import Path
import psutil
from cfil_stream.contracts import canonical, digest, require, validate_message
from cfil_stream.ingestion import atomic_json,shared_json
from . import VERSION
from .config import validate as validate_config
from .journal import Journal,record_bytes
from .store import Store
from .strategies import State,STRATEGIES


def utc(): return datetime.now(timezone.utc).isoformat()


def software():
    return dict(python=platform.python_version(),**{p:importlib.metadata.version(p) for p in ("river","numpy","scipy","narwhals")})


def source_code_hash():
    root=Path(__file__).parent.parent
    h=hashlib.sha256()
    for p in sorted(list((root/"cfil_online").glob("*.py"))+list((root/"cfil_stream").glob("*.py"))):
        h.update(p.parent.name.encode()+p.name.encode()+p.read_bytes())
    return h.hexdigest()


class Learner:
    def __init__(self,journal,database,experiment,config,crash_position=None,crash_phase=None):
        self.journal=None; self.store=None
        try:
            require(bool(experiment.strip()),"Explicit experiment ID required")
            validate_config(config)
            self.journal=Journal(journal)
            meta=self.journal.metadata(); require(meta["runId"] and meta["checkpoint"],"Consumer journal not initialized")
            stat=self.journal.path.stat()
            binding=dict(learnerVersion=VERSION,software=software(),config=config,codeHash=source_code_hash(),
                experimentId=experiment,sourcePath=str(self.journal.path),sourceIdentity=[stat.st_dev,stat.st_ino],
                readerVersion=meta["readerVersion"],runId=meta["runId"],scenario=meta["scenario"],scenarioHash=meta["scenarioHash"])
            self.store=Store(database,binding)
            saved=self.store.load()
            self.position,self.prefix,self.state=saved or (0,hashlib.sha256().hexdigest(),State(config))
            require(self.journal.prefix_hash(self.position)==self.prefix,"Consumed journal prefix changed")
            self.chain=hashlib.sha256()
            p=0
            while p<self.position:
                for record in self.journal.records(p,min(64,self.position-p)):
                    self.chain.update(record_bytes(record)); p=record["position"]
            self.crash_position,self.crash_phase=crash_position,crash_phase
            self.diagnostics=self.store.get("diagnostics",dict(maxPredictSeconds=0,maxUpdateSeconds=0,maxCheckpointSeconds=0,
                maxModelUpdateSeconds=0,peakRssBytes=0,maxJournalLag=0,maxSimulationLagMs=0,maxCheckpointBytes=0,statusPublishFailures=0))
            self.last_report=0; self.process=psutil.Process()
            if saved: self.store.put("restarts",self.store.get("restarts",0)+1)
            else:
                self.store.db.execute("BEGIN IMMEDIATE")
                self.store.save(0,self.prefix,self.state); self.store.db.execute("COMMIT")
        except BaseException:
            self.close(); raise

    def fault(self,position,phase):
        if position==self.crash_position and phase==self.crash_phase: os._exit(93)

    def watermark(self):
        source=self.journal.metadata()["binding"].get("path")
        if not source: return None
        try: return shared_json(Path(source).parent/"producer-progress.json")
        except (OSError,ValueError): return None

    def process_record(self,record):
        position=record["position"]; message=validate_message(record["message"])
        content_hash=digest(dict(message=message,park=record["park"]))
        db=self.store.db
        if position<=self.position:
            row=db.execute("SELECT hash FROM receipts WHERE position=?",(position,)).fetchone()
            require(row and row[0]==content_hash,"Conflicting delivered journal position")
            return False
        require(position==self.position+1,"Journal gap")
        chain=self.chain.copy(); chain.update(record_bytes(record))
        received=utc(); started=time.perf_counter()
        # Reload the authenticated committed object as a working copy. A failed
        # transaction can discard it without leaving mutated weights in self.state.
        work=self.store.load()[2]
        db.execute("BEGIN IMMEDIATE")
        try:
            previous=db.execute("SELECT hash FROM receipts WHERE message_id=? LIMIT 1",(message["messageId"],)).fetchone()
            if previous: require(previous[0]==content_hash,"Conflicting duplicate message identity")
            else:
                self.fault(position,"before-model")
                if message["messageType"]=="ObservationOpened":
                    prediction_start=time.perf_counter()
                    predictions=work.predict(message["features"],record["park"])
                    duration=time.perf_counter()-prediction_start
                    self.diagnostics["maxPredictSeconds"]=max(self.diagnostics["maxPredictSeconds"],duration)
                    completed=utc(); watermark=self.watermark()
                    db.execute("INSERT INTO observations VALUES(?,?,?)",(message["observationId"],canonical(message),record["park"]))
                    for strategy,prediction in predictions.items():
                        payload=dict(prediction,observationId=message["observationId"],experimentId=self.store.binding["experimentId"],
                            strategyId=strategy,featureHash=message["featureHash"],modelConfigVersion=work.config["modelConfigVersion"],
                            journalPosition=position,observationTimeMs=message["occurredAtSimTimeMs"],
                            availableAtSimTimeMs=message["availableAtSimTimeMs"],sourceCutoffSequence=message["sourceCutoffSequence"])
                        diagnostic=dict(receivedAtUtc=received,computedAtUtc=completed,inferenceSeconds=duration,
                                        producerWatermark=watermark,journalPublishedAtUtc=record["publishedAtUtc"])
                        db.execute("INSERT INTO predictions VALUES(?,?,?,?)",(message["observationId"],strategy,canonical(payload),canonical(diagnostic)))
                        work.fallbacks[strategy]+=int(prediction["fallbackReason"] is not None)
                        work.clipped[strategy]+=int(prediction["clipped"]); work.invalid[strategy]+=int(prediction["invalidModelValue"])
                    work.opened+=1
                else:
                    self.resolve(work,record)
                self.fault(position,"after-model")
            db.execute("INSERT INTO receipts VALUES(?,?,?)",(position,message["messageId"],content_hash))
            checkpoint_start=time.perf_counter()
            size=self.store.save(position,chain.hexdigest(),work)
            self.store.put("lastCheckpointUtc",utc())
            self.fault(position,"before-commit")
            db.execute("COMMIT")
            self.fault(position,"after-commit")
            self.diagnostics["maxCheckpointSeconds"]=max(self.diagnostics["maxCheckpointSeconds"],time.perf_counter()-checkpoint_start)
            self.diagnostics["maxCheckpointBytes"]=max(self.diagnostics["maxCheckpointBytes"],size)
            self.state=work; self.position=position; self.chain=chain; self.prefix=chain.hexdigest()
            self.diagnostics["maxUpdateSeconds"]=max(self.diagnostics["maxUpdateSeconds"],time.perf_counter()-started)
            return previous is None
        except BaseException as exc:
            if db.in_transaction: db.execute("ROLLBACK")
            # Includes a commit failure: never continue with the working model.
            self.position,self.prefix,self.state=self.store.load()
            self.store.put("lastError",dict(type=type(exc).__name__,message=str(exc),position=position,occurredAtUtc=utc()))
            raise

    def resolve(self,work,record):
        m=record["message"]; db=self.store.db
        row=db.execute("SELECT message,park FROM observations WHERE id=?",(m["observationId"],)).fetchone()
        require(row is not None,"Outcome before observation")
        opened=json.loads(row[0]); park=row[1]
        require(park==record["park"] and all(m[k]==opened[k] for k in ("runId","vehicleId","chargingAttemptId","targetVersion","featureSchemaVersion")),"Outcome identity mismatch")
        require(m["sourceCutoffSequence"]>=opened["sourceCutoffSequence"] and m["availableAtSimTimeMs"]>=opened["availableAtSimTimeMs"],"Label from future or invalid ordering")
        db.execute("INSERT INTO outcomes VALUES(?,?)",(m["observationId"],canonical(m)))
        work.outcomes[m["status"]]+=1
        if m["status"]!="OBSERVED": return
        y=m["targetSeconds"]
        require(y==(m["occurredAtSimTimeMs"]-opened["occurredAtSimTimeMs"])/1000,"Target does not match original queue entry")
        rows=db.execute("SELECT strategy,payload,diagnostic FROM predictions WHERE observation=?",(m["observationId"],)).fetchall()
        predictions={s:json.loads(p) for s,p,d in rows}
        require(set(predictions)==set(STRATEGIES),"Missing original prediction")
        kind=opened["features"]["vehicleKind"]
        # Never recompute predict_one here: intervening outcomes may already have
        # changed the model. All metrics and ADWIN see the original issued value.
        model_started=time.perf_counter()
        signal,alarm=work.evaluate(predictions,y,kind)
        model_seconds=time.perf_counter()-model_started
        band="zero" if y==0 else "0-300" if y<=300 else "300-1800" if y<=1800 else "1800+"
        for strategy,p in predictions.items():
            raw=p["rawPredictionSeconds"]
            db.execute("INSERT INTO errors VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?)",(m["observationId"],strategy,kind,park,
                int(p["coldStart"]),band,record["position"],m["availableAtSimTimeMs"],y,p["predictedWaitSeconds"]-y,
                raw-y if raw is not None else None,int(p["fallbackReason"] is not None),int(p["clipped"]),
                int(p["stateRevision"]>=work.config["freezeAfterLabels"])))
        diagnostic=json.loads(rows[0][2]); watermark=diagnostic["producerWatermark"]
        db.execute("INSERT INTO latency VALUES(?,?)",(m["observationId"],canonical(dict(
            outcomeAvailableAtSimTimeMs=m["availableAtSimTimeMs"],outcomeJournalPublishedAtUtc=record["publishedAtUtc"],
            computedAtUtc=diagnostic["computedAtUtc"],
            issuedBeforeOutcomeJournalPublication=diagnostic["computedAtUtc"]<=record["publishedAtUtc"],
            producerPastOutcomeAtPrediction=watermark.get("simulationTimeMs",watermark.get("timeMs",0))>m["availableAtSimTimeMs"] if watermark else None))))
        if alarm:
            db.execute("INSERT INTO alarms VALUES(?,?)",(record["position"],canonical(dict(observationId=m["observationId"],kind=kind,
                stateRevision=work.revision,journalPosition=record["position"],observationTimeMs=opened["occurredAtSimTimeMs"],
                outcomeTimeMs=m["availableAtSimTimeMs"],signal=signal,detectorConfig=work.config["drift"],action="observe-only"))))
        model_started=time.perf_counter()
        work.learn(opened["features"],park,y)
        self.diagnostics["maxModelUpdateSeconds"]=max(self.diagnostics["maxModelUpdateSeconds"],model_seconds+time.perf_counter()-model_started)

    def report(self):
        meta=self.journal.metadata(); mark=self.watermark()
        last=self.store.db.execute("SELECT message FROM outcomes ORDER BY rowid DESC LIMIT 1").fetchone()
        obs=self.store.db.execute("SELECT message FROM observations ORDER BY rowid DESC LIMIT 1").fetchone()
        time_ms=max([json.loads(r[0])["availableAtSimTimeMs"] for r in (last,obs) if r]+[0])
        lag=meta["lastPosition"]-self.position
        sim_lag=max(0,(mark.get("simulationTimeMs",mark.get("timeMs",0))-time_ms)) if mark and lag else 0
        self.diagnostics["peakRssBytes"]=max(self.diagnostics["peakRssBytes"],self.process.memory_info().rss)
        self.diagnostics["maxJournalLag"]=max(self.diagnostics["maxJournalLag"],lag)
        self.diagnostics["maxSimulationLagMs"]=max(self.diagnostics["maxSimulationLagMs"],sim_lag)
        result=dict(version=VERSION,experimentId=self.store.binding["experimentId"],journalPosition=self.position,
            journalPrefixHash=self.prefix,modelConfigVersion=self.state.config["modelConfigVersion"],
            journalLag=lag,simulationLagMs=sim_lag,terminal=meta["terminal"],sourceVerified=meta["verified"],
            checkpointUtc=self.store.get("lastCheckpointUtc"),restarts=self.store.get("restarts",0),
            lastError=self.store.get("lastError"),
            diagnostics=self.diagnostics,**self.state.summary())
        self.store.put("diagnostics",self.diagnostics)
        try: atomic_json(str(self.store.path)+".status.json",result)
        except OSError: self.diagnostics["statusPublishFailures"]+=1
        return result

    def run(self,live=False):
        while True:
            records=self.journal.records(self.position)
            for record in records: self.process_record(record)
            if time.monotonic()-self.last_report>1:
                self.report(); self.last_report=time.monotonic()
            if not records:
                meta=self.journal.metadata()
                # A consumer can commit its final messages between the empty
                # SELECT and this terminal read. Drain that newly visible tail
                # before declaring completion; idle EOF alone proves nothing.
                if meta["lastPosition"] > self.position:
                    continue
                if not live or meta["terminal"] and meta["verified"]:
                    require(meta["terminal"] and meta["verified"],"Replay requires verified terminal journal")
                    require(self.journal.prefix_hash(self.position)==self.prefix,"Journal changed during processing")
                    self.store.put("complete",True); self.store.put("sourceProof",meta)
                    return self.report()
                time.sleep(.05)

    def close(self):
        if self.store is not None: self.store.close(); self.store=None
        if self.journal is not None: self.journal.close(); self.journal=None
