import { DecimalPipe } from '@angular/common';
import { Component, computed, input, output, signal } from '@angular/core';
import { ActiveAlarm, AlarmKind, PRIORITY_RANK, plantClock } from './models';

export interface ShelveIntent { tag: string; reason: string; minutes: number; }

/**
 * ISA-101 style alarm summary: grey by default, color only encodes priority; suppressed alarms are listed separately so the
 * operator can see what the system is hiding and why. Shelving cannot be submitted without a reason.
 */
@Component({
  selector: 'db-alarm-summary',
  template: `
    <table class="alarms" aria-label="Active alarms">
      <thead><tr><th>Pri</th><th>Time</th><th>Tag</th><th>Type</th><th>Value</th><th>Limit</th><th></th></tr></thead>
      <tbody>
        @for (a of annunciated(); track a.tag + a.kind) {
          <tr [class]="'p-' + a.priority.toLowerCase()" [class.unacked]="!a.acknowledged" data-testid="alarm-row">
            <td><span class="chip">{{ a.priority }}</span></td>
            <td>{{ clock(a.raisedAtSec) }}</td>
            <td [title]="a.detail ?? ''">{{ a.tag }}</td>
            <td>{{ a.kind }}</td>
            <td>{{ a.value | number: '1.0-2' }}</td>
            <td>{{ a.limit | number: '1.0-2' }}</td>
            <td class="actions">
              @if (!a.acknowledged) { <button type="button" (click)="ack.emit({ tag: a.tag, kind: a.kind })">Ack</button> }
              <button type="button" (click)="startShelve(a.tag)" [disabled]="a.tag === 'PROCESS.DEVIATION'">Shelve</button>
            </td>
          </tr>
          @if (a.detail) { <tr class="detail"><td></td><td colspan="6">{{ a.detail }}</td></tr> }
        } @empty {
          <tr><td colspan="7" class="empty">No annunciated alarms</td></tr>
        }
      </tbody>
    </table>
    @if (shelving(); as tag) {
      <form class="shelve" (submit)="$event.preventDefault(); submitShelve(tag, reason.value, +minutes.value)">
        <strong>Shelve {{ tag }}</strong>
        <input #reason placeholder="Reason (required)" (input)="reasonText.set(reason.value)" aria-label="Shelve reason" />
        <select #minutes aria-label="Shelve duration"><option value="30">30 min</option><option value="60">1 h</option><option value="240">4 h</option></select>
        <button type="submit" [disabled]="reasonText().trim().length < 3">Confirm</button>
        <button type="button" (click)="shelving.set(null)">Cancel</button>
      </form>
    }
    @if (suppressed().length) {
      <details class="suppressed"><summary>{{ suppressed().length }} suppressed by design</summary>
        <ul>@for (a of suppressed(); track a.tag + a.kind) { <li>{{ a.tag }} {{ a.kind }}: {{ a.suppressedBy }}</li> }</ul>
      </details>
    }
  `,
  imports: [DecimalPipe],
  styles: [`
    .alarms { width: 100%; border-collapse: collapse; font-variant-numeric: tabular-nums; }
    th, td { text-align: left; padding: 4px 6px; border-bottom: 1px solid #c9c9c9; }
    .chip { display: inline-block; min-width: 56px; padding: 1px 6px; border-radius: 3px; font-weight: 600; color: #111; }
    .p-critical .chip { background: #d62728; color: #fff; } .p-high .chip { background: #ff7f0e; }
    .p-medium .chip { background: #f2d024; } .p-low .chip { background: #9ecae1; }
    .unacked td:first-child .chip { outline: 2px solid #111; }
    .detail td { font-size: 12px; color: #444; border-bottom: none; }
    .empty { color: #555; font-style: italic; }
    .shelve { display: flex; gap: 8px; align-items: center; margin-top: 8px; }
  `],
})
export class AlarmSummaryComponent {
  readonly alarms = input<ActiveAlarm[]>([]);
  readonly ack = output<{ tag: string; kind: AlarmKind }>();
  readonly shelve = output<ShelveIntent>();
  readonly shelving = signal<string | null>(null);
  readonly reasonText = signal('');

  readonly annunciated = computed(() =>
    this.alarms().filter((a) => a.annunciated)
      .sort((x, y) => PRIORITY_RANK[y.priority] - PRIORITY_RANK[x.priority] || x.raisedAtSec - y.raisedAtSec));
  readonly suppressed = computed(() => this.alarms().filter((a) => !a.annunciated));

  clock = plantClock;

  startShelve(tag: string) { this.reasonText.set(''); this.shelving.set(tag); }

  submitShelve(tag: string, reason: string, minutes: number) {
    if (reason.trim().length < 3) return;
    this.shelve.emit({ tag, reason: reason.trim(), minutes });
    this.shelving.set(null);
  }
}
