"""Explicit update boundaries avoid River Pipeline's implicit inference learning."""
import copy
import math
from collections import deque
from river import drift, linear_model, optim, preprocessing
from cfil_stream.contracts import FEATURES, require, validate_features

STRATEGIES = ("zero", "global", "grouped", "river", "frozen")


def vector(features):
    validate_features(features)
    result = {k: float(features[k] if features[k] is not None else 0) for k in sorted(FEATURES-{"vehicleKind"})}
    for key in ("soc", "meanCompletedChargeSeconds"):
        result[key+"Missing"] = float(features[key] is None)
    # Fixed vocabulary: no future full-run category fit, no stateful encoder.
    # Unknown categories are rejected by the shared v1 contract, never mapped to Car.
    result["isTruck"] = float(features["vehicleKind"] == "Truck")
    return result


class Regressor:
    def __init__(self, config):
        self.scaler = preprocessing.StandardScaler()
        self.model = linear_model.LinearRegression(optimizer=optim.SGD(config["learningRate"]),
            intercept_lr=config["interceptLearningRate"], l2=config["l2"], clip_gradient=config["gradientClip"])
        self.updates = 0

    def transformed(self, x):
        # StandardScaler.transform_one indexes defaultdicts and can insert absent
        # keys. get() implements the identical formula without mutating a cold scaler.
        return {k: (v-self.scaler.means.get(k,0))/math.sqrt(self.scaler.vars.get(k,0))
                if self.scaler.vars.get(k,0) else 0.0 for k,v in x.items()}

    def predict(self, features):
        return float(self.model.predict_one(self.transformed(vector(features))))

    def learn(self, features, target):
        x = vector(features)
        # This executes only after evaluation. Each observed label updates both
        # scaler and regressor once; pending/censored samples update neither.
        self.scaler.learn_one(x)
        self.model.learn_one(self.transformed(x), target)
        self.updates += 1


class Mean:
    def __init__(self): self.count, self.value = 0, 0.0
    def update(self, y):
        self.count += 1; self.value += (y-self.value)/self.count


class Metric:
    def __init__(self, window):
        self.count, self.absolute, self.squared, self.signed = 0, 0.0, 0.0, 0.0
        self.window = deque(maxlen=window)
    def update(self, error):
        self.count += 1; self.absolute += abs(error); self.squared += error*error; self.signed += error
        self.window.append(abs(error))
    def report(self):
        n=self.count
        return dict(count=n,maeSeconds=self.absolute/n if n else None,maeMinutes=self.absolute/n/60 if n else None,
                    rmseSeconds=math.sqrt(self.squared/n) if n else None,biasSeconds=self.signed/n if n else None,
                    rollingMaeSeconds=sum(self.window)/len(self.window) if self.window else None)


class State:
    def __init__(self, config):
        self.config=config
        self.regressor=Regressor(config["model"])
        self.frozen=None
        self.global_mean=Mean(); self.kind_means={k:Mean() for k in ("Car","Truck")}; self.groups={}
        self.metrics={s:Metric(config["rollingWindow"]) for s in STRATEGIES}
        self.kind_metrics={s:{k:Metric(config["rollingWindow"]) for k in ("Car","Truck")} for s in STRATEGIES}
        self.detector=drift.ADWIN(**config["drift"]["adwin"])
        self.revision=0; self.learned={"Car":0,"Truck":0}; self.opened=0
        self.outcomes={s:0 for s in ("OBSERVED","RIGHT_CENSORED","COMPETING_EVENT","INCOMPLETE_RUN")}
        self.fallbacks={s:0 for s in STRATEGIES}; self.clipped={s:0 for s in STRATEGIES}
        self.invalid={s:0 for s in STRATEGIES}; self.alarms=0
        self.freeze_revision=None

    def baseline(self, park, kind):
        group=self.groups.get((park,kind))
        for name,mean in (("park-kind",group),("kind",self.kind_means[kind]),("global",self.global_mean)):
            if mean is not None and mean.count >= self.config["groupMinCount"]:
                return mean.value, name
        return self.config["startSeconds"], "start"

    def predict(self, features, park):
        vector(features)
        kind=features["vehicleKind"]; fallback,level=self.baseline(park,kind)
        predictions={}
        for strategy in STRATEGIES:
            revision=self.revision
            raw=None; reason=None
            if strategy == "zero": raw=0.0; method="constant"
            elif strategy == "global":
                raw=self.global_mean.value if self.global_mean.count else self.config["startSeconds"]
                method="historical-global" if self.global_mean.count else "cold-start"
            elif strategy == "grouped": raw=fallback; method="historical-"+level
            else:
                model=self.frozen if strategy=="frozen" and self.frozen is not None else self.regressor
                revision=model.updates
                raw=model.predict(features)
                if not math.isfinite(raw): reason="nonfinite"
                elif model.updates < self.config["warmupLabels"]: reason="cold-start"
                method="baseline-fallback" if reason else ("frozen-model" if strategy=="frozen" and self.frozen is not None else "online-model")
            issued=fallback if reason else raw
            clipped=issued < 0
            predictions[strategy]=dict(rawPredictionSeconds=raw if math.isfinite(raw) else None,
                predictedWaitSeconds=max(0.0,issued),predictionMethod=method,fallbackReason=reason,
                fallbackLevel=level if reason else None,clipped=clipped,invalidModelValue=reason=="nonfinite",
                stateRevision=revision,learnedObservationCount=revision,coldStart=revision < self.config["warmupLabels"])
        return predictions

    def evaluate(self, predictions, y, kind):
        for strategy,prediction in predictions.items():
            error=prediction["predictedWaitSeconds"]-y
            self.metrics[strategy].update(error); self.kind_metrics[strategy][kind].update(error)
        signal=min(abs(predictions["river"]["predictedWaitSeconds"]-y)/self.config["drift"]["scaleSeconds"],1.0)
        self.detector.update(signal)
        alarm=bool(self.detector.drift_detected)
        self.alarms += int(alarm)
        return signal,alarm

    def learn(self, features, park, y):
        kind=features["vehicleKind"]
        self.regressor.learn(features,y)
        self.global_mean.update(y); self.kind_means[kind].update(y)
        if (park,kind) not in self.groups:
            require(len(self.groups)<512,"Baseline group bound exceeded")
            self.groups[park,kind]=Mean()
        self.groups[park,kind].update(y)
        self.learned[kind]+=1; self.revision+=1
        if self.frozen is None and self.revision==self.config["freezeAfterLabels"]:
            # deepcopy retains learned parameters, unlike estimator.clone(). The
            # twin has exactly the same scaler/weights at this comparison boundary.
            self.frozen=copy.deepcopy(self.regressor); self.freeze_revision=self.revision

    def summary(self):
        return dict(stateRevision=self.revision,learnedByKind=self.learned,opened=self.opened,outcomes=self.outcomes,
                    pending=self.opened-sum(self.outcomes.values()),metrics={s:m.report() for s,m in self.metrics.items()},
                    byKind={s:{k:m.report() for k,m in kinds.items()} for s,kinds in self.kind_metrics.items()},
                    fallbacks=self.fallbacks,clipped=self.clipped,invalid=self.invalid,driftAlarms=self.alarms,
                    freezeRevision=self.freeze_revision,scalerUpdates=self.regressor.updates,
                    detector=dict(width=float(self.detector.width),estimation=float(self.detector.estimation),
                                  variance=float(self.detector.variance),detections=int(self.detector.n_detections)))
