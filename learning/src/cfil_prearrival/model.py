"""Shared River primitives, separate feature space and development-only artifacts."""
import base64
import hashlib
import hmac
import json
import math
import os
import pickle
import secrets
from pathlib import Path
from cfil_online.strategies import Regressor, Mean, Metric
from cfil_online.learner import software
from cfil_stream.contracts import canonical, require
from . import VERSION, FEATURE_VERSION

FEATURES = frozenset(("soc", "capacityKwh", "maxChargingKw", "estimatedTravelSeconds", "estimatedEnergyKwh",
    "estimatedArrivalSoc", "queueNow", "occupiedCompatibleNow", "compatibleBays", "blockedCompatibleNow",
    "availableGridKwNow", "knownIntentCount", "knownIntentEnergyKwh", "timeOfDaySeconds"))


def vector(features):
    require(isinstance(features, dict) and set(features) == FEATURES, "Unsupported pre-arrival features")
    require(all(type(x) in (int,float) and math.isfinite(x) and x >= 0 for x in features.values()), "Invalid pre-arrival values")
    require(features["soc"] <= 1 and features["estimatedArrivalSoc"] <= 1 and features["capacityKwh"] > 0
            and features["maxChargingKw"] > 0 and features["timeOfDaySeconds"] < 86400, "Invalid vehicle/time features")
    return {key:float(features[key]) for key in sorted(FEATURES)}


def code_hash():
    digest = hashlib.sha256()
    root = Path(__file__).parent.parent
    for package in ("cfil_online", "cfil_stream", "cfil_prearrival"):
        for path in sorted((root/package).glob("*.py")):
            digest.update(package.encode()+path.name.encode()+path.read_bytes())
    return digest.hexdigest()


class PreRegressor(Regressor):
    def predict(self, features):
        return float(self.model.predict_one(self.transformed(vector(features))))

    def learn(self, features, target):
        values = vector(features)
        self.scaler.learn_one(values)
        self.model.learn_one(self.transformed(values), target)
        self.updates += 1


class PreState:
    def __init__(self, config):
        require(config["featureVersion"] == FEATURE_VERSION and config["version"] == "prearrival-config-v1", "Unsupported model configuration")
        self.config = config
        self.regressor = PreRegressor(config["model"])
        self.global_mean = Mean()
        self.groups = {}
        self.development_horizons = {str(h):0 for h in config["horizonUpperSeconds"]}
        self.sources = []

    def horizon(self, features):
        return next((str(h) for h in self.config["horizonUpperSeconds"] if features["estimatedTravelSeconds"] <= h), "unsupported")

    def predict(self, candidate):
        features, park = candidate["features"], candidate["parkId"]
        baseline = candidate["deterministicWaitSeconds"]
        for mean in (self.groups.get(park), self.global_mean):
            if mean is not None and mean.count >= self.config["groupMinCount"]:
                baseline = mean.value
                break
        reason, raw = None, None
        try:
            vector(features)
            raw = self.regressor.predict(features)
            horizon = self.horizon(features)
            if self.regressor.updates < self.config["warmupLabels"]: reason = "cold-start"
            elif self.development_horizons.get(horizon,0) < self.config["minimumDevelopmentLabelsPerHorizon"]: reason = "unsupported-arrival-horizon"
            elif not math.isfinite(raw): reason = "nonfinite-model-value"
        except (ValueError, TypeError, KeyError): reason = "unsupported-features"
        return dict(parkId=park, waitSeconds=max(0,raw) if reason is None else baseline, baselineSeconds=baseline,
            rawSeconds=raw if raw is not None and math.isfinite(raw) else None, method="river" if reason is None else "historical-or-structural",
            fallbackReason=reason, featureVersion=FEATURE_VERSION, modelVersion=self.config["modelVersion"],
            revision=self.regressor.updates, supported=reason is None)

    def learn(self, features, park, target, update_model, development):
        vector(features)
        require(type(target) in (int,float) and math.isfinite(target) and target >= 0, "Invalid observed wait")
        self.global_mean.update(target)
        if park not in self.groups:
            require(len(self.groups)<100,"Park group bound exceeded")
            self.groups[park] = Mean()
        self.groups[park].update(target)
        if update_model: self.regressor.learn(features,target)
        if development:
            horizon=self.horizon(features)
            if horizon in self.development_horizons: self.development_horizons[horizon]+=1


def save_model(path, state, source):
    """Explicit local development export, distinct from a recoverable run store."""
    path=Path(path)
    require(not path.exists() and not Path(str(path)+".key").exists(),"Refuse model overwrite")
    require(source["development"] is True,"Only development states may initialize comparison arms")
    state=pickle.loads(pickle.dumps(state,protocol=5))
    state.sources.append(source)
    blob=pickle.dumps(state,protocol=5)
    require(len(blob)<=4*1024*1024,"Model artifact exceeds bound")
    manifest=dict(version=VERSION,featureVersion=FEATURE_VERSION,software=software(),codeHash=code_hash(),
                  config=state.config,sources=state.sources,stateHash=hashlib.sha256(blob).hexdigest())
    key=secrets.token_bytes(32)
    signature=hmac.new(key,canonical(manifest).encode()+b"\0"+blob,hashlib.sha256).hexdigest()
    path.parent.mkdir(parents=True,exist_ok=True)
    Path(str(path)+".key").write_bytes(key)
    if os.name!="nt": Path(str(path)+".key").chmod(0o600)
    path.write_text(json.dumps(dict(manifest=manifest,state=base64.b64encode(blob).decode(),signature=signature),indent=2)+"\n",encoding="utf-8")


def load_model(path, config):
    path=Path(path)
    require(path.stat().st_size<6*1024*1024,"Oversized initial model")
    item=json.loads(path.read_text(encoding="utf-8")); manifest=item["manifest"]
    blob=base64.b64decode(item["state"],validate=True)
    key=Path(str(path)+".key").read_bytes()
    require(len(key)==32 and len(blob)<=4*1024*1024,"Invalid local model/key")
    require(hmac.compare_digest(item["signature"],hmac.new(key,canonical(manifest).encode()+b"\0"+blob,hashlib.sha256).hexdigest()),"Initial model authentication failed")
    require(manifest["version"]==VERSION and manifest["featureVersion"]==FEATURE_VERSION and manifest["codeHash"]==code_hash()
            and manifest["software"]==software() and manifest["config"]==config,"Incompatible initial model")
    require(manifest["sources"] and all(s["development"] is True for s in manifest["sources"]),"Initial model lacks development provenance")
    require(hashlib.sha256(blob).hexdigest()==manifest["stateHash"],"Initial model digest mismatch")
    # As in CFIL-03, authenticate BEFORE unpickling. The trusted boundary includes
    # both the self-produced development artifact and its locally protected key.
    return pickle.loads(blob),manifest
