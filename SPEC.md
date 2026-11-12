# Deadband: spec

## Problem

Process plants drown operators in alarms. The usual "as-found" alarm system puts a statistical HI/LO limit on every tag
with no hysteresis, no delay and no priority logic, so a single upset produces a burst of alarms and normal noise keeps
producing alarms that train operators to ignore them. ISA-18.2 (and EEMUA 191) describe how to fix this: rationalize every
alarm (it must need an operator action), add deadbands and on/off delays, suppress chattering alarms, detect floods, and
make shelving a controlled, audited action. Configuration changes to the alarm system must be versioned and reversible.

## Why it matters to the target team

The Rockwell Automation Software and DevSecOps team builds web and hybrid web applications (Angular, C#, ASP.NET Web APIs,
.NET Core) that sit beside control systems like FactoryTalk, with CI/CD, automated tests and configuration management.
This project is that shape of tool on a real industrial benchmark: an ASP.NET Core Web API that runs an alarm engine on a
live tag stream, an Angular HMI on top, alarm configuration managed as validated, versioned files, and a pipeline that
tests all of it.

## Data

Real, public: the Tennessee Eastman Process (Downs and Vogel 1993) closed-loop data from the Braatz group
(https://github.com/camaramm/tennessee-eastman-profBraatz). 52 tags (41 measurements + 11 controller outputs), one sample
every 3 minutes. Training: `d00.dat` (25 h normal) and `d01..d21.dat` (one per labeled fault). Test: `d00_te.dat` (48 h
normal) and `d01_te..d21_te.dat` (48 h each, fault introduced at 8 h). Nothing is simulated by this project; the "live"
stream in the app is a replay of these files.

## Design

- `Deadband.Core` (C#): TEP loader, ISA-18.2 alarm state machine (HI/LO/HIHI/LOLO, deadband, on/off delay, shelving with
  user + reason + expiry, chattering suppression, flood detection with first-out and priority-based suppression), PCA
  T2/SPE process monitor with EWMA filtering (one `PROCESS.DEVIATION` alarm naming the top deviating tags), OEE adapted to a
  continuous process, config validator, structural diff, append-only versioned config store with SHA-256 integrity.
- `Deadband.Api` (ASP.NET Core 8): versioned REST (`/api/v1/...`, Asp.Versioning), OpenAPI/Swagger, EF Core + SQLite
  alarm journal, Server-Sent Events alarm stream, background replay service, config publish/diff/rollback endpoints that
  hot-swap the running engine.
- `web/` (Angular 22): ISA-101 style HMI (grey, color only for priority), alarm summary with ack and reason-required
  shelving, trends with limit lines, alarm-load and OEE tiles, config history with diff and two-step rollback.
- `Deadband.Cli`: `validate` (CI gate for config files), `diff`, `evaluate` (benchmark), `export-configs`, `loadtest`.

## Metrics and baselines

1. **Operator alarm load**: annunciated alarms per operator per 10 minutes over all 22 test runs (1,056 h), plus peak
   10-minute count and share of 10-minute windows in flood (>= 10). Baseline: the as-found configuration (mean +/- 3 sigma
   on all 52 tags, no deadband or delay).
2. **Detection and false alarms**: of the 21 labeled faults, how many get an annunciated alarm within 2 h of onset and the
   median delay; false alarms = annunciated alarms during 216 h of normal operation (all of `d00_te` plus the 8 h before
   onset in each fault run). Same baseline. Also reported: expected number of detections a pure false-alarm process at the
   same normal-operation rate would produce, so a noisy configuration cannot win by alarming constantly.
3. **API latency**: p95 latency under a 32-worker closed-loop load test against a 302k-row alarm journal, on one laptop.
   Baseline: the same API without the composite (Tag, TimeSec) history index.
4. Test counts (.NET + Angular) as reported by the runners.

Protocol: rationalization parameters (sigma multiple, on-delay, PCA confidence, PCA variance) are chosen on training files
only (5-fold blocked cross-validation over `d00.dat` for false alarms, `d01..d21.dat` for detection), the model is refit on
all of `d00.dat`, and the test files are evaluated once. `scripts/reproduce.sh` regenerates `results/results.json`.
