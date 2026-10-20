/* ============================================================
   DPLL Ultrasonic DAQ — front-end
   SignalR hub: /hubs/dpll
   ============================================================ */
'use strict';

// ---------------- State ----------------
const state = {
  connected: false,
  port: null,
  telemetry: null,
  config: null,
  paused: false,
  streamFresh: false,
  lastStreamAt: 0,
  sampleCount: 0,
  sampleWindowStart: performance.now(),
  sampleWindowCount: 0,
  charts: null,
  logging: false,
  logStartSample: 0,
};

// ---------------- Chart config ----------------
const CHART_POINTS = 600; // ~6 s at 100 Hz
const SERIES = {
  freq:  { label: 'Frequency (Hz)',    color: '#38bdf8', decimals: 2 },
  phase: { label: 'Phase Error (ns)',  color: '#fbbf24', decimals: 1 },
  dac:   { label: 'DAC Voltage (V)',   color: '#34d399', decimals: 3 },
};

// Ring-buffer for each series — avoids repeated Array.shift() at 100 Hz
const _buf = {
  ts:    new Array(CHART_POINTS).fill(''),
  freq:  new Array(CHART_POINTS).fill(null),
  phase: new Array(CHART_POINTS).fill(null),
  dac:   new Array(CHART_POINTS).fill(null),
};
let _bufHead = 0;   // next write index
let _bufCount = 0;  // how many valid samples
let _rafPending = false;  // RAF scheduled?

// ---------------- DOM refs ----------------
const $ = (id) => document.getElementById(id);
const els = {
  connBadge: $('connBadge'),
  connBadgeText: $('connBadgeText'),
  eepromBadge: $('eepromBadge'),
  eepromBadgeText: $('eepromBadgeText'),
  ddsBadge: $('ddsBadge'),
  ddsBadgeText: $('ddsBadgeText'),
  streamBadge: $('streamBadge'),
  streamBadgeText: $('streamBadgeText'),
  lockIndicator: $('lockIndicator'),
  lockStateText: $('lockStateText'),
  freqValue: $('freqValue'),
  phaseValue: $('phaseValue'),
  dacValue: $('dacValue'),
  loopValue: $('loopValue'),
  staleFlag: $('staleFlag'),
  manualFlag: $('manualFlag'),
  sampleRateChip: $('sampleRateChip'),
  pauseBtn: $('pauseBtn'),
  clearBtn: $('clearBtn'),
  logChip: $('logChip'),
  startLogBtn: $('startLogBtn'),
  stopLogBtn: $('stopLogBtn'),
  refreshCfgBtn: $('refreshCfgBtn'),
  applyCfgBtn: $('applyCfgBtn'),
  saveCfgBtn: $('saveCfgBtn'),
  loadCfgBtn: $('loadCfgBtn'),
  generatorSourceInput: $('generatorSourceInput'),
  ddsCenterInput: $('ddsCenterInput'),
  ddsMinInput: $('ddsMinInput'),
  ddsMaxInput: $('ddsMaxInput'),
  ddsCenterField: $('ddsCenterField'),
  ddsMinField: $('ddsMinField'),
  ddsMaxField: $('ddsMaxField'),
  kpUnit: $('kpUnit'),
  jumperModal: $('jumperModal'),
  modalCancelBtn: $('modalCancelBtn'),
  modalConfirmBtn: $('modalConfirmBtn'),
  kpInput: $('kpInput'),
  kiInput: $('kiInput'),
  kdInput: $('kdInput'),
  centerInput: $('centerInput'),
  targetInput: $('targetInput'),
  slewInput: $('slewInput'),
  loopInput: $('loopInput'),
  thrInput: $('thrInput'),
  holdInput: $('holdInput'),
  timeoutInput: $('timeoutInput'),
  streamInput: $('streamInput'),
  lossInput: $('lossInput'),
  resetLoopBtn: $('resetLoopBtn'),
  runLoopBtn: $('runLoopBtn'),
  shutdownBtn: $('shutdownBtn'),
  tabManualDac: $('tabManualDac'),
  tabManualDds: $('tabManualDds'),
  manualDacBox: $('manualDacBox'),
  manualDdsBox: $('manualDdsBox'),
  manualDacSlider: $('manualDacSlider'),
  manualDacInput: $('manualDacInput'),
  manualDacValue: $('manualDacValue'),
  manualSetBtn: $('manualSetBtn'),
  manualDdsSlider: $('manualDdsSlider'),
  manualDdsInput: $('manualDdsInput'),
  manualDdsValue: $('manualDdsValue'),
  manualDdsSetBtn: $('manualDdsSetBtn'),
  log: $('log'),
  clearLogBtn: $('clearLogBtn'),
};

// ---------------- SignalR ----------------
let connection = null;

function initSignalR() {
  connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/dpll')
    .withAutomaticReconnect([0, 1000, 3000, 5000, 10000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();

  connection.on('Telemetry', onTelemetry);
  connection.on('Configuration', onConfiguration);
  connection.on('ConnectionState', onConnectionState);
  connection.on('EepromActionResult', onEepromActionResult);

  connection.onreconnecting(() => {
    log('warn', 'SignalR reconnecting…');
  });
  connection.onreconnected(() => {
    log('ok', 'SignalR reconnected');
    refreshConfiguration();
  });
  connection.onclose(() => {
    log('error', 'SignalR connection closed');
    setUiConnected(false);
  });

  connection.start()
    .then(() => {
      log('ok', 'Connected to server');
      refreshConfiguration();
    })
    .catch((err) => {
      log('error', `SignalR start failed: ${err}`);
      setTimeout(initSignalR, 3000);
    });
}

// ---------------- Hub calls ----------------
async function call(method, ...args) {
  if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
    log('warn', 'Not connected to server');
    return;
  }
  try {
    return await connection.invoke(method, ...args);
  } catch (err) {
    log('error', `hub ${method}: ${err}`);
  }
}

function refreshConfiguration() {
  call('RefreshConfiguration');
}

// ---------------- Hub events ----------------
function onTelemetry(t) {
  state.telemetry = t;
  state.lastStreamAt = performance.now();
  state.sampleCount++;
  state.sampleWindowCount++;

  // Live row counter while CSV logging is active (local sample delta).
  if (state.logging && state.sampleCount % 10 === 0) {
    els.logChip.textContent = `● ${els.logChip.dataset.fname || ''} · ${state.sampleCount - state.logStartSample} rows`;
  }
  if (state.sampleCount % 10 === 0) {
    const now = performance.now();
    const elapsed = (now - state.sampleWindowStart) / 1000;
    if (elapsed >= 1) {
      const rate = Math.round(state.sampleWindowCount / elapsed);
      els.sampleRateChip.textContent = `${rate} Hz`;
      state.sampleWindowStart = now;
      state.sampleWindowCount = 0;
    }
  }

  updateStatus(t);
  updateCharts(t);

  if (!state.paused && state.sampleCount % 25 === 0) {
    log('data', `freq=${fmt(t.ReferenceFrequencyHz, 2)} Hz · phase=${fmt(t.PhaseErrorNs, 1)} ns · dac=${fmt(t.DACVoltage_V, 3)} V · ${t.State}`);
  }
}

function onConfiguration(cfg) {
  state.config = cfg;
  if (els.generatorSourceInput) {
    els.generatorSourceInput.value = cfg.GeneratorSource !== undefined ? cfg.GeneratorSource : 0;
    _currentGeneratorSource = String(cfg.GeneratorSource ?? 0);
  }
  if (els.ddsCenterInput) els.ddsCenterInput.value = cfg.DdsCenterFreq !== undefined ? cfg.DdsCenterFreq : 28000;
  if (els.ddsMinInput) els.ddsMinInput.value = cfg.DdsMinFreq !== undefined ? cfg.DdsMinFreq : 20000;
  if (els.ddsMaxInput) els.ddsMaxInput.value = cfg.DdsMaxFreq !== undefined ? cfg.DdsMaxFreq : 40000;
  if (els.manualDdsSlider && cfg.DdsMinFreq !== undefined && cfg.DdsMaxFreq !== undefined) {
    els.manualDdsSlider.min = cfg.DdsMinFreq;
    els.manualDdsSlider.max = cfg.DdsMaxFreq;
  }
  if (els.manualDdsInput && cfg.DdsCenterFreq !== undefined) {
    if (!els.manualDdsInput.value || els.manualDdsInput.value === '28000') {
      els.manualDdsInput.value = cfg.DdsCenterFreq;
      if (els.manualDdsSlider) els.manualDdsSlider.value = cfg.DdsCenterFreq;
      if (els.manualDdsValue) els.manualDdsValue.textContent = `${parseFloat(cfg.DdsCenterFreq).toFixed(2)} Hz`;
    }
  }
  updateGeneratorSourceUI(cfg.GeneratorSource ?? 0);

  els.kpInput.value = fmt(cfg.Kp, 7);
  els.kiInput.value = fmt(cfg.Ki, 7);
  els.kdInput.value = fmt(cfg.Kd, 7);
  els.centerInput.value = fmt(cfg.CenterVoltage, 3);
  els.targetInput.value = fmt(cfg.TargetPhase, 1);
  els.slewInput.value = fmt(cfg.MaxSlew, 2);
  els.loopInput.value = cfg.LoopPeriodMs;
  els.thrInput.value = fmt(cfg.LockThresholdNs, 0);
  els.holdInput.value = cfg.LockHoldCycles;
  els.timeoutInput.value = cfg.LockMemoryTimeoutMs;
  els.streamInput.value = cfg.StreamPeriodMs;
  els.lossInput.value = cfg.SignalLossBehavior;
  els.applyCfgBtn.disabled = !state.connected;
  els.loopValue.textContent = cfg.LoopPeriodMs;

  els.manualFlag.hidden = !cfg.ManualMode;
  const srcName = cfg.GeneratorSource === 1 ? 'DDS (AD9833)' : 'DAC (CD4046 VCO)';
  log('ok', `Config loaded: Source=${srcName} · Kp=${cfg.Kp} Ki=${cfg.Ki} Kd=${cfg.Kd} · center=${cfg.CenterVoltage} V · loop=${cfg.LoopPeriodMs} ms · loss=${cfg.SignalLossName}`);
}

const LOCK_STATES = {
  0: { text: 'NO REF', cls: 'error' },
  1: { text: 'WAIT ZCD', cls: 'error' },
  2: { text: 'TRACK', cls: 'track' },
  3: { text: 'LOCK', cls: 'lock' },
};

function onConnectionState(code, port) {
  state.connected = code === 2; // Connected
  state.port = code === 2 ? port : null;

  setUiConnected(state.connected);

  switch (code) {
    case 0:
      setConnBadge('muted', 'OFFLINE');
      log('info', 'Disconnected');
      break;
    case 1:
      setConnBadge('warn', 'CONNECTING');
      log('info', `Connecting to ${port || 'configured port'}…`);
      break;
    case 2:
      setConnBadge('ok', `ONLINE · ${port || '—'}`);
      log('ok', `Connected: ${port || '—'}`);
      refreshConfiguration();
      break;
    case 3:
      setConnBadge('danger', 'ERROR');
      log('error', `Connection error: ${port || 'unknown'}`);
      break;
  }
}

function setUiConnected(connected) {
  els.applyCfgBtn.disabled = !connected || !state.config;
  els.saveCfgBtn.disabled = !connected;
  els.loadCfgBtn.disabled = !connected;
  if (els.manualDacSlider) els.manualDacSlider.disabled = !connected;
  if (els.manualDacInput) els.manualDacInput.disabled = !connected;
  if (els.manualSetBtn) els.manualSetBtn.disabled = !connected;
  if (els.manualDdsSlider) els.manualDdsSlider.disabled = !connected;
  if (els.manualDdsInput) els.manualDdsInput.disabled = !connected;
  if (els.manualDdsSetBtn) els.manualDdsSetBtn.disabled = !connected;
  els.startLogBtn.disabled = !connected || state.logging;
  if (!connected) {
    state.telemetry = null;
    els.freqValue.textContent = '—';
    els.phaseValue.textContent = '—';
    els.dacValue.textContent = '—';
    els.loopValue.textContent = '—';
    setLockBadge('error', 'OFFLINE');
    setStreamBadge('muted', 'NO DATA');
    setEepromBadge('muted', 'EEPROM —');
    setDdsBadge('muted', 'AD9833 —');
    els.staleFlag.hidden = true;
    els.manualFlag.hidden = true;
  }
}

// ---------------- Status UI ----------------
function updateStatus(t) {
  els.freqValue.textContent = fmt(t.ReferenceFrequencyHz, 2);
  els.phaseValue.textContent = fmt(t.PhaseErrorNs, 1);
  els.dacValue.textContent = fmt(t.DACVoltage_V, 3);
  els.loopValue.textContent = state.config ? state.config.LoopPeriodMs : '—';

  const s = LOCK_STATES[t.LockStatus] || { text: 'UNKNOWN', cls: 'error' };
  setLockBadge(s.cls, s.text);
  els.staleFlag.hidden = t.PhaseStale !== 1;

  if (t.EepromStatus === 1) {
    setEepromBadge('ok', 'EEPROM OK');
  } else {
    setEepromBadge('danger', 'EEPROM ERR');
  }

  const isDdsActive = (t.DdsStatus & 0x80) !== 0;
  const ddsHwStatus = t.DdsStatus & 0x7F;

  if (isDdsActive) {
    if (ddsHwStatus === 2) {
      setDdsBadge('ok', 'DDS ACTIVE · SIG OK');
    } else if (ddsHwStatus === 1) {
      setDdsBadge('ok', 'DDS ACTIVE');
    } else {
      setDdsBadge('danger', 'DDS ERR');
    }
  } else {
    if (ddsHwStatus === 2) {
      setDdsBadge('ok', 'AD9833 READY (SIG)');
    } else if (ddsHwStatus === 1) {
      setDdsBadge('ok', 'AD9833 READY');
    } else {
      setDdsBadge('danger', 'AD9833 ERR');
    }
  }
}

function setLockBadge(cls, text) {
  els.lockIndicator.className = `lock-indicator ${cls}`;
  els.lockStateText.textContent = text;
}

function setConnBadge(cls, text) {
  els.connBadge.className = `badge badge-${cls}`;
  els.connBadgeText.textContent = text;
}

function setStreamBadge(cls, text) {
  els.streamBadge.className = `badge badge-${cls}`;
  els.streamBadgeText.textContent = text;
}

function setEepromBadge(cls, text) {
  if (!els.eepromBadge) return;
  els.eepromBadge.className = `badge badge-${cls}`;
  els.eepromBadgeText.textContent = text;
}

function setDdsBadge(cls, text) {
  if (!els.ddsBadge) return;
  els.ddsBadge.className = `badge badge-${cls}`;
  els.ddsBadgeText.textContent = text;
}

// ---------------- Charts (ECharts + RAF batching) ----------------
function makeChartOption(def) {
  return {
    animation: false,
    backgroundColor: 'transparent',
    grid: { top: 10, right: 12, bottom: 28, left: 54 },
    xAxis: {
      type: 'category',
      data: [],
      axisLine:  { lineStyle: { color: '#223042' } },
      axisTick:  { lineStyle: { color: '#223042' } },
      axisLabel: { color: '#5d6b7e', fontSize: 11,
                   showMaxLabel: true, showMinLabel: false,
                   interval: (i, v) => v !== '' && i % Math.ceil(CHART_POINTS / 6) === 0 },
      splitLine: { show: false },
    },
    yAxis: {
      type: 'value',
      scale: true,
      axisLine:  { show: false },
      axisTick:  { show: false },
      axisLabel: { color: '#8b9bb0', fontSize: 11,
                   formatter: v => v.toFixed(def.decimals) },
      splitLine: { lineStyle: { color: '#223042', type: 'dashed' } },
    },
    series: [{
      type: 'line',
      data: [],
      symbol: 'none',
      lineStyle: { color: def.color, width: 1.6 },
      areaStyle: { color: { type: 'linear', x: 0, y: 0, x2: 0, y2: 1,
                            colorStops: [{ offset: 0, color: def.color + '44' },
                                         { offset: 1, color: def.color + '00' }] } },
      sampling: 'lttb',
    }],
    tooltip: {
      trigger: 'axis',
      backgroundColor: '#121822',
      borderColor: '#223042',
      textStyle: { color: '#e6edf5', fontSize: 12 },
      formatter: p => `${p[0].axisValue}<br/>${def.label}: <b>${p[0].value !== null ? Number(p[0].value).toFixed(def.decimals) : '—'}</b>`,
    },
  };
}

function initCharts() {
  state.charts = {};
  const defs = { freq: SERIES.freq, phase: SERIES.phase, dac: SERIES.dac };
  for (const [key, def] of Object.entries(defs)) {
    const dom = document.getElementById(`${key}Chart`);
    const ec = echarts.init(dom, null, { renderer: 'canvas', useDirtyRect: true });
    ec.setOption(makeChartOption(def));
    state.charts[key] = ec;
  }
  // resize all charts when window resizes
  window.addEventListener('resize', () => {
    for (const ec of Object.values(state.charts)) ec.resize();
  });
}

function _flushCharts() {
  _rafPending = false;
  if (state.paused) return;

  // Build ordered arrays from the ring buffer
  const len = Math.min(_bufCount, CHART_POINTS);
  const start = _bufCount < CHART_POINTS ? 0 : _bufHead;
  const ts    = new Array(len);
  const freq  = new Array(len);
  const phase = new Array(len);
  const dac   = new Array(len);
  for (let i = 0; i < len; i++) {
    const idx = (start + i) % CHART_POINTS;
    ts[i]    = _buf.ts[idx];
    freq[i]  = _buf.freq[idx];
    phase[i] = _buf.phase[idx];
    dac[i]   = _buf.dac[idx];
  }

  state.charts.freq.setOption( { xAxis: { data: ts }, series: [{ data: freq  }] }, false, true);
  state.charts.phase.setOption({ xAxis: { data: ts }, series: [{ data: phase }] }, false, true);
  state.charts.dac.setOption(  { xAxis: { data: ts }, series: [{ data: dac   }] }, false, true);
}

function updateCharts(t) {
  if (state.paused) return;

  // Write into ring buffer
  const now = new Date();
  const label = now.toLocaleTimeString('en-GB', { hour12: false }) +
                '.' + String(now.getMilliseconds()).padStart(3, '0');
  _buf.ts[_bufHead]    = label;
  _buf.freq[_bufHead]  = t.ReferenceFrequencyHz;
  _buf.phase[_bufHead] = t.PhaseErrorNs;
  _buf.dac[_bufHead]   = t.DACVoltage_V;
  _bufHead = (_bufHead + 1) % CHART_POINTS;
  if (_bufCount < CHART_POINTS) _bufCount++;

  // Schedule a single RAF render (deduplicated)
  if (!_rafPending) {
    _rafPending = true;
    requestAnimationFrame(_flushCharts);
  }
}

function clearCharts() {
  _buf.ts.fill(''); _buf.freq.fill(null);
  _buf.phase.fill(null); _buf.dac.fill(null);
  _bufHead = 0; _bufCount = 0;
  state.charts.freq.setOption( { xAxis: { data: [] }, series: [{ data: [] }] });
  state.charts.phase.setOption({ xAxis: { data: [] }, series: [{ data: [] }] });
  state.charts.dac.setOption(  { xAxis: { data: [] }, series: [{ data: [] }] });
  log('info', 'Charts cleared');
}

// ---------------- Stream freshness watchdog ----------------
setInterval(() => {
  const fresh = state.telemetry && (performance.now() - state.lastStreamAt) < 1200;
  if (fresh !== state.streamFresh) {
    state.streamFresh = fresh;
    setStreamBadge(fresh ? 'ok' : 'warn', fresh ? 'STREAMING' : 'STREAM LOST');
    if (!fresh && state.connected) {
      log('warn', 'Telemetry stream lost — checking connection…');
    }
  }
}, 500);

// ---------------- Controls ----------------
function onApplyConfig() {
  if (!state.connected) { log('warn', 'Not connected'); return; }
  const patch = {};
  const num = (el) => (el.value === '' || el.value === null ? null : parseFloat(el.value));

  const kp = num(els.kpInput);
  const ki = num(els.kiInput);
  const kd = num(els.kdInput);
  const center = num(els.centerInput);
  const target = num(els.targetInput);
  const slew = num(els.slewInput);
  const loop = num(els.loopInput);
  const thr = num(els.thrInput);
  const hold = num(els.holdInput);
  const timeout = num(els.timeoutInput);
  const stream = num(els.streamInput);
  const loss = parseInt(els.lossInput.value, 10);

  if (kp !== null && kp >= 0) patch.Kp = kp;
  if (ki !== null && ki >= 0) patch.Ki = ki;
  if (kd !== null && kd >= 0) patch.Kd = kd;
  if (center !== null && center >= 0 && center <= 3.3) patch.CenterVoltage = center;
  if (target !== null) patch.TargetPhase = target;
  if (slew !== null && slew > 0) patch.MaxSlew = slew;
  if (loop !== null && loop >= 1 && loop <= 1000) patch.LoopPeriodMs = loop;
  if (thr !== null && thr >= 0) patch.LockThresholdNs = thr;
  if (hold !== null && hold >= 1) patch.LockHoldCycles = hold;
  if (timeout !== null && timeout >= 0) patch.LockMemoryTimeoutMs = timeout;
  if (stream !== null && stream >= 1 && stream <= 65535) patch.StreamPeriodMs = stream;
  patch.SignalLossBehavior = loss;

  if (els.generatorSourceInput) {
    patch.GeneratorSource = parseInt(els.generatorSourceInput.value, 10);
  }
  const ddsCenter = num(els.ddsCenterInput);
  if (ddsCenter !== null && ddsCenter >= 1000 && ddsCenter <= 500000) patch.DdsCenterFreq = ddsCenter;
  const ddsMin = num(els.ddsMinInput);
  if (ddsMin !== null && ddsMin >= 1000 && ddsMin <= 500000) patch.DdsMinFreq = ddsMin;
  const ddsMax = num(els.ddsMaxInput);
  if (ddsMax !== null && ddsMax >= 1000 && ddsMax <= 500000) patch.DdsMaxFreq = ddsMax;

  if (Object.keys(patch).length === 0) {
    log('warn', 'No valid values to apply');
    return;
  }

  call('ApplyConfiguration', patch);
  log('info', 'Configuration applied to RAM (not saved to EEPROM)');
}

function onSaveConfig() {
  if (!state.connected) { log('warn', 'Not connected'); return; }
  call('SaveConfigurationToEeprom');
  log('info', 'Saving active configuration to EEPROM…');
}

function onLoadConfig() {
  if (!state.connected) { log('warn', 'Not connected'); return; }
  call('LoadConfigurationFromEeprom');
  log('info', 'Loading configuration from EEPROM…');
}

function onEepromActionResult(isSave, success) {
  if (isSave) {
    if (success) {
      log('ok', 'EEPROM: Configuration successfully saved to 24LC16B!');
    } else {
      log('error', 'EEPROM: Failed to save configuration!');
    }
  } else {
    if (success) {
      log('ok', 'EEPROM: Configuration successfully loaded and applied!');
    } else {
      log('error', 'EEPROM: Failed to load configuration from EEPROM!');
    }
  }
}

function onResetLoop() {
  call('ResetLoop');
  log('warn', 'Loop reset (center voltage, integrator cleared)');
}

function onRunLoop() {
  call('RunLoop');
  log('ok', 'Control loop re-enabled (auto mode)');
}

function onShutdown() {
  call('ShutdownLoop');
  log('error', 'Loop shutdown — DAC forced to 0 V');
}

function onManualSet() {
  const v = parseFloat(els.manualDacInput?.value || els.manualDacSlider.value);
  if (!isNaN(v) && v >= 0.0 && v <= 3.3) {
    call('SetManualVoltage', v);
    els.manualDacValue.textContent = `${v.toFixed(2)} V`;
    els.manualDacSlider.value = v;
    if (els.manualDacInput) els.manualDacInput.value = v.toFixed(2);
    log('warn', `Manual DAC voltage set to ${v.toFixed(2)} V (loop disengaged)`);
  }
}

function onManualSlider() {
  const v = parseFloat(els.manualDacSlider.value);
  els.manualDacValue.textContent = `${v.toFixed(2)} V`;
  if (els.manualDacInput) els.manualDacInput.value = v.toFixed(2);
}

function onManualDacInput() {
  const v = parseFloat(els.manualDacInput.value);
  if (!isNaN(v) && v >= 0.0 && v <= 3.3) {
    els.manualDacSlider.value = v;
    els.manualDacValue.textContent = `${v.toFixed(2)} V`;
  }
}

function onManualDdsSet() {
  const f = parseFloat(els.manualDdsInput?.value || els.manualDdsSlider.value);
  if (!isNaN(f) && f >= 1000 && f <= 500000) {
    call('SetManualFrequency', f);
    els.manualDdsValue.textContent = `${f.toFixed(2)} Hz`;
    els.manualDdsSlider.value = f;
    if (els.manualDdsInput) els.manualDdsInput.value = f.toFixed(2);
    log('warn', `Manual DDS frequency set to ${f.toFixed(2)} Hz (loop disengaged)`);
  }
}

function onManualDdsSlider() {
  const f = parseFloat(els.manualDdsSlider.value);
  els.manualDdsValue.textContent = `${f.toFixed(2)} Hz`;
  if (els.manualDdsInput) els.manualDdsInput.value = f.toFixed(2);
}

function onManualDdsInput() {
  const f = parseFloat(els.manualDdsInput.value);
  if (!isNaN(f) && f >= 1000 && f <= 500000) {
    els.manualDdsSlider.value = f;
    els.manualDdsValue.textContent = `${f.toFixed(2)} Hz`;
  }
}

function selectManualTab(tab) {
  const isDds = tab === 'dds';
  if (els.tabManualDac) els.tabManualDac.classList.toggle('active', !isDds);
  if (els.tabManualDds) els.tabManualDds.classList.toggle('active', isDds);
  if (els.manualDacBox) els.manualDacBox.hidden = isDds;
  if (els.manualDdsBox) els.manualDdsBox.hidden = !isDds;
}

// ---------------- Logging ----------------
function log(level, msg) {
  const now = new Date();
  const time = now.toLocaleTimeString('en-GB', { hour12: false }) + '.' + String(now.getMilliseconds()).padStart(3, '0');
  const line = document.createElement('div');
  line.className = 'log-line';
  line.innerHTML = `<span class="log-time">${time}</span><span class="log-level ${level}">${level.toUpperCase()}</span><span class="log-msg ${level === 'data' ? 'data' : ''}"></span>`;
  line.querySelector('.log-msg').textContent = msg;
  els.log.appendChild(line);
  while (els.log.childElementCount > 500) els.log.removeChild(els.log.firstChild);
  els.log.scrollTop = els.log.scrollHeight;
}

// ---------------- Generator Source & Jumper Modal UI ----------------
let _currentGeneratorSource = '0';

function updateGeneratorSourceUI(source) {
  const isDds = source === 1 || source === '1';
  if (els.ddsCenterField) els.ddsCenterField.classList.toggle('field-highlight', isDds);
  if (els.ddsMinField) els.ddsMinField.classList.toggle('field-highlight', isDds);
  if (els.ddsMaxField) els.ddsMaxField.classList.toggle('field-highlight', isDds);
  if (els.kpUnit) els.kpUnit.textContent = isDds ? '(Hz/ns)' : '(V/ns)';
  selectManualTab(isDds ? 'dds' : 'dac');
}

if (els.generatorSourceInput) {
  els.generatorSourceInput.addEventListener('change', () => {
    const selected = els.generatorSourceInput.value;
    if (selected === '1' && _currentGeneratorSource === '0') {
      if (els.jumperModal) els.jumperModal.hidden = false;
    } else {
      _currentGeneratorSource = selected;
      updateGeneratorSourceUI(parseInt(selected, 10));
    }
  });
}

if (els.modalCancelBtn) {
  els.modalCancelBtn.addEventListener('click', () => {
    if (els.jumperModal) els.jumperModal.hidden = true;
    if (els.generatorSourceInput) els.generatorSourceInput.value = '0';
    _currentGeneratorSource = '0';
    updateGeneratorSourceUI(0);
    log('info', 'Generator Source kept at DAC (CD4046 VCO)');
  });
}

if (els.modalConfirmBtn) {
  els.modalConfirmBtn.addEventListener('click', () => {
    if (els.jumperModal) els.jumperModal.hidden = true;
    _currentGeneratorSource = '1';
    updateGeneratorSourceUI(1);
    log('warn', 'Switched Generator Source to DDS (AD9833). Ensure resistor jumper is set to DDS!');
  });
}

// ---------------- Helpers ----------------
function fmt(value, decimals) {
  if (value === null || value === undefined || Number.isNaN(value)) return '—';
  return Number(value).toFixed(decimals);
}

// ---------------- Wire up events ----------------
els.applyCfgBtn.addEventListener('click', onApplyConfig);
els.saveCfgBtn.addEventListener('click', onSaveConfig);
els.loadCfgBtn.addEventListener('click', onLoadConfig);
els.resetLoopBtn.addEventListener('click', onResetLoop);
els.runLoopBtn.addEventListener('click', onRunLoop);
els.shutdownBtn.addEventListener('click', onShutdown);
els.manualSetBtn.addEventListener('click', onManualSet);
els.manualDacSlider.addEventListener('input', onManualSlider);
if (els.manualDacInput) {
  els.manualDacInput.addEventListener('input', onManualDacInput);
  els.manualDacInput.addEventListener('keydown', (e) => { if (e.key === 'Enter') onManualSet(); });
}

if (els.tabManualDac) els.tabManualDac.addEventListener('click', () => selectManualTab('dac'));
if (els.tabManualDds) els.tabManualDds.addEventListener('click', () => selectManualTab('dds'));

if (els.manualDdsSetBtn) els.manualDdsSetBtn.addEventListener('click', onManualDdsSet);
if (els.manualDdsSlider) els.manualDdsSlider.addEventListener('input', onManualDdsSlider);
if (els.manualDdsInput) {
  els.manualDdsInput.addEventListener('input', onManualDdsInput);
  els.manualDdsInput.addEventListener('keydown', (e) => { if (e.key === 'Enter') onManualDdsSet(); });
}
els.refreshCfgBtn.addEventListener('click', () => { refreshConfiguration(); log('info', 'Requesting configuration from firmware…'); });
els.pauseBtn.addEventListener('click', () => {
  state.paused = !state.paused;
  els.pauseBtn.textContent = state.paused ? 'Resume' : 'Pause';
  els.pauseBtn.classList.toggle('btn-warn', state.paused);
  log('info', state.paused ? 'Chart streaming paused' : 'Chart streaming resumed');
});
els.clearBtn.addEventListener('click', clearCharts);
els.clearLogBtn.addEventListener('click', () => { els.log.innerHTML = ''; });

// ---------------- Logging control ----------------
function setLoggingUi(active, file, rows) {
  els.startLogBtn.hidden = active;
  els.startLogBtn.disabled = active || !state.connected;
  els.stopLogBtn.hidden = !active;
  els.logChip.hidden = !active;
  if (active) {
    const fname = file ? file.split(/[\\/]/).pop() : '';
    els.logChip.textContent = `● ${fname} · ${rows ?? 0} rows`;
  }
}

async function onStartLogging() {
  if (state.logging) return;
  if (!state.connected) { log('warn', 'Not connected — cannot start logging'); return; }
  const file = await call('StartLogging');
  if (file) {
    state.logging = true;
    state.logStartSample = state.sampleCount;
    els.logChip.dataset.fname = file.split(/[\\/]/).pop() || '';
    setLoggingUi(true, file, 0);
    log('ok', `CSV logging started → ${file}`);
  } else {
    log('warn', 'Could not start logging (no connection or already active)');
  }
}

async function onStopLogging() {
  if (!state.logging) return;
  const file = await call('StopLogging');
  if (file) {
    state.logging = false;
    setLoggingUi(false, null, 0);
    log('ok', `CSV logging stopped → ${file}`);
  } else {
    state.logging = false;
    setLoggingUi(false, null, 0);
    log('warn', 'No active logging session');
  }
}

els.startLogBtn.addEventListener('click', onStartLogging);
els.stopLogBtn.addEventListener('click', onStopLogging);

// ---------------- Boot ----------------
initCharts();
initSignalR();
log('info', 'DPLL Ultrasonic DAQ UI ready');
