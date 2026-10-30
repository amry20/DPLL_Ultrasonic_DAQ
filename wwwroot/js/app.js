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
  sourceHardwareWarningBtn: $('sourceHardwareWarningBtn'),
  trackingDirectionInput: $('trackingDirectionInput'),
  trackingDirectionStatus: $('trackingDirectionStatus'),
  referenceCaptureEdgeInput: $('referenceCaptureEdgeInput'),
  zcdCaptureEdgeInput: $('zcdCaptureEdgeInput'),
  jumperModal: $('jumperModal'),
  modalTitleText: $('modalTitleText'),
  modalSourceText: $('modalSourceText'),
  modalReqText: $('modalReqText'),
  modalSubText: $('modalSubText'),
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
  calStartFreqInput: $('calStartFreqInput'),
  calStopFreqInput: $('calStopFreqInput'),
  calStepFreqInput: $('calStepFreqInput'),
  calSettleTimeInput: $('calSettleTimeInput'),
  startCalBtn: $('startCalBtn'),
  stopCalBtn: $('stopCalBtn'),
  downloadCalCsvBtn: $('downloadCalCsvBtn'),
  clearCalBtn: $('clearCalBtn'),
  calStatusChip: $('calStatusChip'),
  calProgressWrapper: $('calProgressWrapper'),
  calProgressText: $('calProgressText'),
  calLiveStats: $('calLiveStats'),
  calProgressFill: $('calProgressFill'),
  calTable: $('calTable'),
  calTableBody: $('calTableBody'),
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

  if (calState.running && typeof t.ReferenceFrequencyHz === 'number') {
    calState.samples.push(t.ReferenceFrequencyHz);
  }

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
    const src = cfg.GeneratorSource !== undefined ? cfg.GeneratorSource : 0;
    els.generatorSourceInput.value = src;
    currentConfirmedSource = Number(src);
  }
  if (els.ddsMcoClockInput && cfg.DdsMcoClock !== undefined) {
    els.ddsMcoClockInput.value = String(cfg.DdsMcoClock);
    updateMcoClockHint(cfg.DdsMcoClock);
  }
  if (els.ddsCenterInput) els.ddsCenterInput.value = cfg.DdsCenterFreq !== undefined ? cfg.DdsCenterFreq : 28000;
  if (els.ddsMinInput) els.ddsMinInput.value = cfg.DdsMinFreq !== undefined ? cfg.DdsMinFreq : 20000;
  if (els.ddsMaxInput) els.ddsMaxInput.value = cfg.DdsMaxFreq !== undefined ? cfg.DdsMaxFreq : 40000;
  if (els.calStartFreqInput && cfg.DdsMinFreq !== undefined && !els.calStartFreqInput.dataset.userEdited) {
    els.calStartFreqInput.value = cfg.DdsMinFreq;
  }
  if (els.calStopFreqInput && cfg.DdsMaxFreq !== undefined && !els.calStopFreqInput.dataset.userEdited) {
    els.calStopFreqInput.value = cfg.DdsMaxFreq;
  }
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
  if (els.startCalBtn) els.startCalBtn.disabled = !connected || calState.running;
  if (!connected && calState.running) {
    stopCalibrationSweep();
  }
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

let currentConfirmedSource = Number(els.generatorSourceInput?.value ?? 2);

function showSourceHardwareModal(targetSource) {
  if (!els.jumperModal) return;
  if (targetSource === 2) {
    if (els.modalSourceText) els.modalSourceText.textContent = 'PWM Dither (STM32 TIM1 PA8)';
    if (els.modalReqText) els.modalReqText.innerHTML =
      'Ensure hardware wiring and routing jumpers are correctly configured:<br>' +
      '• Pin <strong>PA8 (TIM1_CH1)</strong> must be connected to the Gate Driver input.<br>' +
      '• A loopback wire/jumper from <strong>PA8 must be connected to PA0 (TIM2_CH1 REF input)</strong> for phase feedback.<br>' +
      '• Physical resistor/jumper on the PCB must route <strong>PA8 (PWM Dither)</strong>, NOT AD9833 DDS or CD4046 VCO!';
    if (els.modalSubText) els.modalSubText.textContent =
      'Operating without routing PA8 to the gate driver and loopback wire to PA0 will leave the transducer without driving signals and break feedback tracking.';
    els.jumperModal.hidden = false;
  } else if (targetSource === 1) {
    if (els.modalSourceText) els.modalSourceText.textContent = 'DDS (AD9833)';
    if (els.modalReqText) els.modalReqText.innerHTML =
      'Ensure the physical resistor jumper on the PCB is routed to the <strong>AD9833 DDS output</strong>, NOT to the CD4046 VCO or TIM1 PA8!';
    if (els.modalSubText) els.modalSubText.textContent =
      'Operating without routing the hardware connections properly will leave the transducer without driving signals and break feedback tracking.';
    els.jumperModal.hidden = false;
  } else if (targetSource === 0) {
    if (els.modalSourceText) els.modalSourceText.textContent = 'DAC (CD4046 External VCO)';
    if (els.modalReqText) els.modalReqText.innerHTML =
      'Ensure DAC output is connected to the CD4046 VCO frequency control input and PCB selector jumper is routed to the CD4046 output!';
    if (els.modalSubText) els.modalSubText.textContent =
      'Operating without routing the hardware connections properly will leave the transducer without driving signals and break feedback tracking.';
    els.jumperModal.hidden = false;
  }
}

if (els.generatorSourceInput) {
  els.generatorSourceInput.addEventListener('change', () => {
    const selected = Number(els.generatorSourceInput.value);
    if (selected !== currentConfirmedSource) {
      showSourceHardwareModal(selected);
    }
    // Keep the displayed gain units tied to the last confirmed active source.
    updateConfigControls();
  });
}

if (els.sourceHardwareWarningBtn) {
  els.sourceHardwareWarningBtn.addEventListener('click', () => {
    const selected = Number(els.generatorSourceInput?.value ?? currentConfirmedSource);
    showSourceHardwareModal(selected);
  });
}

if (els.modalCancelBtn) {
  els.modalCancelBtn.addEventListener('click', () => {
    if (els.jumperModal) els.jumperModal.hidden = true;
    els.generatorSourceInput.value = String(currentConfirmedSource);
    updateConfigControls();
    log('info', 'Generator source change cancelled');
  });
}

if (els.modalConfirmBtn) {
  els.modalConfirmBtn.addEventListener('click', () => {
    if (els.jumperModal) els.jumperModal.hidden = true;
    currentConfirmedSource = Number(els.generatorSourceInput.value);
    updateConfigControls();
    const sel = currentConfirmedSource;
    const names = { 0: 'DAC (CD4046)', 1: 'DDS (AD9833)', 2: 'PWM Dither (TIM1 PA8)' };
    const name = names[sel] || 'Unknown';
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

// ---------------- Frequency Calibration & Sweep ----------------
const calState = {
  running: false,
  aborting: false,
  results: [],
  samples: [],
};

function setCalBadge(status) {
  if (!els.calStatusChip) return;
  els.calStatusChip.className = 'chip';
  switch (status) {
    case 'running':
      els.calStatusChip.classList.add('chip-primary');
      els.calStatusChip.textContent = 'Sweeping…';
      break;
    case 'complete':
      els.calStatusChip.classList.add('chip-success');
      els.calStatusChip.textContent = 'Complete';
      break;
    case 'aborted':
      els.calStatusChip.classList.add('chip-warn');
      els.calStatusChip.textContent = 'Stopped';
      break;
    default:
      els.calStatusChip.textContent = 'Idle';
      break;
  }
}

function clearCalibrationResults() {
  if (calState.running) return;
  calState.results = [];
  calState.samples = [];
  if (els.calTableBody) els.calTableBody.innerHTML = '';
  if (els.calProgressWrapper) els.calProgressWrapper.hidden = true;
  if (els.calProgressFill && els.calProgressFill.style) els.calProgressFill.style.width = '0%';
  if (els.calProgressText) els.calProgressText.textContent = 'Step 0 of 0 (0%)';
  if (els.calLiveStats) els.calLiveStats.textContent = 'Target: — | Measured: — | Dev: —';
  if (els.downloadCalCsvBtn) els.downloadCalCsvBtn.disabled = true;
  if (els.clearCalBtn) els.clearCalBtn.disabled = true;
  setCalBadge('idle');
  log('info', 'Calibration results cleared');
}

function renderCalTableRow(item) {
  if (!els.calTableBody) return;
  const tr = document.createElement('tr');

  const devClass = Math.abs(item.deviation) <= 2 ? 'cal-dev-good'
                 : Math.abs(item.deviation) <= 10 ? 'cal-dev-warn'
                 : 'cal-dev-bad';

  const sign = item.deviation > 0 ? '+' : '';
  const errSign = item.errorPct > 0 ? '+' : '';

  tr.innerHTML = `
    <td>${item.step}</td>
    <td class="mono">${fmt(item.target, 2)}</td>
    <td class="mono">${fmt(item.measured, 2)}</td>
    <td class="mono ${devClass}">${sign}${fmt(item.deviation, 2)}</td>
    <td class="mono ${devClass}">${errSign}${fmt(item.errorPct, 4)}%</td>
    <td class="mono">${item.settleTimeMs}</td>
  `;

  els.calTableBody.appendChild(tr);
  if (els.calTableBody.parentElement) {
    els.calTableBody.parentElement.scrollTop = els.calTableBody.parentElement.scrollHeight;
  }
}

async function startCalibrationSweep() {
  if (calState.running) return;
  if (!state.connected) {
    log('warn', 'Cannot start calibration: device not connected');
    return;
  }

  const startFreq = parseFloat(els.calStartFreqInput ? els.calStartFreqInput.value : 0);
  const stopFreq = parseFloat(els.calStopFreqInput ? els.calStopFreqInput.value : 0);
  const stepSize = parseFloat(els.calStepFreqInput ? els.calStepFreqInput.value : 0);
  const settleTime = parseInt(els.calSettleTimeInput ? els.calSettleTimeInput.value : 0, 10);

  if (isNaN(startFreq) || startFreq <= 0) {
    log('error', 'Calibration Error: Start frequency must be positive');
    return;
  }
  if (isNaN(stopFreq) || stopFreq < startFreq) {
    log('error', 'Calibration Error: Stop frequency must be >= Start frequency');
    return;
  }
  if (isNaN(stepSize) || stepSize <= 0) {
    log('error', 'Calibration Error: Step size must be positive');
    return;
  }
  if (isNaN(settleTime) || settleTime < 50) {
    log('error', 'Calibration Error: Settle time must be at least 50 ms');
    return;
  }

  const points = [];
  for (let f = startFreq; f <= stopFreq + 1e-4; f += stepSize) {
    points.push(Math.round(f * 100) / 100);
  }
  if (points.length === 0) {
    log('error', 'Calibration Error: No frequency steps calculated');
    return;
  }

  calState.running = true;
  calState.aborting = false;
  calState.results = [];
  calState.samples = [];

  if (els.calTableBody) els.calTableBody.innerHTML = '';
  if (els.calProgressWrapper) els.calProgressWrapper.hidden = false;
  if (els.startCalBtn) els.startCalBtn.hidden = true;
  if (els.stopCalBtn) {
    els.stopCalBtn.hidden = false;
    els.stopCalBtn.disabled = false;
  }
  if (els.downloadCalCsvBtn) els.downloadCalCsvBtn.disabled = true;
  if (els.clearCalBtn) els.clearCalBtn.disabled = true;
  if (els.calStartFreqInput) els.calStartFreqInput.disabled = true;
  if (els.calStopFreqInput) els.calStopFreqInput.disabled = true;
  if (els.calStepFreqInput) els.calStepFreqInput.disabled = true;
  if (els.calSettleTimeInput) els.calSettleTimeInput.disabled = true;

  setCalBadge('running');
  log('info', `Starting frequency calibration: ${points.length} steps (${startFreq} Hz → ${stopFreq} Hz, step: ${stepSize} Hz, settle: ${settleTime} ms)`);

  const total = points.length;

  try {
    for (let i = 0; i < total; i++) {
      if (calState.aborting || !state.connected) {
        log('warn', 'Calibration aborted by user or connection lost');
        break;
      }

      const targetFreq = points[i];
      const stepNum = i + 1;
      const pct = Math.round((stepNum / total) * 100);

      if (els.calProgressText) {
        els.calProgressText.textContent = `Step ${stepNum} of ${total} (${pct}%)`;
      }
      if (els.calProgressFill && els.calProgressFill.style) {
        els.calProgressFill.style.width = `${pct}%`;
      }

      calState.samples = [];

      try {
        await call('SetManualFrequency', targetFreq);
      } catch (err) {
        log('error', `Failed to set manual frequency ${targetFreq} Hz: ${err.message}`);
        break;
      }

      const startTime = performance.now();
      while (performance.now() - startTime < settleTime) {
        if (calState.aborting) break;
        await new Promise(r => setTimeout(r, 20));
      }

      if (calState.aborting) {
        log('warn', 'Calibration aborted during stabilization');
        break;
      }

      let measuredFreq = 0;
      if (calState.samples.length > 0) {
        const slice = calState.samples.slice(-5);
        const sum = slice.reduce((a, b) => a + b, 0);
        measuredFreq = sum / slice.length;
      } else if (state.telemetry && typeof state.telemetry.ReferenceFrequencyHz === 'number') {
        measuredFreq = state.telemetry.ReferenceFrequencyHz;
      }

      const deviation = measuredFreq - targetFreq;
      const errorPct = targetFreq !== 0 ? (deviation / targetFreq) * 100 : 0;

      const item = {
        step: stepNum,
        target: targetFreq,
        measured: measuredFreq,
        deviation: deviation,
        errorPct: errorPct,
        settleTimeMs: settleTime,
        timestamp: new Date().toISOString()
      };

      calState.results.push(item);
      renderCalTableRow(item);

      if (els.calLiveStats) {
        const sign = deviation > 0 ? '+' : '';
        const errSign = errorPct > 0 ? '+' : '';
        els.calLiveStats.textContent = `Target: ${fmt(targetFreq, 2)} Hz | Measured: ${fmt(measuredFreq, 2)} Hz | Dev: ${sign}${fmt(deviation, 2)} Hz (${errSign}${fmt(errorPct, 4)}%)`;
      }
    }
  } catch (err) {
    log('error', `Calibration sweep encountered error: ${err.message}`);
  } finally {
    const wasAborted = calState.aborting || calState.results.length < total;
    calState.running = false;
    calState.aborting = false;

    if (els.startCalBtn) {
      els.startCalBtn.hidden = false;
      els.startCalBtn.disabled = !state.connected;
    }
    if (els.stopCalBtn) {
      els.stopCalBtn.hidden = true;
    }
    if (els.downloadCalCsvBtn) {
      els.downloadCalCsvBtn.disabled = calState.results.length === 0;
    }
    if (els.clearCalBtn) {
      els.clearCalBtn.disabled = calState.results.length === 0;
    }
    if (els.calStartFreqInput) els.calStartFreqInput.disabled = false;
    if (els.calStopFreqInput) els.calStopFreqInput.disabled = false;
    if (els.calStepFreqInput) els.calStepFreqInput.disabled = false;
    if (els.calSettleTimeInput) els.calSettleTimeInput.disabled = false;

    setCalBadge(wasAborted ? 'aborted' : 'complete');
    if (!wasAborted) {
      log('ok', `Calibration completed successfully (${calState.results.length} points). You can now download the CSV report.`);
    }
  }
}

function stopCalibrationSweep() {
  if (!calState.running) return;
  calState.aborting = true;
  if (els.stopCalBtn) els.stopCalBtn.disabled = true;
  log('info', 'Stopping calibration sweep…');
}

function getGeneratorSourceName() {
  if (!els.generatorSourceInput) return 'Unknown';
  switch (parseInt(els.generatorSourceInput.value, 10)) {
    case 0: return 'CD4046 / DAC VCO (Source 0)';
    case 1: return 'AD9833 DDS (Source 1)';
    case 2: return 'STM32 TIM1 PWM Dither (Source 2)';
    default: return `Source ${els.generatorSourceInput.value}`;
  }
}

function onDownloadCalibrationCsv() {
  if (calState.results.length === 0) {
    log('warn', 'No calibration results to export');
    return;
  }
  const now = new Date();
  const timestampStr = now.toISOString().replace(/[:.]/g, '-').slice(0, 19);
  const filename = `frequency_calibration_${timestampStr}.csv`;

  const lines = [
    `# DPLL Ultrasonic Frequency Calibration Report`,
    `# Export Date: ${now.toISOString()}`,
    `# Generator Source: ${getGeneratorSourceName()}`,
    `# Target Range: ${els.calStartFreqInput ? els.calStartFreqInput.value : ''} Hz - ${els.calStopFreqInput ? els.calStopFreqInput.value : ''} Hz (Step: ${els.calStepFreqInput ? els.calStepFreqInput.value : ''} Hz)`,
    `# Settle Time: ${els.calSettleTimeInput ? els.calSettleTimeInput.value : ''} ms`,
    `# Total Steps: ${calState.results.length}`,
    `Step,Target_Frequency_Hz,Measured_Frequency_Hz,Deviation_Hz,Error_Percent,Settle_Time_ms,Timestamp`
  ];

  for (const r of calState.results) {
    lines.push([
      r.step,
      r.target.toFixed(2),
      r.measured.toFixed(2),
      r.deviation.toFixed(2),
      r.errorPct.toFixed(4),
      r.settleTimeMs,
      `"${r.timestamp}"`
    ].join(','));
  }

  const csvContent = lines.join('\r\n');
  const blob = new Blob([csvContent], { type: 'text/csv;charset=utf-8;' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);

  log('ok', `Calibration CSV exported: ${filename}`);
}

if (els.startCalBtn) els.startCalBtn.addEventListener('click', startCalibrationSweep);
if (els.stopCalBtn) els.stopCalBtn.addEventListener('click', stopCalibrationSweep);
if (els.downloadCalCsvBtn) els.downloadCalCsvBtn.addEventListener('click', onDownloadCalibrationCsv);
if (els.clearCalBtn) els.clearCalBtn.addEventListener('click', clearCalibrationResults);

['calStartFreqInput', 'calStopFreqInput', 'calStepFreqInput', 'calSettleTimeInput'].forEach(id => {
  if (els[id]) {
    els[id].addEventListener('input', () => { els[id].dataset.userEdited = 'true'; });
  }
});

// ---------------- Boot ----------------
updateConfigControls();
initCharts();
initSignalR();
log('info', 'DPLL Ultrasonic DAQ UI ready');
