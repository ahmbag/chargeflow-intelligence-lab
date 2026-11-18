"""Adaptation uses delayed, paired predictions, never refits old predictions."""
import copy
import math
from river import drift
from cfil_prearrival.model import PreState
from cfil_online.strategies import Metric
from cfil_stream.contracts import require


class MultiDayState(PreState):
    def __init__(self, initial, strategy, settings):
        self.__dict__.update(copy.deepcopy(initial.__dict__))
        require(strategy in ('M0', 'M1', 'M2'), 'Unknown multi-day strategy')
        require(settings['version']=='MULTIDAY-ADAPTATION-V1' and
                1 <= settings['minimumTruckLabels'] <= settings['minimumPairedLabels'] <= settings['maximumChallengerLabels'] and
                0 < settings['minimumRelativeImprovement'] < 1 and 0 < settings['maximumTruckMaeRatio'] <= 1 and
                settings['cooldownSeconds'] > 0 and settings['rollingWindow'] > 0 and
                math.isfinite(settings['signalScaleSeconds']) and settings['signalScaleSeconds'] > 0,
                'Invalid versioned adaptation gates')
        self.strategy, self.settings = strategy, settings
        self.detector = drift.ADWIN(**settings['adwin'])
        self.challenger = None
        self.challenger_id = 0
        self.champion_id = 0
        self.last_promotion_ms = -10**15
        self.paired = {'champion': Metric(settings['rollingWindow']), 'challenger': Metric(settings['rollingWindow'])}
        self.learned = {'Truck': 0, 'Car': 0}
        self.last_update_ms = None
        self.first_label_ms = None
        self.alarms = self.promotions = 0

    def learn(self, features, park, target, update_model, development):
        # Runtime calls this before the transactional label hook. Adaptation owns
        # that update instead, so evaluation always precedes training. M0 freezes
        # historical fallback means as well as weights/scaler (unlike legacy P2).
        pass

    def resolve(self, decision, message, paired_prediction):
        now = message['availableAtSimTimeMs']
        target = message['targetSeconds']
        error = decision['forecast']['waitSeconds'] - target
        events = []
        pair = paired_prediction
        if self.challenger is not None and pair is not None and pair['challengerId'] == self.challenger_id:
            # Both values were stored at the SAME original decision barrier.
            # Late outcomes from an older challenger cannot promote a new one.
            self.paired['champion'].update(pair['champion'] - target)
            self.paired['challenger'].update(pair['challenger'] - target)
        if self.strategy == 'M2':
            self.detector.update(min(abs(error) / self.settings['signalScaleSeconds'], 1))
            if self.detector.drift_detected:
                self.alarms += 1
                events.append(dict(type='DRIFT_ALARM', interpretation='Unconfirmed error-distribution change'))
                if self.challenger is None and now - self.last_promotion_ms >= self.settings['cooldownSeconds'] * 1000:
                    self.challenger_id += 1
                    self.challenger = PreState(self.config)
                    self.challenger.development_horizons = copy.deepcopy(self.development_horizons)
                    self.paired = {k: Metric(self.settings['rollingWindow']) for k in ('champion', 'challenger')}
                    events.append(dict(type='CHALLENGER_STARTED', challengerId=self.challenger_id))
        if self.strategy != 'M0':
            super().learn(decision['candidate']['features'], decision['candidate']['parkId'], target, True, False)
            self.learned['Truck'] += 1  # CFIL-04 decisions are truck-only; cars remain N/A.
            self.first_label_ms = now if self.first_label_ms is None else self.first_label_ms
            self.last_update_ms = now
            if self.challenger is not None:
                self.challenger.learn(decision['candidate']['features'], decision['candidate']['parkId'], target, True, False)
        if self.challenger is not None:
            a, b = (self.paired[k].report() for k in ('champion', 'challenger'))
            n = a['count']
            enough = n >= max(self.settings['minimumPairedLabels'], self.settings['minimumTruckLabels'])
            better = enough and b['maeSeconds'] < a['maeSeconds'] * (1 - self.settings['minimumRelativeImprovement'])
            truck_safe = enough and b['maeSeconds'] <= a['maeSeconds'] * self.settings['maximumTruckMaeRatio']
            cooled = now - self.last_promotion_ms >= self.settings['cooldownSeconds'] * 1000
            if enough:
                events.append(dict(type='CHALLENGER_COMPARISON', pairedTruckLabels=n, champion=a, challenger=b))
            if better and truck_safe and cooled:
                revision = self.regressor.updates
                self.regressor = self.challenger.regressor
                # Public revision is a monotone stream revision, not the training
                # count of a freshly promoted regressor. Identity is logged apart.
                self.regressor.updates = revision
                self.global_mean, self.groups = self.challenger.global_mean, self.challenger.groups
                self.champion_id = self.challenger_id
                self.last_promotion_ms = now
                self.promotions += 1
                self.challenger = None
                events.append(dict(type='PROMOTION', championId=self.champion_id, revision=revision))
            elif n >= self.settings['maximumChallengerLabels']:
                self.challenger = None
                events.append(dict(type='RETAIN_CHAMPION', reason='quality-gate-not-met', pairedTruckLabels=n))
        return events
