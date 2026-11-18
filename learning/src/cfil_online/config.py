"""Reject ambiguous or nonfinite learning rules before opening persistent state."""
import math
from cfil_stream.contracts import require


def validate(config):
    expected={"schemaVersion","modelConfigVersion","featureVersion","targetVersion","initialization","seed",
              "groupMinCount","startSeconds","warmupLabels","freezeAfterLabels","rollingWindow","model","drift"}
    require(set(config)==expected,"Unexpected learner configuration fields")
    require(config["schemaVersion"]==config["featureVersion"]==config["targetVersion"]=="1.0"
            and config["initialization"]=="cold-start-per-run","Unsupported configuration version or initialization")
    require(isinstance(config["modelConfigVersion"],str) and config["modelConfigVersion"],"Missing model version")
    for key in ("groupMinCount","warmupLabels","freezeAfterLabels","rollingWindow"):
        require(type(config[key]) is int and 1<=config[key]<=10000,"Invalid bounded setting: "+key)
    require(type(config["seed"]) is int and config["seed"]>=0,"Invalid seed")
    require(type(config["startSeconds"]) in (int,float) and math.isfinite(config["startSeconds"]) and config["startSeconds"]>=0,"Invalid cold start")
    require(set(config["model"])=={"learningRate","interceptLearningRate","l2","gradientClip"},"Unexpected model parameter")
    for key,value in config["model"].items():
        require(type(value) in (int,float) and math.isfinite(value) and (value>=0 if key=="l2" else value>0),"Invalid model parameter")
    drift=config["drift"]
    require(set(drift)=={"scaleSeconds","adwin"} and type(drift["scaleSeconds"]) in (int,float)
            and math.isfinite(drift["scaleSeconds"]) and drift["scaleSeconds"]>0,"Invalid drift scaling")
    adwin=drift["adwin"]
    require(set(adwin)=={"delta","clock","max_buckets","min_window_length","grace_period"},"Unexpected ADWIN parameter")
    require(type(adwin["delta"]) in (int,float) and 0<adwin["delta"]<1,"Invalid ADWIN delta")
    for key in set(adwin)-{"delta"}:
        require(type(adwin[key]) is int and 1<=adwin[key]<=10000,"Invalid bounded ADWIN setting")
    return config
