import { HttpClient } from '@angular/common/http';
import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { forkJoin } from 'rxjs';
import { AlarmSummaryComponent, ShelveIntent } from './alarm-summary';
import { ConfigHistoryComponent } from './config-history';
import { KpiTilesComponent } from './kpi-tiles';
import { ActiveAlarm, AlarmKind, AlarmLoad, ConfigDiff, ConfigVersion, Oee, ReplayStatus, TagAlarmConfig, TrendPoint, plantClock } from './models';
import { API, PlantService } from './plant.service';
import { TrendComponent } from './trend';

const TREND_TAGS = ['XMEAS07', 'XMEAS09', 'XMEAS08', 'XMEAS12', 'XMEAS15', 'XMEAS01', 'XMEAS17', 'XMEAS21'];

@Component({
  selector: 'app-root',
  imports: [AlarmSummaryComponent, KpiTilesComponent, TrendComponent, ConfigHistoryComponent],
  template: `
    <header>
      <h1>Deadband <small>alarm management for the Tennessee Eastman plant</small></h1>
      @if (replay(); as r) {
        <div class="status">
          Run {{ r.run }} @if (r.fault) { (fault: {{ r.fault }}, onset {{ clock((r.faultOnsetSample ?? 0) * 180) }}) }
          · plant time {{ clock(r.cursor * 180) }} · config v{{ r.configVersion }}
          · stream {{ plant.connected() ? 'live' : 'offline' }}
        </div>
      }
      <label>Operator <input [value]="operator()" (input)="operator.set($any($event.target).value)" /></label>
    </header>
    <main>
      <section class="left">
        <db-kpi-tiles [load]="load()" [oee]="oee()" />
        <h2>Alarm summary</h2>
        <db-alarm-summary [alarms]="alarms()" (ack)="onAck($event)" (shelve)="onShelve($event)" />
      </section>
      <section class="right">
        <h2>Trend
          <select (change)="pickTag($any($event.target).value)" aria-label="Trend tag">
            @for (t of trendTags; track t) { <option [value]="t" [selected]="t === trendTag()">{{ t }}</option> }
          </select>
        </h2>
        <db-trend [points]="trend()" [hi]="limits()?.hi ?? null" [lo]="limits()?.lo ?? null" [label]="trendTag()" />
        <h2>Configuration</h2>
        <db-config-history [versions]="versions()" [active]="activeVersion()" [selected]="selected()" [diff]="diff()"
          (select)="selectVersion($event)" (rollback)="onRollback($event)" />
      </section>
    </main>
  `,
  styles: [`
    :host { display: block; font-family: system-ui, sans-serif; background: #d4d4d4; color: #111; min-height: 100vh; padding: 12px; }
    header { display: flex; gap: 16px; align-items: baseline; flex-wrap: wrap; } h1 { margin: 0; font-size: 22px; }
    h1 small { font-weight: 400; color: #444; font-size: 13px; } .status { font-size: 13px; color: #333; }
    main { display: grid; grid-template-columns: 3fr 2fr; gap: 16px; margin-top: 12px; }
    h2 { font-size: 15px; margin: 14px 0 6px; }
  `],
})
export class App implements OnInit {
  readonly plant = inject(PlantService);
  private readonly http = inject(HttpClient);
  private readonly destroyRef = inject(DestroyRef);
  readonly trendTags = TREND_TAGS;
  readonly operator = signal('operator1');
  readonly alarms = signal<ActiveAlarm[]>([]);
  readonly load = signal<AlarmLoad | null>(null);
  readonly oee = signal<Oee | null>(null);
  readonly replay = signal<ReplayStatus | null>(null);
  readonly trendTag = signal(TREND_TAGS[0]);
  readonly trend = signal<TrendPoint[]>([]);
  readonly limits = signal<TagAlarmConfig | null>(null);
  readonly versions = signal<ConfigVersion[]>([]);
  readonly activeVersion = signal(0);
  readonly selected = signal<number | null>(null);
  readonly diff = signal<ConfigDiff | null>(null);
  private tagConfig: TagAlarmConfig[] = [];
  clock = plantClock;

  ngOnInit() {
    this.refreshConfig();
    this.refresh();
    const timer = setInterval(() => this.refresh(), 2000);
    const close = this.plant.connect(() => this.plant.active().subscribe((a) => this.alarms.set(a)));
    this.destroyRef.onDestroy(() => { clearInterval(timer); close(); });
  }

  refresh() {
    forkJoin({ a: this.plant.active(), l: this.plant.load(), o: this.plant.oee(), r: this.plant.replay(), t: this.plant.trend(this.trendTag()) })
      .subscribe(({ a, l, o, r, t }) => {
        this.alarms.set(a); this.load.set(l); this.oee.set(o); this.trend.set(t);
        if (this.replay()?.configVersion !== r.configVersion) this.refreshConfig();
        this.replay.set(r);
      });
  }

  refreshConfig() {
    this.plant.versions().subscribe((v) => { this.versions.set(v.versions); this.activeVersion.set(v.active); });
    this.http.get<{ tags: TagAlarmConfig[] }>(`${API}/config/active`).subscribe((c) => { this.tagConfig = c.tags; this.pickTag(this.trendTag()); });
  }

  pickTag(tag: string) {
    this.trendTag.set(tag);
    this.limits.set(this.tagConfig.find((t) => t.tag === tag && t.enabled) ?? null);
    this.plant.trend(tag).subscribe((t) => this.trend.set(t));
  }

  onAck(e: { tag: string; kind: AlarmKind }) { this.plant.acknowledge(e.tag, e.kind, this.operator()).subscribe(() => this.refresh()); }
  onShelve(e: ShelveIntent) { this.plant.shelve(e.tag, this.operator(), e.reason, e.minutes).subscribe(() => this.refresh()); }

  selectVersion(v: number) {
    this.selected.set(v);
    this.plant.diff(v, this.activeVersion()).subscribe((d) => this.diff.set(d));
  }

  onRollback(v: number) {
    this.plant.rollback(v, this.operator(), `rollback from HMI to v${v}`).subscribe(() => { this.selected.set(null); this.diff.set(null); this.refreshConfig(); });
  }
}
