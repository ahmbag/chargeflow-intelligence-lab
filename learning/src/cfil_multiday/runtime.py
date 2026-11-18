"""Long-term model, adaptation journal and outcomes share one SQLite commit."""
import hashlib
import json
from pathlib import Path
from cfil_prearrival.runtime import PreArrivalRuntime
from cfil_stream.contracts import canonical, require
from .state import MultiDayState


class MultiDayRuntime(PreArrivalRuntime):
    def __init__(self, *args, strategy, settings=None, **kwargs):
        settings = settings or json.loads((Path(__file__).resolve().parents[2] / 'config/multiday-v1.json').read_text())
        super().__init__(*args, **kwargs)
        try:
            fingerprint = hashlib.sha256()
            for path in sorted(Path(__file__).parent.glob('*.py')):
                fingerprint.update(path.name.encode() + path.read_bytes())
            binding = dict(strategy=strategy, settings=settings, codeHash=fingerprint.hexdigest())
            old = self.store.get('multiDayBinding')
            require(old is None or old == binding, 'Multi-day strategy/configuration/code changed during recovery')
            self.store.db.executescript('''
                CREATE TABLE IF NOT EXISTS md_pairs(decision TEXT, park TEXT, payload TEXT NOT NULL, PRIMARY KEY(decision,park));
                CREATE TABLE IF NOT EXISTS md_labels(position INTEGER PRIMARY KEY, payload TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS md_transitions(position INTEGER, ordinal INTEGER, payload TEXT NOT NULL, PRIMARY KEY(position,ordinal));
            ''')
            if old is None:
                require(self.position == 0 and not isinstance(self.state, MultiDayState), 'Cannot adopt an active legacy learner')
                self.state = MultiDayState(self.state, strategy, settings)
                self.store.db.execute('BEGIN IMMEDIATE')
                self.store.put('multiDayBinding', binding)
                self.store.save(self.position, self.prefix, self.state)
                self.store.db.execute('COMMIT')
        except BaseException:
            self.close()
            raise

    def forecast_evidence(self, context, candidates, answer):
        state = self.state
        if state.challenger is None:
            return
        for candidate, forecast in zip(candidates, answer['forecasts']):
            pair = dict(challengerId=state.challenger_id, championId=state.champion_id,
                        champion=forecast['waitSeconds'], challenger=state.challenger.predict(candidate)['waitSeconds'],
                        sourceSequence=context['sourceSequence'], timeMs=context['timeMs'])
            self.store.db.execute('INSERT INTO md_pairs VALUES(?,?,?)',
                                 (context['decisionId'], candidate['parkId'], canonical(pair)))

    def after_label(self, work, decision, message, position):
        profile = self.consumer.store.db.execute('SELECT data FROM profiles WHERE id=?', (decision['context']['vehicleId'],)).fetchone()
        require(profile is not None and json.loads(profile[0])['kind'] == 'Truck',
                'Pre-arrival adaptation accepts registered truck decisions only')
        row = self.store.db.execute('SELECT payload FROM md_pairs WHERE decision=? AND park=?',
                                   (decision['context']['decisionId'], decision['candidate']['parkId'])).fetchone()
        events = work.resolve(decision, message, json.loads(row[0]) if row else None)
        now = message['availableAtSimTimeMs']
        sample = dict(timeMs=now, predictionTimeMs=decision['context']['timeMs'],
                      day=now // 86400000 + 1, kind='Truck', park=decision['candidate']['parkId'],
                      horizon=work.horizon(decision['candidate']['features']),
                      revision=decision['forecast']['revision'], currentRevision=work.regressor.updates,
                      championId=work.champion_id, target=message['targetSeconds'],
                      prediction=decision['forecast']['waitSeconds'], fallback=not decision['forecast']['supported'],
                      attemptId=message['chargingAttemptId'])
        self.store.db.execute('INSERT INTO md_labels VALUES(?,?)', (position, canonical(sample)))
        for ordinal, event in enumerate(events):
            event.update(timeMs=now, position=position, labels=work.learned.copy())
            self.store.db.execute('INSERT INTO md_transitions VALUES(?,?,?)', (position, ordinal, canonical(event)))

    def report(self):
        result = super().report()
        state = self.state
        result['multiDay'] = dict(binding=self.store.get('multiDayBinding'), championId=state.champion_id,
            challengerId=state.challenger_id if state.challenger is not None else None,
            learnedLabels=state.learned, lastUpdateMs=state.last_update_ms, firstLabelMs=state.first_label_ms,
            experienceAgeMs=None if state.first_label_ms is None else self.store.get('simulationTimeMs', 0)-state.first_label_ms,
            fullOperatingDays=self.store.get('simulationTimeMs', 0)//86400000,
            alarms=state.alarms, promotions=state.promotions,
            detectorWidth=state.detector.width, carQuality='N/A: pre-arrival policy is truck-only')
        return result
