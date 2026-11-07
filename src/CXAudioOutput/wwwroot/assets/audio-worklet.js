/*
 * CX Audio Output — jitter-buffered PCM player.
 *
 * The server sends 20 ms chunks of interleaved little-endian Int16 stereo.
 * A slow fractional resampler corrects clock drift between the Windows PC and
 * the listening device without audible pitch jumps.
 */
class CxAudioProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this.outputRate = sampleRate;
    this.sourceRate = 48000;
    this.channels = 2;
    this.baseTargetMs = 240;
    this.targetMs = 240;
    this.boostMs = 0;
    this.ringFrames = 0;
    this.ring = null;
    this.writePos = 0;
    this.readPos = 0;
    this.primed = false;
    this.paused = true;
    this.playing = false;
    this.baseStep = 1;
    this.step = 1;
    this.rateBias = 0;
    this.underruns = 0;
    this.overruns = 0;
    this.lastStats = -1;
    this.fade = 0;
    this.lastLeft = 0;
    this.lastRight = 0;
    this.allocate(48000);
    this.port.onmessage = (event) => this.handleMessage(event.data);
  }

  allocate(rate) {
    const safeRate = Math.max(8000, Math.min(384000, Number(rate) || 48000));
    this.sourceRate = safeRate;
    this.ringFrames = Math.ceil(safeRate * 2.5);
    this.ring = new Float32Array(this.ringFrames * 2);
    this.writePos = 0;
    this.readPos = 0;
    this.primed = false;
    this.playing = false;
    this.fade = 0;
    this.baseStep = this.sourceRate / this.outputRate;
    this.step = this.baseStep;
  }

  reset() {
    if (this.ring) this.ring.fill(0);
    this.writePos = 0;
    this.readPos = 0;
    this.primed = false;
    this.playing = false;
    this.fade = 0;
    this.step = this.baseStep;
    this.lastLeft = 0;
    this.lastRight = 0;
  }

  handleMessage(message) {
    if (!message || typeof message.type !== 'string') return;
    switch (message.type) {
      case 'config': {
        const rate = Number(message.sampleRate) || 48000;
        const channels = Math.max(1, Math.min(8, Number(message.channels) || 2));
        const rateChanged = rate !== this.sourceRate || channels !== this.channels;
        this.channels = channels;
        this.baseTargetMs = this.clampTarget(Number(message.targetMs) || this.baseTargetMs);
        this.targetMs = this.baseTargetMs;
        this.boostMs = 0;
        if (rateChanged || !this.ring) {
          this.allocate(rate);
        } else {
          this.reset();
        }
        this.paused = false;
        this.port.postMessage({ type: 'ready', sourceRate: this.sourceRate });
        break;
      }
      case 'audio':
        if (message.buffer && this.ring) this.writeAudio(message.buffer);
        break;
      case 'target':
        this.baseTargetMs = this.clampTarget(Number(message.targetMs) || this.baseTargetMs);
        this.targetMs = this.baseTargetMs;
        break;
      case 'pause':
        this.paused = true;
        this.reset();
        this.port.postMessage({ type: 'paused' });
        break;
      case 'resume':
        this.paused = false;
        this.reset();
        break;
      case 'reset':
        this.reset();
        break;
    }
  }

  clampTarget(value) {
    return Math.max(45, Math.min(1200, value));
  }

  writeAudio(buffer) {
    if (!(buffer instanceof ArrayBuffer) || buffer.byteLength < 4) return;
    const view = new DataView(buffer);
    const bytesPerFrame = this.channels * 2;
    const frames = Math.floor(buffer.byteLength / bytesPerFrame);
    if (frames <= 0) return;

    let available = this.writePos - this.readPos;
    const targetFrames = ((this.targetMs + 60 + this.boostMs) / 1000) * this.sourceRate;
    const safetyCap = Math.min(this.ringFrames - frames, targetFrames + this.sourceRate * 0.35);
    if (available > safetyCap) {
      this.readPos = this.writePos - targetFrames;
      available = this.writePos - this.readPos;
      this.overruns += 1;
      this.boostMs = Math.max(0, this.boostMs - 20);
    }

    if (available + frames > this.ringFrames) {
      if (this.primed) {
        this.readPos = this.writePos + frames - this.ringFrames;
        this.overruns += 1;
        this.boostMs = Math.max(0, this.boostMs - 20);
      } else {
        this.readPos = this.writePos;
      }
    }

    let writeIndex = this.writePos % this.ringFrames;
    let offset = 0;
    for (let frame = 0; frame < frames; frame += 1) {
      let left;
      let right;
      if (this.channels === 1) {
        left = right = view.getInt16(offset, true) / 32768;
      } else {
        left = view.getInt16(offset, true) / 32768;
        right = view.getInt16(offset + 2, true) / 32768;
      }
      offset += bytesPerFrame;
      const pair = writeIndex * 2;
      this.ring[pair] = left;
      this.ring[pair + 1] = right;
      writeIndex += 1;
      if (writeIndex >= this.ringFrames) writeIndex = 0;
    }

    this.writePos += frames;
  }

  process(inputs, outputs) {
    const output = outputs[0];
    if (!output || output.length === 0) return true;
    const left = output[0];
    const right = output.length > 1 ? output[1] : output[0];
    const frames = left.length;

    if (this.paused || !this.ring) {
      this.reportStats();
      return true;
    }

    // A small fixed safety margin covers browser/audio-device scheduling
    // variance. It is still well below the "stable" profile and removes the
    // occasional one-block underrun that caused clicks on real hardware.
    const effectiveTargetMs = Math.min(900, this.targetMs + 60 + this.boostMs);
    const targetFrames = (effectiveTargetMs / 1000) * this.sourceRate;
    const primeFrames = targetFrames + this.sourceRate * 0.06;
    if (!this.primed) {
      if (this.writePos - this.readPos < primeFrames) {
        this.reportStats();
        return true;
      }
      this.primed = true;
      this.playing = true;
      this.fade = 0;
    }

    const fadeStep = 1 / Math.max(1, this.outputRate * 0.018);
    let underrun = false;

    for (let i = 0; i < frames; i += 1) {
      if (this.writePos - this.readPos <= 1) {
        underrun = true;
        this.underruns += 1;
        this.playing = false;
        this.primed = false;
        this.readPos = this.writePos;
        this.fade = 0;
        // Ramp the tail instead of cutting to zero: fewer clicks/pops.
        const tail = Math.min(96, frames - i);
        for (let j = 0; j < tail; j += 1) {
          const gain = 1 - (j + 1) / tail;
          left[i + j] = this.lastLeft * gain;
          if (right !== left) right[i + j] = this.lastRight * gain;
        }
        this.boostMs = Math.min(180, this.boostMs + 35);
        break;
      }

      const whole = Math.floor(this.readPos);
      const indexA = whole % this.ringFrames;
      const indexB = (indexA + 1) % this.ringFrames;
      const fraction = this.readPos - whole;
      const a = indexA * 2;
      const b = indexB * 2;
      const sampleLeft = this.ring[a] + (this.ring[b] - this.ring[a]) * fraction;
      const sampleRight = this.ring[a + 1] + (this.ring[b + 1] - this.ring[a + 1]) * fraction;

      this.lastLeft = sampleLeft;
      this.lastRight = sampleRight;
      this.fade = Math.min(1, this.fade + fadeStep);
      left[i] = sampleLeft * this.fade;
      if (right !== left) right[i] = sampleRight * this.fade;
      this.readPos += this.step;
    }

    const available = this.writePos - this.readPos;
    const error = (available - targetFrames) / Math.max(targetFrames, 1);
    // Integral term learns the listening device's actual clock rate. A
    // proportional-only controller can leave the buffer permanently below
    // target on devices whose audio clock differs slightly from 48 kHz.
    this.rateBias = Math.max(-0.03, Math.min(0.03, this.rateBias + error * 0.000018));
    const correction = Math.max(-0.03, Math.min(0.03, error * 0.02 + this.rateBias));
    this.step = this.baseStep * (1 + correction);

    this.reportStats();
    return true;
  }

  reportStats() {
    const now = currentTime;
    if (this.lastStats >= 0 && now - this.lastStats < 0.5) return;
    this.lastStats = now;
    const bufferedFrames = Math.max(0, this.writePos - this.readPos);
    this.port.postMessage({
      type: 'stats',
      bufferMs: Math.round((bufferedFrames / this.sourceRate) * 1000),
      targetMs: Math.round(Math.min(900, this.targetMs + 60 + this.boostMs)),
      underruns: this.underruns,
      overruns: this.overruns,
      playing: this.playing,
      primed: this.primed,
      step: Number(this.step.toFixed(6)),
      rateBias: Number(this.rateBias.toFixed(6)),
      sourceRate: this.sourceRate
    });
  }
}

registerProcessor('cx-audio', CxAudioProcessor);
