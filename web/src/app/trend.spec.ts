import { TestBed } from '@angular/core/testing';
import { TrendComponent } from './trend';

describe('TrendComponent', () => {
  it('draws one vertex per point, both limit lines, and marks excursions', async () => {
    await TestBed.configureTestingModule({ imports: [TrendComponent] }).compileComponents();
    const f = TestBed.createComponent(TrendComponent);
    f.componentRef.setInput('points', [{ t: 0, v: 1 }, { t: 180, v: 5 }, { t: 360, v: 12 }, { t: 540, v: -3 }]);
    f.componentRef.setInput('hi', 10);
    f.componentRef.setInput('lo', 0);
    f.detectChanges();
    const el: HTMLElement = f.nativeElement;
    expect(el.querySelector('polyline')!.getAttribute('points')!.split(' ').length).toBe(4);
    expect(el.querySelectorAll('line.limit').length).toBe(2);
    expect(el.querySelectorAll('circle.excursion').length).toBe(2);
  });

  it('maps higher values to smaller y (SVG coordinates)', async () => {
    await TestBed.configureTestingModule({ imports: [TrendComponent] }).compileComponents();
    const c = TestBed.createComponent(TrendComponent).componentInstance;
    expect(c.y(1)).toBeLessThan(c.y(0.5));
  });
});
