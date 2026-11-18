"""All mutable projection state lives in the same SQLite transaction as the offset."""
import json
import sqlite3
from pathlib import Path
from .contracts import canonical, require

DDL = """
CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS receipts(sequence INTEGER PRIMARY KEY, hash TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS entities(id TEXT PRIMARY KEY, event_id TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS profiles(id TEXT PRIMARY KEY, data TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS parks(id TEXT PRIMARY KEY, data TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS attempts(id TEXT PRIMARY KEY, vehicle TEXT NOT NULL, park TEXT NOT NULL, outcome TEXT);
CREATE TABLE IF NOT EXISTS queue(attempt TEXT PRIMARY KEY, vehicle TEXT UNIQUE NOT NULL, park TEXT NOT NULL, sequence INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS occupants(park TEXT, bay TEXT, vehicle TEXT UNIQUE NOT NULL, PRIMARY KEY(park,bay));
CREATE TABLE IF NOT EXISTS arrivals(sequence INTEGER PRIMARY KEY, park TEXT NOT NULL, time INTEGER NOT NULL);
CREATE INDEX IF NOT EXISTS arrivals_window ON arrivals(park,time);
CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY, park TEXT NOT NULL, started INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS history(park TEXT PRIMARY KEY, count INTEGER NOT NULL, total_seconds REAL NOT NULL);
CREATE TABLE IF NOT EXISTS observations(id TEXT PRIMARY KEY, attempt TEXT UNIQUE NOT NULL, vehicle TEXT NOT NULL,
    kind TEXT NOT NULL, opened INTEGER NOT NULL, message TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS outcomes(observation TEXT PRIMARY KEY REFERENCES observations(id), message TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS exclusions(attempt TEXT PRIMARY KEY, vehicle TEXT NOT NULL, kind TEXT NOT NULL, reason TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS journal(position INTEGER PRIMARY KEY, message_id TEXT UNIQUE NOT NULL, message TEXT NOT NULL, processed_at_utc TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS recording(message_id TEXT PRIMARY KEY, hash TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS recorded_observations(id TEXT PRIMARY KEY, message TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS recorded_outcomes(id TEXT PRIMARY KEY, message TEXT NOT NULL);
CREATE TRIGGER IF NOT EXISTS immutable_observation BEFORE UPDATE ON observations BEGIN SELECT RAISE(ABORT,'Immutable observation'); END;
CREATE TRIGGER IF NOT EXISTS immutable_outcome BEFORE UPDATE ON outcomes BEGIN SELECT RAISE(ABORT,'Immutable outcome'); END;
CREATE TRIGGER IF NOT EXISTS immutable_journal BEFORE UPDATE ON journal BEGIN SELECT RAISE(ABORT,'Immutable journal'); END;
"""


class Store:
    def __init__(self, path):
        Path(path).parent.mkdir(parents=True, exist_ok=True)
        self.db = sqlite3.connect(path, isolation_level=None, timeout=10)
        self.db.row_factory = sqlite3.Row
        self.db.execute("PRAGMA journal_mode=WAL")
        self.db.execute("PRAGMA synchronous=FULL")
        self.db.execute("PRAGMA foreign_keys=ON")
        self.db.execute("PRAGMA cache_size=-8192")
        self.db.execute("PRAGMA busy_timeout=10000")
        require(self.db.execute("PRAGMA journal_mode").fetchone()[0] == "wal"
                and self.db.execute("PRAGMA synchronous").fetchone()[0] == 2, "Required SQLite durability unavailable")
        self.db.executescript(DDL)

    def get(self, key, default=None):
        row = self.db.execute("SELECT value FROM meta WHERE key=?", (key,)).fetchone()
        return json.loads(row[0]) if row else default

    def put(self, key, value):
        self.db.execute("INSERT INTO meta VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value", (key, canonical(value)))

    def increment(self, key, amount=1):
        self.put(key, self.get(key, 0) + amount)

    def close(self):
        self.db.close()
