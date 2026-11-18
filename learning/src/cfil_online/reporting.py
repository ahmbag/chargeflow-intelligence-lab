"""Exact offline summaries read persistent errors, never retain full history in RAM."""
import hashlib
import json
import math
from pathlib import Path
from cfil_stream.contracts import canonical,require
from .strategies import STRATEGIES


def aggregate(db,where="1=1",params=()):
    n,absolute,squared,signed,raw_n,raw_absolute,fallback,clipped=db.execute(
        "SELECT count(*),sum(abs(error)),sum(error*error),sum(error),count(raw_error),sum(abs(raw_error)),sum(fallback),sum(clipped) FROM errors WHERE "+where,params).fetchone()
    p90=None
    if n:
        # Nearest-rank P90 is exact and disk-backed; no unbounded error array.
        p90=db.execute("SELECT abs(error) FROM errors WHERE "+where+" ORDER BY abs(error) LIMIT 1 OFFSET ?",(*params,math.ceil(.9*n)-1)).fetchone()[0]
    return dict(count=n,status="EVALUABLE" if n else "N/A",maeSeconds=absolute/n if n else None,
        maeMinutes=absolute/n/60 if n else None,rmseSeconds=math.sqrt(squared/n) if n else None,
        biasSeconds=signed/n if n else None,p90AbsoluteErrorSeconds=p90,
        rawModelCount=raw_n,rawMaeSeconds=raw_absolute/raw_n if raw_n else None,
        fallbackRate=fallback/n if n else None,clippingRate=clipped/n if n else None)


def report(learner):
    db=learner.store.db
    result=dict(schemaVersion="1.0",unit="seconds",context=learner.store.binding,source=learner.store.get("sourceProof"),
                status=learner.report(),strategies={},exclusions=learner.journal.exclusions(),
                caveat="Regression describes observed charge starts only; censored/competing cases are not success probabilities.")
    parks=[r[0] for r in db.execute("SELECT DISTINCT park FROM observations ORDER BY park")]
    for strategy in STRATEGIES:
        base="strategy=?"; params=(strategy,)
        result["strategies"][strategy]=dict(overall=aggregate(db,base,params),
            byKind={k:aggregate(db,base+" AND kind=?",(*params,k)) for k in ("Car","Truck")},
            byPark={p:aggregate(db,base+" AND park=?",(*params,p)) for p in parks},
            byKindAndPark={k:{p:aggregate(db,base+" AND kind=? AND park=?",(*params,k,p)) for p in parks} for k in ("Car","Truck")},
            byColdStart={str(bool(c)):aggregate(db,base+" AND cold=?",(*params,c)) for c in (0,1)},
            byWaitBand={b:aggregate(db,base+" AND wait_band=?",(*params,b)) for b in ("zero","0-300","300-1800","1800+")},
            afterFreeze=aggregate(db,base+" AND after_freeze=1",params),
            beforeRegimeChange=aggregate(db,base+" AND time_ms<21600000",params),
            afterRegimeChange=aggregate(db,base+" AND time_ms>=21600000 AND after_freeze=1",params))
    coverage={k:dict(opened=0,observed=0,positiveWait=0,statuses={}) for k in ("Car","Truck")}
    for raw,outcome in db.execute("SELECT o.message,r.message FROM observations o LEFT JOIN outcomes r ON r.observation=o.id"):
        opened=json.loads(raw); resolved=json.loads(outcome) if outcome else dict(status="PENDING")
        group=coverage[opened["features"]["vehicleKind"]]; group["opened"]+=1
        state=resolved["status"]; group["statuses"][state]=group["statuses"].get(state,0)+1
        if state=="OBSERVED":
            group["observed"]+=1; group["positiveWait"]+=int(resolved["targetSeconds"]>0)
    result["coverage"]=coverage
    latency=dict(evaluated=0,issuedBeforeOutcomeJournalPublication=0,producerPastOutcomeAtPrediction=0,missingWatermark=0)
    for (raw,) in db.execute("SELECT payload FROM latency"):
        value=json.loads(raw); latency["evaluated"]+=1
        latency["issuedBeforeOutcomeJournalPublication"]+=int(value["issuedBeforeOutcomeJournalPublication"])
        latency["producerPastOutcomeAtPrediction"]+=int(value["producerPastOutcomeAtPrediction"] is True)
        latency["missingWatermark"]+=int(value["producerPastOutcomeAtPrediction"] is None)
    latency["interpretation"]="Journal-publication timing and sampled producer watermark; not an operational decision deadline guarantee. Replay is retrospective."
    result["timeliness"]=latency
    result["alarms"]=[json.loads(r[0]) for r in db.execute("SELECT payload FROM alarms ORDER BY position")]
    return result


def export(learner,path):
    require(learner.store.get("complete"),"Export requires completed verified journal")
    db=learner.store.db; checksum=hashlib.sha256()
    with open(path,"wb") as output:
        def write(value):
            raw=(canonical(value)+"\n").encode(); output.write(raw); checksum.update(raw)
        # Physical source paths, process times and signatures differ across a fresh
        # replay and are intentionally absent. Business records and state remain.
        write(dict(type="Context",config=learner.state.config,software=learner.store.binding["software"],
                   scenario=learner.store.binding["scenario"],runId=learner.store.binding["runId"]))
        for row in db.execute("SELECT payload FROM predictions ORDER BY json_extract(payload,'$.journalPosition'),strategy"):
            write(dict(type="Prediction",**json.loads(row[0])))
        for row in db.execute("SELECT observation,strategy,kind,park,cold,wait_band,position,time_ms,target,error,raw_error,fallback,clipped,after_freeze FROM errors ORDER BY position,strategy"):
            write(dict(type="Evaluation",values=list(row)))
        for row in db.execute("SELECT message FROM outcomes ORDER BY observation"):
            write(dict(type="Outcome",message=json.loads(row[0])))
        for row in db.execute("SELECT payload FROM alarms ORDER BY position"):
            write(dict(type="Alarm",**json.loads(row[0])))
        blob=db.execute("SELECT state FROM checkpoint WHERE id=1").fetchone()[0]
        write(dict(type="State",position=learner.position,journalPrefixHash=learner.prefix,
            stateSha256=hashlib.sha256(blob).hexdigest(),summary=learner.state.summary()))
    return checksum.hexdigest()
