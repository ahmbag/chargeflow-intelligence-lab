"""One authenticated local checkpoint and its effects commit in one SQLite transaction."""
import hashlib
import hmac
import json
import os
import pickle
import secrets
import sqlite3
from pathlib import Path
from cfil_stream.contracts import canonical, require

MAX_CHECKPOINT=4*1024*1024
DDL="""
CREATE TABLE IF NOT EXISTS metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS checkpoint(id INTEGER PRIMARY KEY CHECK(id=1),position INTEGER NOT NULL,
 prefix_hash TEXT NOT NULL,state BLOB NOT NULL,signature TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS receipts(position INTEGER PRIMARY KEY,message_id TEXT NOT NULL,hash TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS message_receipts ON receipts(message_id);
CREATE TABLE IF NOT EXISTS observations(id TEXT PRIMARY KEY,message TEXT NOT NULL,park TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS predictions(observation TEXT NOT NULL,strategy TEXT NOT NULL,payload TEXT NOT NULL,
 diagnostic TEXT NOT NULL,PRIMARY KEY(observation,strategy));
CREATE TABLE IF NOT EXISTS outcomes(observation TEXT PRIMARY KEY,message TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS errors(observation TEXT NOT NULL,strategy TEXT NOT NULL,kind TEXT NOT NULL,
 park TEXT NOT NULL,cold INTEGER NOT NULL,wait_band TEXT NOT NULL,position INTEGER NOT NULL,
 time_ms INTEGER NOT NULL,target REAL NOT NULL,error REAL NOT NULL,raw_error REAL,
 fallback INTEGER NOT NULL,clipped INTEGER NOT NULL,after_freeze INTEGER NOT NULL,
 PRIMARY KEY(observation,strategy));
CREATE INDEX IF NOT EXISTS segmented_errors ON errors(strategy,kind,park,cold,wait_band);
CREATE TABLE IF NOT EXISTS alarms(position INTEGER PRIMARY KEY,payload TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS latency(observation TEXT PRIMARY KEY,payload TEXT NOT NULL);
"""


class Store:
    def __init__(self,path,binding):
        self.path=Path(path).resolve(); self.path.parent.mkdir(parents=True,exist_ok=True)
        self.db=None; self.lock=None
        try:
            self.lock=open(str(self.path)+".lock","a+b"); self.lock.seek(0)
            if os.name=="nt":
                import msvcrt
                if not self.lock.read(1): self.lock.write(b"0"); self.lock.flush()
                self.lock.seek(0); msvcrt.locking(self.lock.fileno(),msvcrt.LK_NBLCK,1)
            else:
                import fcntl
                fcntl.flock(self.lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
            key_path=Path(str(self.path)+".key")
            existed=self.path.exists()
            if not existed:
                # There is deliberately no checkpoint import command. A key is
                # created only for a new local store, never recovered from a blob.
                with open(key_path,"xb") as output: output.write(secrets.token_bytes(32))
                if os.name!="nt": key_path.chmod(0o600)
            require(key_path.exists(),"Trusted local signing key missing; refuse checkpoint")
            self.key=key_path.read_bytes(); require(len(self.key)==32,"Invalid signing key")
            self.db=sqlite3.connect(self.path,isolation_level=None,timeout=10)
            self.db.execute("PRAGMA journal_mode=WAL"); self.db.execute("PRAGMA synchronous=FULL")
            self.db.execute("PRAGMA foreign_keys=ON"); self.db.execute("PRAGMA cache_size=-8192")
            self.db.execute("PRAGMA temp_store=FILE")
            self.db.executescript(DDL)
            old=self.get("binding")
            require(old is None or old==binding,"Incompatible experiment, source, software or model configuration")
            if old is None: self.put("binding",binding)
            self.binding=binding
        except BaseException:
            self.close(); raise

    def get(self,key,default=None):
        row=self.db.execute("SELECT value FROM metadata WHERE key=?",(key,)).fetchone()
        return json.loads(row[0]) if row else default

    def put(self,key,value):
        self.db.execute("INSERT INTO metadata VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value",(key,canonical(value)))

    def signature(self,position,prefix,blob):
        header=canonical(dict(binding=self.binding,position=position,prefixHash=prefix)).encode()
        return hmac.new(self.key,header+b"\0"+blob,hashlib.sha256).hexdigest()

    def load(self):
        row=self.db.execute("SELECT position,prefix_hash,state,signature FROM checkpoint WHERE id=1").fetchone()
        if not row: return None
        position,prefix,blob,signature=row
        require(len(blob)<=MAX_CHECKPOINT,"Oversized checkpoint")
        require(hmac.compare_digest(signature,self.signature(position,prefix,blob)),"Checkpoint authentication failed")
        # Authentication MUST precede unpickling. The trust boundary is the local
        # store AND its separately generated key; imported/replaced key+DB pairs
        # are not trusted. Protect both with the user's filesystem permissions.
        return position,prefix,pickle.loads(blob)

    def save(self,position,prefix,state):
        blob=pickle.dumps(state,protocol=5)
        require(len(blob)<=MAX_CHECKPOINT,"Checkpoint size budget exceeded")
        self.db.execute("INSERT INTO checkpoint VALUES(1,?,?,?,?) ON CONFLICT(id) DO UPDATE SET position=excluded.position,prefix_hash=excluded.prefix_hash,state=excluded.state,signature=excluded.signature",
                        (position,prefix,blob,self.signature(position,prefix,blob)))
        return len(blob)

    def close(self):
        if self.db is not None: self.db.close(); self.db=None
        if self.lock is not None: self.lock.close(); self.lock=None
