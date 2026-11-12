import { Component, computed, input } from '@angular/core';
import { TrendPoint } from './models';

/** Minimal SVG trend with alarm limits drawn as dashed lines; points beyond a limit are marked. */
@Component({
  selector: 'db-trend',
  template: `
    <figure class="trend">
      <figcaption>{{ label() }}</figcaption>
      <svg [attr.viewBox]="'0 0 ' + width + ' ' + height" preserveAspectRatio="none" role="img" [attr.aria-label]="label() + ' trend'">
        @if (hiY() !== null) { <line class="limit" x1="0" [attr.x2]="width" [attr.y1]="hiY()" [attr.y2]="hiY()" /> }
        @if (loY() !== null) { <line class="limit" x1="0" [attr.x2]="width" [attr.y1]="loY()" [attr.y2]="loY()" /> }
        <polyline class="line" [attr.points]="path()" />
        @for (p of excursions(); track $index) { <circle class="excursion" [attr.cx]="p.x" [attr.cy]="p.y" r="2.5" /> }
      </svg>
    </figure>
  `,
  styles: [`
    .trend { margin: 0; } figcaption { font-size: 12px; color: #333; }
    svg { width: 100%; height: 120px; background: #e9e9e9; }
    .line { fill: none; stroke: #222; stroke-width: 1.2; } .limit { stroke: #777; stroke-dasharray: 4 3; }
    .excursion { fill: #d62728; }
  `],
})
export class TrendComponent {
  readonly points = input<TrendPoint[]>([]);
  readonly hi = input<number | null>(null);
  readonly lo = input<number | null>(null);
  readonly label = input('');
  readonly width = 400;
  readonly height = 120;

  private readonly range = computed(() => {
    const vs = this.points().map((p) => p.v);
    const extra = [this.hi(), this.lo()].filter((v): v is number => v !== null);
    const all = [...vs, ...extra];
    if (!all.length) return { min: 0, max: 1 };
    let min = Math.min(...all), max = Math.max(...all);
    if (min === max) { min -= 1; max += 1; }
    const pad = (max - min) * 0.05;
    return { min: min - pad, max: max + pad };
  });

  y(v: number): number {
    const { min, max } = this.range();
    return +(this.height - ((v - min) / (max - min)) * this.height).toFixed(2);
  }

  private x(i: number): number {
    const n = this.points().length;
    return n <= 1 ? 0 : +((i / (n - 1)) * this.width).toFixed(2);
  }

  readonly path = computed(() => this.points().map((p, i) => `${this.x(i)},${this.y(p.v)}`).join(' '));
  readonly hiY = computed(() => (this.hi() === null ? null : this.y(this.hi()!)));
  readonly loY = computed(() => (this.lo() === null ? null : this.y(this.lo()!)));
  readonly excursions = computed(() =>
    this.points().map((p, i) => ({ p, i }))
      .filter(({ p }) => (this.hi() !== null && p.v > this.hi()!) || (this.lo() !== null && p.v < this.lo()!))
      .map(({ p, i }) => ({ x: this.x(i), y: this.y(p.v) })));
}
