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
  configFormBaseline: null,
  configBusy: false,
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
  armRawBtn: $('armRawBtn'),
  checkRawBtn: $('checkRawBtn'),
  downloadRawBtn: $('downloadRawBtn'),
  rawConditionInput: $('rawConditionInput'),
  rawCaptureStatus: $('rawCaptureStatus'),
  refreshCfgBtn: $('refreshCfgBtn'),
  applyCfgBtn: $('applyCfgBtn'),
  saveCfgBtn: $('saveCfgBtn'),
  loadCfgBtn: $('loadCfgBtn'),
  resetEepromBtn: $('resetEepromBtn'),
  generatorSourceInput: $('generatorSourceInput'),
  ddsCenterInput: $('ddsCenterInput'),
  ddsMinInput: $('ddsMinInput'),
  ddsMaxInput: $('ddsMaxInput'),
  ddsMcoClockInput: $('ddsMcoClockInput'),
  ddsMcoClockField: $('ddsMcoClockField'),
  ddsMcoClockHint: $('ddsMcoClockHint'),
  ddsCenterField: $('ddsCenterField'),
  ddsMinField: $('ddsMinField'),
  ddsMaxField: $('ddsMaxField'),
  kpUnit: $('kpUnit'),
  kiUnit: $('kiUnit'),
  kdUnit: $('kdUnit'),
  firmwareVersion: $('firmwareVersion'),
  configReadbackStatus: $('configReadbackStatus'),
  sourceChangeHint: $('sourceChangeHint'),
  trackingDirectionInput: $('trackingDirectionInput'),
  trackingDirectionStatus: $('trackingDirectionStatus'),
  referenceCaptureEdgeInput: $('referenceCaptureEdgeInput'),
  zcdCaptureEdgeInput: $('zcdCaptureEdgeInput'),
  jumperModal: $('jumperModal'),
  modalTitleText: $('modalTitleText'),
  modalSourceText: $('modalSourceText'),
  modalReqText: $('modalReqText'),
  modalCancelBtn: $('modalCancelBtn'),
  modalConfirmBtn: $('modalConfirmBtn'),
  kpInput: $('kpInput'),
  kiInput: $('kiInput'),
  kdInput: $('kdInput'),
  centerInput: $('centerInput'),
  targetInput: $('targetInput'),
  slewInput: $('slewInput'),
  slewUnit: $('slewUnit'),
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

const DDS_MCO_PRESETS = {
  0: { name: 'HSE 8 MHz (/1)', res: 0.029802, desc: 'Crystal 8.0 MHz, low phase jitter (125ns), res 0.0298 Hz' },
  1: { name: 'HSE 4 MHz (/2)', res: 0.014901, desc: 'Crystal 4.0 MHz, 2x finer resolution (0.0149 Hz)' },
  2: { name: 'HSE 2 MHz (/4)', res: 0.007451, desc: 'Crystal 2.0 MHz, high resolution (0.00745 Hz)' },
  3: { name: 'HSE 1.6 MHz (/5)', res: 0.005960, desc: 'Crystal 1.6 MHz, ultra-fine resolution (0.00596 Hz)' },
  4: { name: 'HSI 16 MHz (/1)', res: 0.059605, desc: 'Internal RC 16.0 MHz, fastest edge (62.5ns)' },
  5: { name: 'HSI 8 MHz (/2)', res: 0.029802, desc: 'Internal RC 8.0 MHz, standard resolution' },
  6: { name: 'HSI 4 MHz (/4)', res: 0.014901, desc: 'Internal RC 4.0 MHz, high resolution' },
};

function updateMcoClockHint(val) {
  if (!els.ddsMcoClockHint) return;
  const p = DDS_MCO_PRESETS[val];
  if (p) {
    els.ddsMcoClockHint.textContent = `Resolution: ±${p.res.toFixed(6)} Hz · ${p.desc}`;
  }
}

function onConfiguration(cfg) {
  state.config = cfg;
  els.firmwareVersion.textContent = cfg.FirmwareVersion || 'Unknown — GET_VERSION unanswered';
  els.configReadbackStatus.textContent = cfg.ReadbackComplete
    ? 'Configuration read back. Apply sends edited fields only.'
    : 'Readback incomplete or in progress. Apply is disabled until all required settings arrive.';
  if (!cfg.ReadbackComplete) {
    updateConfigControls();
    return;
  }
  if (els.generatorSourceInput) {
    els.generatorSourceInput.value = cfg.GeneratorSource !== undefined ? cfg.GeneratorSource : 0;
  }
  if (els.ddsMcoClockInput && cfg.DdsMcoClock !== undefined) {
    els.ddsMcoClockInput.value = String(cfg.DdsMcoClock);
    updateMcoClockHint(cfg.DdsMcoClock);
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
  updateGeneratorSourceUI(cfg.GeneratorSource ?? 2);

  els.kpInput.value = fmt(cfg.Kp, 7);
  els.kiInput.value = fmt(cfg.Ki, 7);
  els.kdInput.value = fmt(cfg.Kd, 7);
  els.centerInput.value = fmt(cfg.CenterVoltage, 3);
  els.targetInput.value = fmt(cfg.TargetPhase, 1);
  els.slewInput.value = fmt(cfg.MaxSlew, cfg.GeneratorSource === 1 ? 0 : 2);
  els.loopInput.value = cfg.LoopPeriodMs;
  els.thrInput.value = fmt(cfg.LockThresholdNs, 0);
  els.holdInput.value = cfg.LockHoldCycles;
  els.timeoutInput.value = cfg.LockMemoryTimeoutMs;
  els.streamInput.value = cfg.StreamPeriodMs;
  els.lossInput.value = cfg.SignalLossBehavior;
  els.referenceCaptureEdgeInput.value = cfg.ReferenceCaptureEdge == null ? '' : String(cfg.ReferenceCaptureEdge);
  els.zcdCaptureEdgeInput.value = cfg.ZcdCaptureEdge == null ? '' : String(cfg.ZcdCaptureEdge);
  els.trackingDirectionInput.value = cfg.TrackingDirection == null ? '' : String(cfg.TrackingDirection);
  els.trackingDirectionStatus.textContent = cfg.TrackingDirection == null
    ? 'Active direction: unknown / unsupported GET'
    : `Active direction: ${cfg.TrackingDirection === 1 ? 'Inverted' : 'Normal'}`;
  state.configFormBaseline = DpllConfig.snapshot(els);
  updateConfigControls();
  els.loopValue.textContent = cfg.LoopPeriodMs;

  els.manualFlag.hidden = state.telemetry ? state.telemetry.LockStatus !== 4 : !cfg.ManualMode;
  const srcNames = { 0: 'DAC (CD4046 VCO)', 1: 'DDS (AD9833)', 2: 'PWM Dither (TIM1 PA8)' };
  const srcName = srcNames[cfg.GeneratorSource] || 'DAC (CD4046 VCO)';
  const clkName = cfg.DdsMcoClockName || 'HSE 8 MHz (/1)';
  const resHz = (cfg.DdsResolutionHz || 0.029802).toFixed(6);
  log('ok', `Config loaded: Source=${srcName} · MCO Clock=${clkName} (Res: ${resHz} Hz) · Kp=${cfg.Kp} Ki=${cfg.Ki} Kd=${cfg.Kd} · center=${cfg.CenterVoltage} V · loop=${cfg.LoopPeriodMs} ms · loss=${cfg.SignalLossName}`);
}

const LOCK_STATES = {
  0: { text: 'NO REF', cls: 'error' },
  1: { text: 'WAIT ZCD', cls: 'error' },
  2: { text: 'TRACK', cls: 'track' },
  3: { text: 'LOCK', cls: 'lock' },
  4: { text: 'MANUAL', cls: 'manual' },
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
  state.connected = connected;
  updateConfigControls();
  els.saveCfgBtn.disabled = !connected;
  els.loadCfgBtn.disabled = !connected;
  if (els.resetEepromBtn) els.resetEepromBtn.disabled = !connected;
  if (els.manualDacSlider) els.manualDacSlider.disabled = !connected;
  if (els.manualDacInput) els.manualDacInput.disabled = !connected;
  if (els.manualSetBtn) els.manualSetBtn.disabled = !connected;
  if (els.manualDdsSlider) els.manualDdsSlider.disabled = !connected;
  if (els.manualDdsInput) els.manualDdsInput.disabled = !connected;
  if (els.manualDdsSetBtn) els.manualDdsSetBtn.disabled = !connected;
  els.startLogBtn.disabled = !connected || state.logging;
  els.armRawBtn.disabled = !connected;
  els.checkRawBtn.disabled = !connected;
  els.downloadRawBtn.disabled = !connected;
  if (!connected) els.rawCaptureStatus.textContent = 'Disconnected: diagnostic capture unavailable.';
  if (!connected) {
    state.telemetry = null;
    state.config = null;
    state.configFormBaseline = null;
    updateConfigControls();
    els.firmwareVersion.textContent = 'Unknown — device disconnected';
    els.configReadbackStatus.textContent = 'Connect and read configuration before applying settings.';
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
  els.manualFlag.hidden = t.LockStatus !== 4;

  if (t.EepromStatus === 1) {
    setEepromBadge('ok', 'EEPROM OK');
  } else {
    setEepromBadge('danger', 'EEPROM ERR');
  }

  const dds = DpllConfig.decodeDdsStatus(t.DdsStatus);
  const isDdsActive = dds.active;
  const ddsHwStatus = dds.hardware;
  // A zero flag on old firmware cannot establish support for direction readback.
  const directionKnown = dds.inverted || state.config?.TrackingDirection != null;
  els.trackingDirectionStatus.textContent = directionKnown
    ? `Active direction: ${dds.inverted ? 'Inverted' : 'Normal'} (telemetry)`
    : 'Active direction: unknown / unsupported GET';

  if (state.config?.GeneratorSource === 2) {
    setDdsBadge('ok', 'PWM DITHER (PA8)');
  } else if (isDdsActive) {
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
async function onApplyConfig() {
  if (!state.connected || state.configBusy) { log('warn', 'Not connected or configuration busy'); return; }
  let patch;
  try {
    patch = DpllConfig.buildPatch(DpllConfig.snapshot(els), state.configFormBaseline, state.config);
  } catch (err) {
    log('error', err.message);
    return;
  }
  if (Object.keys(patch).length === 0) {
    log('info', 'No configuration changes to apply');
    return;
  }
  state.configBusy = true;
  updateConfigControls();
  try {
    const confirmed = await call('ApplyConfiguration', patch);
    if (confirmed === true) {
      log('ok', Object.hasOwn(patch, 'GeneratorSource')
        ? 'Source changed and active gain bank read back. Edit gains in the displayed units, then Apply.'
        : 'Edited settings confirmed by firmware readback (RAM; not saved to EEPROM)');
    } else {
      log('warn', 'Apply was not confirmed. Read configuration before retrying.');
    }
  } finally {
    state.configBusy = false;
    updateConfigControls();
  }
}

function updateConfigControls() {
  const ready = state.connected && state.config?.ReadbackComplete === true && !state.configBusy;
  const sourcePending = ready && Number(els.generatorSourceInput.value) !== state.config.GeneratorSource;
  for (const [key, id, , , , optional] of DpllConfig.fields) {
    const field = els[id];
    if (field) field.disabled = !ready || (sourcePending && key !== 'GeneratorSource') ||
      (Boolean(optional) && state.config?.[key] == null);
  }
  els.applyCfgBtn.disabled = !ready;
  els.sourceChangeHint.textContent = sourcePending
    ? 'Source change pending: Apply switches source only, then reads its gain bank. Other unsaved edits will be replaced by that readback.'
    : 'Kp, Ki, Kd and slew belong to the active source. Change source and Apply before editing its gains.';
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

function onResetEeprom() {
  if (!state.connected) { log('warn', 'Not connected'); return; }
  const confirmed = confirm(
    'Reset EEPROM Configuration?\n\n' +
    'This will erase saved parameters from the 24LC16B EEPROM and revert the controller and DDS to factory baseline defaults.\n\n' +
    'Are you sure you want to proceed?'
  );
  if (!confirmed) return;
  call('ResetEepromConfiguration');
  log('warn', 'Resetting EEPROM to factory defaults…');
}

function onEepromActionResult(action, success) {
  if (action === 'save' || action === true) {
    if (success) {
      log('ok', 'EEPROM: Configuration successfully saved to 24LC16B!');
    } else {
      log('error', 'EEPROM: Failed to save configuration!');
    }
  } else if (action === 'reset') {
    if (success) {
      log('ok', 'EEPROM: Configuration successfully reset to factory defaults!');
    } else {
      log('error', 'EEPROM: Failed to reset configuration in EEPROM!');
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
  log('warn', 'Loop reset requested — restart from the configured center');
}

function onRunLoop() {
  call('RunLoop');
  log('info', 'Automatic control requested — watch the firmware state indicator');
}

function onShutdown() {
  call('ShutdownLoop');
  log('warn', 'Shutdown requested — monitor MANUAL state; Run or Reset resumes control');
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

function updateGeneratorSourceUI(source) {
  const isFreq = source === 1 || source === '1' || source === 2 || source === '2';
  const isDds = source === 1 || source === '1';
  const isPwm = source === 2 || source === '2';
  if (els.ddsMcoClockField) els.ddsMcoClockField.classList.toggle('field-highlight', isDds);
  if (els.ddsCenterField) els.ddsCenterField.classList.toggle('field-highlight', isFreq);
  if (els.ddsMinField) els.ddsMinField.classList.toggle('field-highlight', isFreq);
  if (els.ddsMaxField) els.ddsMaxField.classList.toggle('field-highlight', isFreq);
  if (els.kpUnit) els.kpUnit.textContent = isFreq ? '(Hz/ns)' : '(V/ns)';
  if (els.kiUnit) els.kiUnit.textContent = isFreq ? '(Hz/(ns·s))' : '(V/(ns·s))';
  if (els.kdUnit) els.kdUnit.textContent = isFreq ? '(Hz·s/ns)' : '(V·s/ns)';
  if (els.slewUnit) els.slewUnit.textContent = isFreq ? '(Hz/s)' : '(V/s)';
  if (els.slewInput) {
    els.slewInput.step = isFreq ? '100' : '0.1';
    els.slewInput.placeholder = isFreq ? '20000' : '30';
  }
  selectManualTab(isFreq ? 'dds' : 'dac');
}

if (els.ddsMcoClockInput) {
  els.ddsMcoClockInput.addEventListener('change', () => {
    updateMcoClockHint(parseInt(els.ddsMcoClockInput.value, 10));
  });
}

if (els.generatorSourceInput) {
  els.generatorSourceInput.addEventListener('change', () => {
    const selected = Number(els.generatorSourceInput.value);
    const prevSource = state.config?.GeneratorSource ?? 0;
    if (selected === 1 && prevSource !== 1 && els.jumperModal) {
      if (els.modalSourceText) els.modalSourceText.textContent = 'DDS (AD9833)';
      if (els.modalReqText) els.modalReqText.innerHTML = 'Ensure the physical resistor jumper on the PCB is routed to the <strong>AD9833 DDS output</strong>, NOT to the CD4046 VCO!';
      els.jumperModal.hidden = false;
    } else if (selected === 2 && prevSource !== 2 && els.jumperModal) {
      if (els.modalSourceText) els.modalSourceText.textContent = 'PWM Dither (STM32 TIM1 PA8)';
      if (els.modalReqText) els.modalReqText.innerHTML = 'Ensure <strong>PA8</strong> is connected to Gate Driver input AND loopback wire <strong>PA8 is connected to PA0</strong> (TIM2_CH1 REF input)!';
      els.jumperModal.hidden = false;
    }
    // Keep the displayed gain units tied to the last confirmed active source.
    updateConfigControls();
  });
}

if (els.modalCancelBtn) {
  els.modalCancelBtn.addEventListener('click', () => {
    if (els.jumperModal) els.jumperModal.hidden = true;
    els.generatorSourceInput.value = String(state.config?.GeneratorSource ?? 2);
    updateConfigControls();
    log('info', 'Generator source change cancelled');
  });
}

if (els.modalConfirmBtn) {
  els.modalConfirmBtn.addEventListener('click', () => {
    if (els.jumperModal) els.jumperModal.hidden = true;
    updateConfigControls();
    const sel = Number(els.generatorSourceInput.value);
    const name = sel === 2 ? 'PWM Dither (TIM1 PA8)' : 'DDS (AD9833)';
    log('warn', `${name} selected. Check hardware wiring/jumpers, then Apply to switch source and read its gains.`);
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
if (els.resetEepromBtn) els.resetEepromBtn.addEventListener('click', onResetEeprom);
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

// ---------------- Raw REF/ZCD diagnostic capture ----------------
// Only ONE control window is stored in STM32 RAM at a time. No client-side
// estimate is substituted for the true paired hardware timestamp records.
function rawInfoSummary(info) {
  if (!info) return 'No response from the firmware.';
  if (info.State === 0) return 'Idle - click Arm next window.';
  if (info.State === 1) return 'Armed - waiting for the next processed tracking window.';
  if (info.State === 2) {
    const pid = info.PidApplied ? 'PID applied' : 'FILTER HOLD (PID not applied)';
    return `Window ${info.WindowId}: ${info.SampleCount} paired samples, ${info.AcceptedCount} accepted, ${pid}; loop ${info.LoopPeriodMs} ms, phase ${Number(info.FilteredPhaseNs).toFixed(1)} ns, state ${info.LockStatus}; overflow=${info.Overflow}`;
  }
  return 'Unknown firmware response.';
}

async function onArmRawWindow() {
  if (!state.connected) return;
  els.armRawBtn.disabled = true;
  els.downloadRawBtn.disabled = true;
  els.rawCaptureStatus.textContent = 'Arming next processed tracking window...';
  try {
    const ok = await call('ArmRawWindow');
    els.rawCaptureStatus.textContent = ok ?
      'Armed. Run a sonication pulse, then click Check capture (after the pulse).' :
      'No ACK from firmware. Check the firmware version and connection.';
    if (ok) log('ok', 'Raw capture armed for one window');
  } finally {
    els.armRawBtn.disabled = !state.connected;
  }
}

async function onCheckRawWindow() {
  if (!state.connected) return;
  const info = await call('GetRawWindowInfo');
  els.rawCaptureStatus.textContent = rawInfoSummary(info);
  els.downloadRawBtn.disabled = !state.connected || !info || info.State !== 2;
}

async function onDownloadRawWindow() {
  if (!state.connected) return;
  els.downloadRawBtn.disabled = true;
  els.rawCaptureStatus.textContent = 'Fetching raw data pages from firmware. Best done after pulse...';
  const condition = els.rawConditionInput.value || 'Unknown';
  try {
    const csv = await call('ExportRawWindowCsv', condition);
    if (!csv || !csv.startsWith('Condition,WindowId,')) {
      els.rawCaptureStatus.textContent = 'Export failed: no valid captured window.';
      return;
    }
    const blob = new Blob([csv], { type: 'text/csv;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    const safe = condition.replace(/[^a-zA-Z0-9_-]+/g, '_').slice(0, 60);
    anchor.href = url;
    anchor.download = `DPLL_raw_ZCD_${safe}_${new Date().toISOString().replace(/[:.]/g, '-')}.csv`;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(url);
    const samples = csv.trimEnd().split('\n').length - 1;
    els.rawCaptureStatus.textContent = `Raw CSV downloaded: ${samples} samples from one control window.`;
    log('ok', `Raw ZCD CSV exported (${samples} samples)`);
  } finally {
    els.downloadRawBtn.disabled = !state.connected;
  }
}

els.armRawBtn.addEventListener('click', onArmRawWindow);
els.checkRawBtn.addEventListener('click', onCheckRawWindow);
els.downloadRawBtn.addEventListener('click', onDownloadRawWindow);

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
updateConfigControls();
initCharts();
initSignalR();
log('info', 'DPLL Ultrasonic DAQ UI ready');
