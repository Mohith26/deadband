import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AlarmSummaryComponent, ShelveIntent } from './alarm-summary';
import { ActiveAlarm } from './models';

const alarm = (over: Partial<ActiveAlarm>): ActiveAlarm => ({
  tag: 'XMEAS09', kind: 'Hi', priority: 'Medium', raisedAtSec: 600, value: 121, limit: 120.6,
  annunciated: true, suppressedBy: null, acknowledged: false, detail: null, ...over,
});

describe('AlarmSummaryComponent', () => {
  let fixture: ComponentFixture<AlarmSummaryComponent>;
  let el: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [AlarmSummaryComponent] }).compileComponents();
    fixture = TestBed.createComponent(AlarmSummaryComponent);
    el = fixture.nativeElement;
  });

  function render(alarms: ActiveAlarm[]) {
    fixture.componentRef.setInput('alarms', alarms);
    fixture.detectChanges();
  }

  it('sorts annunciated alarms by priority then time and hides suppressed ones from the main list', () => {
    render([
      alarm({ tag: 'XMEAS01', priority: 'Medium', raisedAtSec: 100 }),
      alarm({ tag: 'XMEAS07', priority: 'High', raisedAtSec: 900 }),
      alarm({ tag: 'XMEAS21', priority: 'Medium', raisedAtSec: 50, annunciated: false, suppressedBy: 'chatter' }),
    ]);
    const rows = Array.from(el.querySelectorAll('[data-testid="alarm-row"]'));
    expect(rows.map((r) => r.children[2].textContent!.trim())).toEqual(['XMEAS07', 'XMEAS01']);
    expect(rows[0].className).toContain('p-high');
    expect(el.querySelector('.suppressed summary')!.textContent).toContain('1 suppressed by design');
  });

  it('shows an empty state', () => {
    render([]);
    expect(el.querySelector('.empty')!.textContent).toContain('No annunciated alarms');
  });

  it('emits acknowledge for an unacknowledged alarm', () => {
    render([alarm({})]);
    let got: unknown;
    fixture.componentInstance.ack.subscribe((e) => (got = e));
    (el.querySelector('.actions button') as HTMLButtonElement).click();
    expect(got).toEqual({ tag: 'XMEAS09', kind: 'Hi' });
  });

  it('will not shelve without a reason', () => {
    render([alarm({})]);
    const emitted: ShelveIntent[] = [];
    fixture.componentInstance.shelve.subscribe((e) => emitted.push(e));
    const buttons = el.querySelectorAll<HTMLButtonElement>('.actions button');
    buttons[buttons.length - 1].click();
    fixture.detectChanges();
    const confirm = el.querySelector<HTMLButtonElement>('form.shelve button[type="submit"]')!;
    expect(confirm.disabled).toBe(true);
    const input = el.querySelector<HTMLInputElement>('form.shelve input')!;
    input.value = 'TT-9 recalibration';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(confirm.disabled).toBe(false);
    confirm.click();
    expect(emitted).toEqual([{ tag: 'XMEAS09', reason: 'TT-9 recalibration', minutes: 30 }]);
  });

  it('does not allow shelving the multivariate deviation alarm', () => {
    render([alarm({ tag: 'PROCESS.DEVIATION', kind: 'Deviation', priority: 'High', detail: 'top deviating tags: XMEAS01' })]);
    const buttons = el.querySelectorAll<HTMLButtonElement>('.actions button');
    expect(buttons[buttons.length - 1].disabled).toBe(true);
    expect(el.querySelector('.detail')!.textContent).toContain('XMEAS01');
  });
});
