import { Component, input, output, signal } from '@angular/core';
import { ConfigDiff, ConfigVersion } from './models';

/** Configuration history with a field-level diff against the active version and a guarded rollback. */
@Component({
  selector: 'db-config-history',
  template: `
    <ol class="versions">
      @for (v of versions(); track v.version) {
        <li [class.active]="v.version === active()">
          <button type="button" (click)="select.emit(v.version)" [attr.aria-pressed]="v.version === selected()">v{{ v.version }}</button>
          {{ v.message }} <small>by {{ v.author }}{{ v.rollbackOf ? ' (rollback of v' + v.rollbackOf + ')' : '' }}</small>
          @if (v.version === active()) { <em>active</em> }
        </li>
      }
    </ol>
    @if (diff(); as d) {
      <div class="diff" data-testid="diff">
        <strong>v{{ selected() }} vs active v{{ active() }}</strong>
        @if (!d.addedTags.length && !d.removedTags.length && !d.changes.length) { <p>No differences.</p> }
        <ul>
          @for (t of d.addedTags; track t) { <li class="add">+ {{ t }}</li> }
          @for (t of d.removedTags; track t) { <li class="del">- {{ t }}</li> }
          @for (c of d.changes; track c.path) { <li class="chg">{{ c.path }}: {{ c.before ?? 'unset' }} &rarr; {{ c.after ?? 'unset' }}</li> }
        </ul>
        @if (selected() !== null && selected() !== active()) {
          @if (!confirming()) {
            <button type="button" (click)="confirming.set(true)">Roll back to v{{ selected() }}</button>
          } @else {
            <button type="button" class="danger" (click)="rollback.emit(selected()!); confirming.set(false)">Confirm rollback (creates a new version)</button>
            <button type="button" (click)="confirming.set(false)">Cancel</button>
          }
        }
      </div>
    }
  `,
  styles: [`
    .versions { padding-left: 18px; } .active { font-weight: 600; } .diff { background: #e9e9e9; padding: 8px; }
    .add { color: #1b5e20; } .del { color: #b71c1c; } .danger { background: #b71c1c; color: #fff; }
  `],
})
export class ConfigHistoryComponent {
  readonly versions = input<ConfigVersion[]>([]);
  readonly active = input<number>(0);
  readonly selected = input<number | null>(null);
  readonly diff = input<ConfigDiff | null>(null);
  readonly select = output<number>();
  readonly rollback = output<number>();
  readonly confirming = signal(false);
}
