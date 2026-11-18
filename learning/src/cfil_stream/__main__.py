import argparse
import json
import sys
from .contracts import IntegrityError
from .diagnostics import export, status
from .ingestion import Consumer
from .persistence import Store


def main():
    parser = argparse.ArgumentParser(description="CFIL-02 local shadow consumer; no model training")
    sub = parser.add_subparsers(dest="command", required=True)
    for name in ("live", "replay"):
        p = sub.add_parser(name)
        p.add_argument("source")
        p.add_argument("--database", required=True)
        p.add_argument("--experiment", required=True)
        p.add_argument("--producer-pid", type=int)
        p.add_argument("--producer-aborted", action="store_true", help="Explicit operator confirmation of a terminated producer; never a timeout")
        p.add_argument("--batch-size", type=int, default=256)
        p.add_argument("--delay-seconds", type=float, default=0, help="Intentional slow-consumer test")
        p.add_argument("--crash-at", type=int, help="TEST ONLY: kill this process at source sequence")
        p.add_argument("--crash-phase", choices=("before-commit", "after-commit"), default="before-commit")
    for name in ("status", "export"):
        p = sub.add_parser(name)
        p.add_argument("database")
        if name == "export":
            p.add_argument("output")
    args = parser.parse_args()
    try:
        if args.command in ("live", "replay"):
            if args.delay_seconds < 0:
                parser.error("delay must be nonnegative")
            with Consumer(args.source, args.database, args.experiment, args.batch_size, args.crash_at, args.crash_phase) as consumer:
                consumer.run(live=args.command == "live", producer_pid=args.producer_pid,
                             aborted=args.producer_aborted, delay=args.delay_seconds)
                print(json.dumps(status(consumer.store), indent=2))
        else:
            store = Store(args.database)
            try:
                print(json.dumps(status(store), indent=2) if args.command == "status" else export(store, args.output))
            finally:
                store.close()
    except (IntegrityError, OSError, ValueError, KeyError, TypeError) as exc:
        print(f"Consumer stopped: {exc}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
