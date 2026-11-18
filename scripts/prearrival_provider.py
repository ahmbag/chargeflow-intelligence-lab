"""JSON-lines process adapter; no TraCI imports or simulation control capability."""
import json
from pathlib import Path
import sys
import time
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/"learning/src"))
from cfil_prearrival.runtime import PreArrivalRuntime


def main():
    runtime=None
    try:
        for line in sys.stdin:
            try:
                request=json.loads(line); operation=request["operation"]
                if operation=="initialize":
                    if runtime is not None: raise ValueError("Already initialized")
                    config=json.loads((ROOT/"learning/config/prearrival-v1.json").read_text())
                    if request.get("learningStrategy"):
                        from cfil_multiday.runtime import MultiDayRuntime
                        runtime=MultiDayRuntime(request["source"],request["directory"],request["experiment"],request["policy"],
                            request["development"],config,request.get("initialModel"), strategy=request["learningStrategy"])
                    else:
                        runtime=PreArrivalRuntime(request["source"],request["directory"],request["experiment"],request["policy"],
                            request["development"],config,request.get("initialModel"))
                    result=dict(ready=True)
                elif runtime is None: raise ValueError("Provider not initialized")
                elif operation=="test-hang":
                    time.sleep(request["seconds"])
                    result=dict(testOnly=True)
                elif operation=="sync": result=runtime.synchronize(request["sourceSequence"],request["timeMs"])
                elif operation=="forecast": result=runtime.forecast(request["context"],request["candidates"])
                elif operation=="commit": result=runtime.commit(request["decision"])
                elif operation=="complete": result=runtime.complete(request["sourceSequence"],request["timeMs"])
                else: raise ValueError("Unknown pre-arrival operation")
                print(json.dumps(dict(ok=True,result=result),allow_nan=False,separators=(",", ":")),flush=True)
            except Exception as error:
                print(json.dumps(dict(ok=False,error=f"{type(error).__name__}: {error}")),flush=True)
    finally:
        if runtime is not None: runtime.close()


if __name__=="__main__": main()
