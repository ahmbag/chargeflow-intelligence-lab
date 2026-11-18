"""Causal decision barrier over the existing CFIL-02 consumer and learning journal."""
import hashlib
import json
import math
import os
from pathlib import Path
import pickle
import time
import psutil
from cfil_online.store import Store
from cfil_online.learner import software
from cfil_stream.ingestion import Consumer, atomic_json
from cfil_stream.contracts import canonical, digest, require, validate_message
from .model import PreState, vector, code_hash, load_model, save_model
from . import VERSION, FEATURE_VERSION

DDL = """
CREATE TABLE IF NOT EXISTS pa_batches(id TEXT PRIMARY KEY,hash TEXT NOT NULL,request TEXT NOT NULL,answer TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS pa_decisions(attempt TEXT PRIMARY KEY,decision_id TEXT UNIQUE NOT NULL,vehicle TEXT NOT NULL,
 park TEXT NOT NULL,payload TEXT NOT NULL,hash TEXT NOT NULL,status TEXT NOT NULL,queue_ms INTEGER,outcome TEXT);
CREATE TABLE IF NOT EXISTS pa_errors(attempt TEXT PRIMARY KEY,target REAL NOT NULL,prediction REAL NOT NULL,
 baseline REAL NOT NULL,raw_prediction REAL,horizon TEXT NOT NULL,method TEXT NOT NULL,revision INTEGER NOT NULL,position INTEGER NOT NULL);
CREATE TRIGGER IF NOT EXISTS immutable_pa_features BEFORE UPDATE OF payload,hash ON pa_decisions
BEGIN SELECT RAISE(ABORT,'Pre-arrival snapshots are immutable'); END;
"""


class PreArrivalRuntime:
    def __init__(self, source, directory, experiment, policy, development, config, initial_model=None,
                 crash_position=None, crash_phase=None):
        self.consumer=None; self.store=None
        try:
            require(policy in ("P1","P2","P3"),"Unsupported forecasting arm")
            require(policy not in ("P2","P3") or initial_model,"ML arms require development initialization")
            self.directory=Path(directory).resolve(); self.directory.mkdir(parents=True,exist_ok=True)
            self.consumer=Consumer(source,self.directory/"consumer.sqlite",experiment)
            self.policy,self.development,self.config=policy,development,config
            stat=Path(source).stat()
            binding=dict(version=VERSION,featureVersion=FEATURE_VERSION,codeHash=code_hash(),software=software(),
                source=str(Path(source).resolve()),sourceIdentity=[stat.st_dev,stat.st_ino],experiment=experiment,
                policy=policy,development=development,config=config,
                initialModelHash=hashlib.sha256(Path(initial_model).read_bytes()).hexdigest() if initial_model else None)
            self.store=Store(self.directory/"prearrival.sqlite",binding)
            self.store.db.executescript(DDL)
            saved=self.store.load()
            self.position,self.prefix,self.state=saved or (0,hashlib.sha256().hexdigest(),None)
            if saved is None:
                self.state=load_model(initial_model,config)[0] if initial_model else PreState(config)
                self.store.db.execute("BEGIN IMMEDIATE")
                self.store.save(0,self.prefix,self.state)
                self.store.put("initialRevision",self.state.regressor.updates)
                self.store.db.execute("COMMIT")
            self.chain=hashlib.sha256()
            for position,raw in self.consumer.store.db.execute("SELECT position,message FROM journal WHERE position<=? ORDER BY position",(self.position,)):
                self.chain.update((canonical(validate_message(json.loads(raw)))+"\n").encode())
            require(self.chain.hexdigest()==self.prefix,"Changed pre-arrival journal prefix")
            require(self.consumer.store.db.execute("SELECT coalesce(max(position),0) FROM journal").fetchone()[0]>=self.position,"Journal truncated")
            self.crash_position,self.crash_phase=crash_position,crash_phase
            self.diagnostics=self.store.get("diagnostics",dict(maxSyncSeconds=0,maxForecastSeconds=0,maxUpdateSeconds=0,
                maxCheckpointBytes=0,peakRssBytes=0,maxSourceLag=0,restarts=0))
            if saved: self.diagnostics["restarts"]+=1
        except BaseException:
            self.close(); raise

    def fault(self,position,phase):
        if position==self.crash_position and phase==self.crash_phase: os._exit(94)

    def synchronize(self,sequence,time_ms):
        started=time.perf_counter()
        cp=self.consumer.store.get("checkpoint")
        require(type(sequence) is int and type(time_ms) is int and sequence>=cp["sequence"] and time_ms>=cp["timeMs"],"Stale/future causal barrier")
        self.diagnostics["maxSourceLag"]=max(self.diagnostics["maxSourceLag"],sequence-cp["sequence"])
        while cp["sequence"]<sequence:
            batch=self.consumer.read_batch()
            accepted=[]
            for raw in batch:
                event=json.loads(raw)
                if event["sequence"]>sequence: break
                require(event["simulationTimeMs"]<=time_ms,"Future source event at barrier")
                accepted.append(raw)
            require(accepted,"Source prefix not flushed through decision barrier")
            # Never consume a future event even when an entire replay file is
            # already available. Rewind any bytes beyond the requested prefix.
            self.consumer.file.seek(cp["offset"]+sum(map(len,accepted)))
            self.consumer.process_batch(accepted)
            cp=self.consumer.store.get("checkpoint")
        require(cp["sequence"]==sequence,"Consumer exceeded information cutoff")
        while True:
            rows=self.consumer.store.db.execute("SELECT position,message FROM journal WHERE position>? ORDER BY position LIMIT 64",(self.position,)).fetchall()
            if not rows: break
            for position,raw in rows:
                message=validate_message(json.loads(raw))
                require(message["sourceCutoffSequence"]<=sequence,"Future learning message")
                self.process(position,message)
        self.resolve_exclusions()
        self.consumer.report()
        self.store.put("sourceSequence",sequence)
        self.store.put("simulationTimeMs",time_ms)
        self.diagnostics["maxSyncSeconds"]=max(self.diagnostics["maxSyncSeconds"],time.perf_counter()-started)
        self.diagnostics["peakRssBytes"]=max(self.diagnostics["peakRssBytes"],psutil.Process().memory_info().rss)
        self.store.put("diagnostics",self.diagnostics)
        return dict(sourceSequence=sequence,revision=self.state.regressor.updates,journalPosition=self.position)

    def process(self,position,message):
        require(position==self.position+1,"Pre-arrival journal gap")
        db=self.store.db
        work=self.store.load()[2]
        chain=self.chain.copy(); chain.update((canonical(message)+"\n").encode())
        started=time.perf_counter(); db.execute("BEGIN IMMEDIATE")
        try:
            self.fault(position,"before-model")
            row=db.execute("SELECT payload,status,queue_ms FROM pa_decisions WHERE attempt=?",(message["chargingAttemptId"],)).fetchone()
            if row is not None:
                decision=json.loads(row[0]); context=decision["context"]
                require(context["sourceSequence"]<=message["sourceCutoffSequence"] and context["timeMs"]<=message["availableAtSimTimeMs"],"Noncausal pre-arrival outcome")
                require(context["vehicleId"]==message["vehicleId"] and context["runId"]==message["runId"],"Pre-arrival identity mismatch")
                if message["messageType"]=="ObservationOpened":
                    require(row[1]=="PENDING" and row[2] is None,"Duplicate or replaced queue reference")
                    db.execute("UPDATE pa_decisions SET queue_ms=? WHERE attempt=?",(message["occurredAtSimTimeMs"],message["chargingAttemptId"]))
                else:
                    require(row[1]=="PENDING" and row[2] is not None,"Outcome without unique realized decision")
                    if message["status"]=="OBSERVED":
                        y=message["targetSeconds"]
                        require(y==(message["occurredAtSimTimeMs"]-row[2])/1000,"Target includes approach instead of queue wait")
                        prediction=decision["forecast"]
                        # Evaluate the ORIGINAL pre-arrival prediction. Alternative
                        # parks and intermediate reconsiderations never get labels.
                        db.execute("INSERT INTO pa_errors VALUES(?,?,?,?,?,?,?,?,?)",(message["chargingAttemptId"],y,
                            prediction["waitSeconds"],prediction["baselineSeconds"],prediction["rawSeconds"],
                            work.horizon(decision["candidate"]["features"]),prediction["method"],prediction["revision"],position))
                        work.learn(decision["candidate"]["features"],decision["candidate"]["parkId"],y,
                            update_model=self.development or self.policy=="P3",development=self.development)
                        self.after_label(work, decision, message, position)
                    db.execute("UPDATE pa_decisions SET status=?,outcome=? WHERE attempt=?",(message["status"],canonical(message),message["chargingAttemptId"]))
            self.fault(position,"after-model")
            size=self.store.save(position,chain.hexdigest(),work)
            self.fault(position,"before-commit")
            db.execute("COMMIT")
            self.fault(position,"after-commit")
            self.state,self.position,self.prefix,self.chain=work,position,chain.hexdigest(),chain
            self.diagnostics["maxCheckpointBytes"]=max(self.diagnostics["maxCheckpointBytes"],size)
            self.diagnostics["maxUpdateSeconds"]=max(self.diagnostics["maxUpdateSeconds"],time.perf_counter()-started)
        except BaseException:
            if db.in_transaction: db.execute("ROLLBACK")
            self.position,self.prefix,self.state=self.store.load()
            raise

    def resolve_exclusions(self):
        for attempt,payload in self.store.db.execute("SELECT attempt,payload FROM pa_decisions WHERE status='PENDING' AND queue_ms IS NULL").fetchall():
            row=self.consumer.store.db.execute("SELECT outcome FROM attempts WHERE id=?",(attempt,)).fetchone()
            if row and row[0] is not None:
                status="INCOMPLETE_RUN" if row[0]=="censored-run-failure" else "NOT_REALIZED"
                self.store.db.execute("UPDATE pa_decisions SET status=?,outcome=? WHERE attempt=?",(status,canonical(dict(reason=row[0])),attempt))

    def forecast(self,context,candidates):
        started=time.perf_counter()
        require(len(candidates)<=100 and len({c["parkId"] for c in candidates})==len(candidates),"Unbounded/duplicate candidates")
        request=dict(context=context,candidates=candidates); request_hash=digest(request)
        prior=self.store.db.execute("SELECT hash,answer FROM pa_batches WHERE id=?",(context["decisionId"],)).fetchone()
        if prior:
            require(prior[0]==request_hash,"Conflicting forecast retry")
            return json.loads(prior[1])
        self.synchronize(context["sourceSequence"],context["timeMs"])
        require(context["runId"]==self.consumer.store.get("runId"),"Wrong source run")
        before=pickle.dumps(self.state,protocol=5)
        forecasts=[dict(self.state.predict(c),sourceSequence=context["sourceSequence"]) for c in candidates]
        require(before==pickle.dumps(self.state,protocol=5),"Candidate inference mutated model/scaler")
        answer=dict(decisionId=context["decisionId"],revision=self.state.regressor.updates,sourceSequence=context["sourceSequence"],
            stateHash=hashlib.sha256(before).hexdigest(),forecasts=forecasts)
        self.store.db.execute("BEGIN IMMEDIATE")
        try:
            self.store.db.execute("INSERT INTO pa_batches VALUES(?,?,?,?)",(context["decisionId"],request_hash,canonical(request),canonical(answer)))
            self.forecast_evidence(context, candidates, answer)
            self.store.db.execute("COMMIT")
        except BaseException:
            self.store.db.execute("ROLLBACK")
            raise
        self.diagnostics["maxForecastSeconds"]=max(self.diagnostics["maxForecastSeconds"],time.perf_counter()-started)
        return answer

    def after_label(self, work, decision, message, position):
        """Extension hook inside the same checkpoint/outcome transaction."""

    def forecast_evidence(self, context, candidates, answer):
        """Extension hook atomically persists same-barrier paired predictions."""

    def commit(self,decision):
        context,candidate,prediction=decision["context"],decision["candidate"],decision["forecast"]
        vector(candidate["features"])
        require(candidate["parkId"]==prediction["parkId"] and prediction["featureVersion"]==FEATURE_VERSION
            and prediction["sourceSequence"]<=context["sourceSequence"],"Invalid realized forecast")
        require(all(type(n) in (int,float) and math.isfinite(n) and n>=0 for n in
            (prediction["waitSeconds"],prediction["baselineSeconds"],decision["issuedWaitSeconds"])),"Invalid realized prediction")
        fingerprint=digest(decision); db=self.store.db
        previous=db.execute("SELECT hash FROM pa_decisions WHERE attempt=?",(decision["attemptId"],)).fetchone()
        if previous:
            require(previous[0]==fingerprint,"Conflicting realized decision retry")
            return dict(committed=True,duplicate=True)
        require(context["timeMs"]>=self.store.get("simulationTimeMs",0),"Late commit would reorder learning")
        batch=db.execute("SELECT request,answer FROM pa_batches WHERE id=?",(context["decisionId"],)).fetchone()
        if prediction["revision"] >= 0:
            require(batch is not None,"Realized model prediction has no inference evidence")
            request,answer=map(json.loads,batch)
            require(context==request["context"] and candidate in request["candidates"] and prediction in answer["forecasts"],
                    "Realized snapshot differs from original inference")
        else:
            require(prediction["method"]=="structural" and not prediction["supported"],"Invalid unavailable-provider fallback")
        db.execute("BEGIN IMMEDIATE")
        try:
            if context["previousParkId"] is not None:
                db.execute("UPDATE pa_decisions SET status='REPLACED' WHERE vehicle=? AND status='PENDING' AND queue_ms IS NULL",(context["vehicleId"],))
            db.execute("INSERT INTO pa_decisions VALUES(?,?,?,?,?,?,'PENDING',NULL,NULL)",(decision["attemptId"],context["decisionId"],
                context["vehicleId"],candidate["parkId"],canonical(decision),fingerprint))
            db.execute("COMMIT")
        except BaseException:
            db.execute("ROLLBACK"); raise
        return dict(committed=True,duplicate=False)

    def report(self):
        db=self.store.db
        def metrics(where="1=1",params=()):
            rows=db.execute("SELECT target,prediction,baseline,raw_prediction FROM pa_errors WHERE "+where,params).fetchall()
            def summary(index):
                errors=sorted(abs(r[index]-r[0]) for r in rows if r[index] is not None)
                return dict(count=len(errors),maeSeconds=sum(errors)/len(errors) if errors else None,
                    p90Seconds=errors[math.ceil(.9*len(errors))-1] if errors else None,
                    rmseSeconds=math.sqrt(sum(e*e for e in errors)/len(errors)) if errors else None)
            return dict(status="EVALUABLE" if rows else "N/A",model=summary(1),baseline=summary(2),rawModel=summary(3))
        result=dict(version=VERSION,featureVersion=FEATURE_VERSION,binding=self.store.binding,
            revision=self.state.regressor.updates,initialRevision=self.store.get("initialRevision"),
            onlineUpdates=self.state.regressor.updates-self.store.get("initialRevision"),journalPosition=self.position,
            sourceSequence=self.store.get("sourceSequence"),sourceVerified=self.consumer.store.get("verified",False),
            coverage=dict(db.execute("SELECT status,count(*) FROM pa_decisions GROUP BY status")),
            candidateForecasts=db.execute("SELECT coalesce(sum(json_array_length(answer,'$.forecasts')),0) FROM pa_batches").fetchone()[0],
            realizedDecisions=db.execute("SELECT count(*) FROM pa_decisions").fetchone()[0],
            metrics=metrics(),byHorizon={str(h):metrics("horizon=?",(str(h),)) for h in [*self.config["horizonUpperSeconds"],"unsupported"]},
            byMethod={m:metrics("method=?",(m,)) for (m,) in db.execute("SELECT DISTINCT method FROM pa_errors")},
            supportedDevelopmentHorizons=self.state.development_horizons,diagnostics=self.diagnostics,
            modelStateHash=hashlib.sha256(pickle.dumps(self.state,protocol=5)).hexdigest())
        export=hashlib.sha256()
        # Unused replies can exist after a lost pipe. They are audited separately;
        # only realized decisions and outcomes define recovery business equality.
        for table,order in (("pa_decisions","attempt"),("pa_errors","position")):
            for row in db.execute(f"SELECT * FROM {table} ORDER BY {order}"):
                export.update((canonical(list(row))+"\n").encode())
        export.update(result["modelStateHash"].encode())
        result["businessExportHash"]=export.hexdigest()
        audit=hashlib.sha256()
        for row in db.execute("SELECT * FROM pa_batches ORDER BY id"):
            audit.update((canonical(list(row))+"\n").encode())
        result["forecastAuditHash"]=audit.hexdigest()
        return result

    def complete(self,sequence,time_ms):
        self.synchronize(sequence,time_ms)
        require(self.consumer.store.get("terminal"),"No producer terminal proof")
        self.consumer.run(live=False)
        self.resolve_exclusions()
        require(self.store.db.execute("SELECT count(*) FROM pa_decisions WHERE status='PENDING'").fetchone()[0]==0,"Unresolved pre-arrival decisions")
        result=self.report(); atomic_json(self.directory/"prearrival-report.json",result)
        if self.development:
            target=self.directory/"development.model.json"
            if not target.exists():
                save_model(target,self.state,dict(development=True,experiment=self.store.binding["experiment"],
                    runId=self.consumer.store.get("runId"),sourcePrefixHash=self.consumer.hash.hexdigest(),
                    observed=result["coverage"].get("OBSERVED",0),reportHash=digest(result)))
        return result

    def close(self):
        if self.store is not None: self.store.close(); self.store=None
        if self.consumer is not None: self.consumer.close(); self.consumer=None
