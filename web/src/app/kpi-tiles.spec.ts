import { TestBed } from '@angular/core/testing';
import { KpiTilesComponent, loadBand } from './kpi-tiles';

describe('loadBand', () => {
  it('follows the ISA-18.2 / EEMUA alarm-rate bands', () => {
    expect(loadBand(0)).toBe('acceptable');
    expect(loadBand(1)).toBe('acceptable');
    expect(loadBand(2)).toBe('manageable');
    expect(loadBand(5)).toBe('demanding');
    expect(loadBand(10)).toBe('flood');
  });
});

describe('KpiTilesComponent', () => {
  it('renders alarm load, flood banner and OEE', async () => {
    await TestBed.configureTestingModule({ imports: [KpiTilesComponent] }).compileComponents();
    const f = TestBed.createComponent(KpiTilesComponent);
    f.componentRef.setInput('load', { plantTimeSec: 3600, annunciatedLast10Min: 12, annunciatedPer10MinAverage: 2.4, inFlood: true, activeAlarms: 14, shelved: 1 });
    f.componentRef.setInput('oee', { availability: 1, performance: 0.9, quality: 0.5, oee: 0.45, samples: 10 });
    f.detectChanges();
    const el: HTMLElement = f.nativeElement;
    expect(el.querySelector('[data-testid="load-tile"]')!.className).toContain('band-flood');
    expect(el.querySelector('.flood')!.textContent).toContain('ALARM FLOOD');
    expect(el.querySelector('[data-testid="oee-tile"] .big')!.textContent!.trim()).toBe('45.0%');
  });
});
