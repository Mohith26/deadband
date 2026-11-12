import { DecimalPipe, PercentPipe } from '@angular/common';
import { Component, computed, input } from '@angular/core';
import { AlarmLoad, Oee } from './models';

export type LoadBand = 'acceptable' | 'manageable' | 'demanding' | 'flood';

/** ISA-18.2 / EEMUA 191 guidance on annunciated alarms per operator per 10 minutes. */
export function loadBand(per10min: number): LoadBand {
  if (per10min >= 10) return 'flood';
  if (per10min > 2) return 'demanding';
  if (per10min > 1) return 'manageable';
  return 'acceptable';
}

@Component({
  selector: 'db-kpi-tiles',
  imports: [DecimalPipe, PercentPipe],
  template: `
    <section class="tiles">
      @if (load(); as l) {
        <div class="tile" [class]="'tile band-' + band()" data-testid="load-tile">
          <h3>Alarm load</h3>
          <div class="big">{{ l.annunciatedLast10Min }}</div>
          <div>in last 10 min ({{ band() }})</div>
          <small>average {{ l.annunciatedPer10MinAverage | number: '1.2-2' }} / 10 min; {{ l.activeAlarms }} active; {{ l.shelved }} shelved</small>
          @if (l.inFlood) { <div class="flood" role="alert">ALARM FLOOD</div> }
        </div>
      }
      @if (oee(); as o) {
        <div class="tile" data-testid="oee-tile">
          <h3>Line OEE</h3>
          <div class="big">{{ o.oee | percent: '1.1-1' }}</div>
          <small>A {{ o.availability | percent: '1.0-1' }} x P {{ o.performance | percent: '1.0-1' }} x Q {{ o.quality | percent: '1.0-1' }}</small>
        </div>
      }
    </section>
  `,
  styles: [`
    .tiles { display: flex; gap: 12px; } .tile { background: #dcdcdc; padding: 10px 14px; min-width: 180px; }
    h3 { margin: 0 0 4px; font-size: 13px; color: #333; } .big { font-size: 32px; font-weight: 600; }
    .band-demanding { box-shadow: inset 4px 0 #ff7f0e; } .band-flood { box-shadow: inset 4px 0 #d62728; }
    .flood { margin-top: 6px; background: #d62728; color: #fff; font-weight: 700; padding: 2px 6px; }
  `],
})
export class KpiTilesComponent {
  readonly load = input<AlarmLoad | null>(null);
  readonly oee = input<Oee | null>(null);
  readonly band = computed<LoadBand>(() => loadBand(this.load()?.annunciatedLast10Min ?? 0));
}
