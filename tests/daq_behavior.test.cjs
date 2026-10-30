'use strict';
// Runs the shipped browser logic and controls with a small DOM/hub test double.
// No hardware measurements or simulated analogue traces are claimed here.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const test = require('node:test');
const DpllConfig = require('../wwwroot/js/configuration.js');
const appSource = fs.readFileSync(path.join(__dirname, '../wwwroot/js/app.js'), 'utf8');
const appWithoutBoot = appSource.slice(0, appSource.indexOf('// ---------------- Boot ----------------'));

function configuration(overrides = {}) {
  return {
    FirmwareVersion: 'DPLL v2.0.1 build=test123', ReadbackComplete: true,
    GeneratorSource: 0, Kp: 0.00000200000022, Ki: 0.0002, Kd: 0,
    CenterVoltage: 1.654321, TargetPhase: 0, MaxSlew: 30,
    LoopPeriodMs: 20, LockThresholdNs: 500, LockHoldCycles: 10,
    LockMemoryTimeoutMs: 5000, StreamPeriodMs: 10, SignalLossBehavior: 0,
    DdsMcoClock: 0, DdsCenterFreq: 28000.1234, DdsMinFreq: 20000, DdsMaxFreq: 40000,
    ReferenceCaptureEdge: 0, ZcdCaptureEdge: 0, TrackingDirection: 0,
    ManualMode: false, ...overrides,
  };
}

function fixture(cfg = configuration()) {
  const elements = new Map();
  const calls = [];
  const logs = [];
  function element(id) {
    if (!elements.has(id)) {
      let value = '';
      elements.set(id, {
        get value() { return value; }, set value(v) { value = String(v); },
        textContent: '', hidden: false, disabled: false, className: '', dataset: {},
        listeners: {}, classList: { toggle() {} },
        addEventListener(type, handler) { this.listeners[type] = handler; },
        appendChild() {}, removeChild() {}, querySelector() { return { textContent: '' }; },
      });
    }
    return elements.get(id);
  }
  const context = vm.createContext({
    DpllConfig, performance: { now: () => 0 }, setInterval() {}, setTimeout() {},
    signalR: { HubConnectionState: { Connected: 'Connected' } },
    document: { getElementById: element, createElement: () => element('log-entry') },
    recordLog: (level, message) => logs.push({ level, message }),
    hubInvoke: async (method, patch) => { calls.push({ method, patch: patch === undefined ? undefined : JSON.parse(JSON.stringify(patch)) }); return true; },
  });
  vm.runInContext(appWithoutBoot, context);
  vm.runInContext("log = recordLog; state.connected = true; connection = { state: 'Connected', invoke: (...args) => hubInvoke(...args) };", context);
  const run = (code) => vm.runInContext(code, context);
  const load = (next) => { context.nextConfig = next; run('onConfiguration(nextConfig)'); };
  load(cfg);
  return { element, calls, logs, run, load, context };
}

function sample(overrides = {}) {
  return { ReferenceFrequencyHz: 28000, PhaseErrorNs: 0, DACVoltage_V: 1.65,
    LockStatus: 3, PhaseStale: 0, EepromStatus: 1, DdsStatus: 0xC2, ...overrides };
}

// Before: every populated field was sent, including restart-sensitive centers/clock.
test('unchanged Apply sends no patch, even when displayed floats are rounded', async () => {
  const f = fixture();
  assert.equal(f.element('centerInput').value, '1.654');
  await f.run('onApplyConfig()');
  assert.equal(f.calls.length, 0);
  assert.match(f.logs.at(-1).message, /No configuration changes/);
});

test('one gain edit sends only that gain and its expected source', async () => {
  const f = fixture();
  f.element('kpInput').value = '0.000003';
  await f.run('onApplyConfig()');
  assert.deepEqual(f.calls, [{ method: 'ApplyConfiguration', patch: { Kp: 0.000003, ExpectedGeneratorSource: 0 } }]);
});

test('reverting an edit, or changing numeric formatting, produces no writes', async () => {
  const f = fixture();
  f.element('kpInput').value = '0.0002';
  f.element('kpInput').value = '0.00000200';
  f.element('ddsMcoClockInput').value = '00';
  await f.run('onApplyConfig()');
  assert.equal(f.calls.length, 0);
});

// Before: old DAC gains were sent before selecting DDS, then displayed in DDS units.
test('source switch sends source only and reads the new bank before editing gains', async () => {
  const f = fixture();
  f.element('kpInput').value = '0.000009'; // unsaved DAC gain must not become a DDS gain
  f.element('centerInput').value = '1.7';
  f.element('generatorSourceInput').value = '1';
  f.element('generatorSourceInput').listeners.change();
  assert.equal(f.element('kpInput').disabled, true);
  assert.equal(f.element('kpUnit').textContent, '(V/ns)');
  assert.match(f.element('sourceChangeHint').textContent, /switches source only/);
  f.context.hubInvoke = async (method, patch) => {
    f.calls.push({ method, patch: JSON.parse(JSON.stringify(patch)) });
    f.load(configuration({ GeneratorSource: 1, Kp: 0.075, Ki: 0.25, Kd: 0.001, MaxSlew: 20000 }));
    return true;
  };
  await f.run('onApplyConfig()');
  assert.deepEqual(f.calls[0], { method: 'ApplyConfiguration', patch: { GeneratorSource: 1, ExpectedGeneratorSource: 0 } });
  assert.equal(f.element('kpInput').value, '0.0750000');
  assert.equal(f.element('kpInput').disabled, false);
  assert.equal(f.element('kpUnit').textContent, '(Hz/ns)');
  assert.equal(f.element('kiUnit').textContent, '(Hz/(ns·s))');
  assert.equal(f.element('kdUnit').textContent, '(Hz·s/ns)');
  f.element('kpInput').value = '0.08';
  await f.run('onApplyConfig()');
  assert.deepEqual(f.calls[1].patch, { Kp: 0.08, ExpectedGeneratorSource: 1 });
});

test('source modal cancel returns to the confirmed bank without writing', () => {
  const f = fixture();
  f.element('generatorSourceInput').value = '1';
  f.element('generatorSourceInput').listeners.change();
  f.element('modalCancelBtn').listeners.click();
  assert.equal(f.element('generatorSourceInput').value, '0');
  assert.equal(f.element('kpInput').disabled, false);
  assert.equal(f.calls.length, 0);
});

test('switching to PWM Dither triggers critical hardware verification warning modal', () => {
  const f = fixture(); // confirmed is 0 (DAC)
  f.element('generatorSourceInput').value = '2';
  f.element('generatorSourceInput').listeners.change();
  assert.equal(f.element('jumperModal').hidden, false);
  assert.match(f.element('modalSourceText').textContent, /PWM Dither/);
  assert.match(f.element('modalReqText').innerHTML, /PA8/);
  assert.match(f.element('modalReqText').innerHTML, /PA0/);
  assert.match(f.element('modalReqText').innerHTML, /Gate Driver/);
  f.element('modalConfirmBtn').listeners.click();
  assert.equal(f.element('jumperModal').hidden, true);
  assert.match(f.logs.at(-1).message, /PWM Dither/);
});

test('negative gains are rejected visibly; direction is an explicit field', async () => {
  const f = fixture();
  f.element('kpInput').value = '-0.000002';
  await f.run('onApplyConfig()');
  assert.equal(f.calls.length, 0);
  assert.equal(f.logs.at(-1).level, 'error');
  assert.match(f.logs.at(-1).message, /Kp/);
  f.load(configuration());
  f.element('trackingDirectionInput').value = '1';
  await f.run('onApplyConfig()');
  assert.deepEqual(f.calls[0].patch, { TrackingDirection: 1 });
});

test('edge edits preserve the unedited edge through a partial patch', async () => {
  const f = fixture();
  f.element('zcdCaptureEdgeInput').value = '1';
  await f.run('onApplyConfig()');
  assert.deepEqual(f.calls[0].patch, { ZcdCaptureEdge: 1 });
});

test('old firmware leaves unsupported controls unknown without writing default polarity', async () => {
  const f = fixture(configuration({ FirmwareVersion: 'DPLL v2.0.0', ReferenceCaptureEdge: null, ZcdCaptureEdge: null, TrackingDirection: null }));
  for (const id of ['referenceCaptureEdgeInput', 'zcdCaptureEdgeInput', 'trackingDirectionInput']) {
    assert.equal(f.element(id).value, '');
    assert.equal(f.element(id).disabled, true);
  }
  assert.equal(f.element('applyCfgBtn').disabled, false);
  f.element('targetInput').value = '10';
  await f.run('onApplyConfig()');
  assert.deepEqual(f.calls[0].patch, { TargetPhase: 10 });
});

test('incomplete readback never exposes a mixed source and gain bank for Apply', async () => {
  const f = fixture();
  f.load(configuration({ GeneratorSource: 1, Kp: 12, ReadbackComplete: false }));
  assert.equal(f.element('applyCfgBtn').disabled, true);
  assert.equal(f.element('kpInput').value, '0.0000020');
  assert.equal(f.element('kpUnit').textContent, '(V/ns)');
  await f.run('onApplyConfig()');
  assert.equal(f.calls.length, 0);
});

test('firmware version and build identity are visible without interpreting version as capability', () => {
  const f = fixture();
  assert.equal(f.element('firmwareVersion').textContent, 'DPLL v2.0.1 build=test123');
});

// Before: 0xC2 was DDS ERR because inversion bit was part of hardware status.
test('DDS hardware, direction and active source flags decode independently in the actual status UI', () => {
  const f = fixture();
  for (const hardware of [0, 1, 2]) for (const inverted of [false, true]) for (const active of [false, true]) {
    const DdsStatus = hardware | (inverted ? 0x40 : 0) | (active ? 0x80 : 0);
    const decoded = DpllConfig.decodeDdsStatus(DdsStatus);
    assert.deepEqual(decoded, { hardware, ready: hardware > 0, verified: hardware === 2, inverted, active });
    f.context.nextSample = sample({ DdsStatus });
    f.run('updateStatus(nextSample)');
    assert.equal(f.element('ddsBadge').className, hardware ? 'badge badge-ok' : 'badge badge-danger');
    assert.match(f.element('trackingDirectionStatus').textContent, inverted ? /Inverted/ : /Normal/);
  }
  assert.equal(DpllConfig.decodeDdsStatus(0x7F).ready, false);
});

test('controller MANUAL=4 is displayed and lock state is not recomputed from instantaneous phase', () => {
  const f = fixture();
  f.context.nextSample = sample({ LockStatus: 4 });
  f.run('updateStatus(nextSample)');
  assert.equal(f.element('lockStateText').textContent, 'MANUAL');
  assert.equal(f.element('manualFlag').hidden, false);
  f.context.nextSample = sample({ LockStatus: 2, PhaseErrorNs: 0 });
  f.run('updateStatus(nextSample)');
  assert.equal(f.element('lockStateText').textContent, 'TRACK');
  assert.equal(f.element('manualFlag').hidden, true);
});

test('failed hub Apply never logs a confirmed settings write', async () => {
  const f = fixture();
  f.context.hubInvoke = async () => { throw new Error('readback timed out'); };
  f.element('targetInput').value = '42';
  await f.run('onApplyConfig()');
  assert.equal(f.logs.some(x => x.level === 'ok' && /confirmed/.test(x.message)), false);
  assert.match(f.logs.at(-1).message, /not confirmed/);
});

test('invalid integers and reversed DDS bounds are rejected before any hub write', async () => {
  const f = fixture();
  f.element('loopInput').value = '1.5';
  await f.run('onApplyConfig()');
  assert.equal(f.calls.length, 0);
  f.load(configuration());
  f.element('ddsMinInput').value = '45000';
  await f.run('onApplyConfig()');
  assert.equal(f.calls.length, 0);
  assert.match(f.logs.at(-1).message, /minimum/);
});

test('zero lock threshold is rejected before sending a setter the firmware would ignore', async () => {
  const f = fixture();
  f.element('thrInput').value = '0';
  await f.run('onApplyConfig()');
  assert.equal(f.calls.length, 0);
  assert.equal(f.logs.at(-1).level, 'error');
  assert.match(f.logs.at(-1).message, /LockThresholdNs/);
});

test('frequency calibration sweep steps through target range, records deviation, and produces results', async () => {
  let virtualTime = 0;
  const elements = new Map();
  const calls = [];
  const logs = [];
  function element(id) {
    if (!elements.has(id)) {
      let value = '';
      elements.set(id, {
        get value() { return value; }, set value(v) { value = String(v); },
        textContent: '', hidden: false, disabled: false, className: '', dataset: {}, style: {},
        listeners: {}, classList: { toggle() {}, add() {}, remove() {} },
        addEventListener(type, handler) { this.listeners[type] = handler; },
        appendChild() {}, removeChild() {}, querySelector() { return { textContent: '' }; },
      });
    }
    return elements.get(id);
  }
  const context = vm.createContext({
    DpllConfig,
    performance: { now: () => (virtualTime += 50) },
    setInterval() {},
    setTimeout: (fn) => fn(),
    signalR: { HubConnectionState: { Connected: 'Connected' } },
    document: {
      getElementById: element,
      createElement: () => element('row'),
      body: { appendChild() {}, removeChild() {} }
    },
    recordLog: (level, message) => logs.push({ level, message }),
    hubInvoke: async (method, patch) => {
      calls.push({ method, patch });
      return true;
    },
  });

  vm.runInContext(appWithoutBoot, context);
  vm.runInContext("log = recordLog; state.connected = true; connection = { state: 'Connected', invoke: (...args) => hubInvoke(...args) };", context);
  const run = (code) => vm.runInContext(code, context);

  // Set calibration inputs: 20000 to 22000 in 1000 Hz steps (3 steps: 20000, 21000, 22000)
  element('calStartFreqInput').value = '20000';
  element('calStopFreqInput').value = '22000';
  element('calStepFreqInput').value = '1000';
  element('calSettleTimeInput').value = '50';

  // Simulate telemetry returning measured frequency with slight offset
  run(`
    state.telemetry = { ReferenceFrequencyHz: 20000.5, PhaseErrorNs: 0, DACVoltage_V: 1.65, LockStatus: 3 };
  `);

  await run('startCalibrationSweep()');

  // Verify SetManualFrequency calls
  const freqCalls = calls.filter(c => c.method === 'SetManualFrequency');
  assert.equal(freqCalls.length, 3);
  assert.equal(freqCalls[0].patch, 20000);
  assert.equal(freqCalls[1].patch, 21000);
  assert.equal(freqCalls[2].patch, 22000);

  // Check state results
  const results = run('calState.results');
  assert.equal(results.length, 3);
  assert.equal(results[0].target, 20000);
  assert.equal(results[1].target, 21000);
  assert.equal(results[2].target, 22000);
  assert.equal(element('downloadCalCsvBtn').disabled, false);
  assert.match(logs.at(-1).message, /Calibration completed successfully/);
});
