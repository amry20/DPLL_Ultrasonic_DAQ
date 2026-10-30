/* Shared browser/Node logic: only edited form values become firmware writes. */
(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.DpllConfig = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';
  const FLOAT_MAX = 3.4028234663852886e38;
  const fields = [
    ['GeneratorSource', 'generatorSourceInput', 0, 2, true],
    ['Kp', 'kpInput', 0, FLOAT_MAX],
    ['Ki', 'kiInput', 0, FLOAT_MAX],
    ['Kd', 'kdInput', 0, FLOAT_MAX],
    ['CenterVoltage', 'centerInput', 0, 3.3],
    ['TargetPhase', 'targetInput', -FLOAT_MAX, FLOAT_MAX],
    ['MaxSlew', 'slewInput', 1.401298464324817e-45, FLOAT_MAX],
    ['LoopPeriodMs', 'loopInput', 1, 1000, true],
    ['LockThresholdNs', 'thrInput', 1.401298464324817e-45, FLOAT_MAX],
    ['LockHoldCycles', 'holdInput', 1, 4294967295, true],
    ['LockMemoryTimeoutMs', 'timeoutInput', 0, 4294967295, true],
    ['StreamPeriodMs', 'streamInput', 1, 65535, true],
    ['SignalLossBehavior', 'lossInput', 0, 2, true],
    ['DdsMcoClock', 'ddsMcoClockInput', 0, 6, true],
    ['DdsCenterFreq', 'ddsCenterInput', 1000, 500000],
    ['DdsMinFreq', 'ddsMinInput', 1000, 500000],
    ['DdsMaxFreq', 'ddsMaxInput', 1000, 500000],
    ['ReferenceCaptureEdge', 'referenceCaptureEdgeInput', 0, 2, true, true],
    ['ZcdCaptureEdge', 'zcdCaptureEdgeInput', 0, 2, true, true],
    ['TrackingDirection', 'trackingDirectionInput', 0, 1, true, true],
  ];
  const gainFields = ['Kp', 'Ki', 'Kd', 'MaxSlew'];

  function snapshot(elements) {
    return Object.fromEntries(fields.map(([key, id]) => [key, String(elements[id]?.value ?? '')]));
  }

  function changed(value, previous) {
    if (value === previous) return false;
    return value === '' || previous === '' || Number(value) !== Number(previous);
  }

  function buildPatch(values, baseline, config) {
    if (!config?.ReadbackComplete || !baseline)
      throw new Error('Read a complete configuration before applying changes.');
    const source = Number(values.GeneratorSource);
    if (values.GeneratorSource === '' || ![0, 1, 2].includes(source))
      throw new Error('Select a valid generator source.');
    // A source switch is its own transaction. Never carry numerical gains or
    // slew from the old unit system into the new source's bank.
    if (source !== config.GeneratorSource)
      return { GeneratorSource: source, ExpectedGeneratorSource: config.GeneratorSource };

    const patch = {};
    for (const [key, , min, max, integer, optional] of fields) {
      if (key === 'GeneratorSource' || !changed(values[key], baseline[key])) continue;
      const value = Number(values[key]);
      if (values[key] === '' || !Number.isFinite(value) || value < min || value > max ||
          (integer && !Number.isInteger(value)))
        throw new Error(`${key}: enter ${integer ? 'an integer' : 'a finite number'} between ${min} and ${max}.`);
      if (optional && config[key] == null)
        throw new Error(`${key}: firmware readback is unknown or unsupported.`);
      patch[key] = value;
    }
    if (gainFields.some(key => Object.hasOwn(patch, key)))
      patch.ExpectedGeneratorSource = config.GeneratorSource;
    const min = patch.DdsMinFreq ?? config.DdsMinFreq;
    const max = patch.DdsMaxFreq ?? config.DdsMaxFreq;
    if (min >= max) throw new Error('DDS minimum must be below its maximum.');
    if (Object.hasOwn(patch, 'DdsCenterFreq') && (patch.DdsCenterFreq < min || patch.DdsCenterFreq > max))
      throw new Error('DDS center must be within the configured bounds.');
    return patch;
  }

  function decodeDdsStatus(value) {
    const hardware = value & 0x3F;
    return {
      hardware,
      ready: hardware === 1 || hardware === 2,
      verified: hardware === 2,
      inverted: (value & 0x40) !== 0,
      active: (value & 0x80) !== 0,
    };
  }
  return { fields, snapshot, buildPatch, decodeDdsStatus };
});
