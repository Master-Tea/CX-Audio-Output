(() => {
  'use strict';

  const $ = (selector, root = document) => root.querySelector(selector);
  const $$ = (selector, root = document) => Array.from(root.querySelectorAll(selector));

  const els = {
    body: document.body,
    joinView: $('#join-view'),
    playerView: $('#player-view'),
    joinForm: $('#join-form'),
    deviceName: $('#device-name'),
    codeField: $('#code-field'),
    accessCode: $('#access-code'),
    linkedNote: $('#linked-note'),
    changeCode: $('#change-code'),
    startButton: $('#start-button'),
    startLabel: $('#start-label'),
    formNote: $('#form-note'),
    joinHostName: $('#join-host-name'),
    testButton: $('#test-button'),
    rxStatus: $('#rx-status'),
    playerNetwork: $('#player-network'),
    hostName: $('#receiver-host-name'),
    source: $('#receiver-source'),
    orbState: $('#orb-state'),
    localMute: $('#local-mute'),
    localVolume: $('#local-volume'),
    localVolumeValue: $('#local-volume-value'),
    fullscreen: $('#fullscreen-button'),
    buffer: $('#rx-buffer'),
    rtt: $('#rx-rtt'),
    engineChip: $('#engine-chip'),
    detailsButton: $('#details-button'),
    footer: $('#rx-footer'),
    overlay: $('#rx-overlay'),
    overlayIcon: $('#overlay-icon'),
    overlayEyebrow: $('#overlay-eyebrow'),
    overlayTitle: $('#overlay-title'),
    overlayCopy: $('#overlay-copy'),
    overlayAction: $('#overlay-action'),
    overlayDetails: $('#overlay-details'),
    detailsDialog: $('#details-dialog'),
    closeDetails: $('#close-details'),
    detailHost: $('#detail-host'),
    detailRate: $('#detail-rate'),
    detailProfile: $('#detail-profile'),
    detailBuffer: $('#detail-buffer'),
    detailRtt: $('#detail-rtt'),
    detailEngine: $('#detail-engine'),
    detailUnderruns: $('#detail-underruns'),
    disconnect: $('#disconnect-button'),
    reconnect: $('#reconnect-button'),
    toast: $('#rx-toast'),
    orbCanvas: $('#orb-canvas')
  };

  const R = {
    code: '',
    codeFromUrl: false,
    name: '',
    platform: detectPlatform(),
    ws: null,
    socketGeneration: 0,
    connected: false,
    gotHello: false,
    started: false,
    intentional: false,
    hostName: '你的电脑',
    captureStatus: 'starting',
    streamPlaying: false,
    masterVolume: 100,
    masterMuted: false,
    localVolume: 100,
    localMuted: false,
    targetMs: 240,
    profile: 'balanced',
    sampleRate: 48000,
    rtt: 0,
    stats: { bufferMs: 0, underruns: 0, overruns: 0, playing: false, primed: false },
    ctx: null,
    player: null,
    audioEngine: 'unknown',
    gain: null,
    analyser: null,
    analyserData: null,
    reconnectTimer: 0,
    reconnectAttempts: 0,
    startTime: 0,
    overlayHandler: null,
    overlayDismissible: false,
    toastTimer: 0,
    identifyUntil: 0,
    wakeLock: null,
    volumeSendTimer: 0,
    lastStatsSent: 0,
    canvasReady: false,
    reducedMotion: false,
    lastOrbDraw: 0
  };

  init();

  function init() {
    const params = new URLSearchParams(location.search);
    const urlCode = (params.get('code') || '').replace(/\D/g, '').slice(0, 6);
    R.codeFromUrl = urlCode.length === 6;
    R.code = urlCode || (localStorage.getItem('cx.code') || '').replace(/\D/g, '').slice(0, 6);
    R.name = localStorage.getItem('cx.name') || detectDeviceName();
    R.localVolume = clampNumber(localStorage.getItem('cx.volume'), 100, 0, 100);
    R.localMuted = localStorage.getItem('cx.muted') === '1';
    R.reducedMotion = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

    els.deviceName.value = R.name;
    els.accessCode.value = R.code;
    updateVolumeUi();
    applyCodeUi();
    bindEvents();
    initCanvas();
    requestAnimationFrame(drawOrb);

    if (R.codeFromUrl) {
      validateCode(R.code);
    } else if (R.code.length === 6) {
      validateCode(R.code);
    }

    window.setInterval(sendStats, 2000);
    window.setInterval(updateConnectionAge, 5000);
  }

  function bindEvents() {
    els.deviceName.addEventListener('input', () => {
      R.name = els.deviceName.value.trim().slice(0, 32);
      localStorage.setItem('cx.name', R.name);
    });

    els.accessCode.addEventListener('input', () => {
      let value = els.accessCode.value.replace(/\D/g, '').slice(0, 6);
      els.accessCode.value = value;
      R.code = value;
      R.codeFromUrl = false;
      setFormNote('点击后请允许浏览器播放声音。');
    });

    els.accessCode.addEventListener('keydown', (event) => {
      if (event.key === 'Enter') {
        event.preventDefault();
        els.joinForm.requestSubmit();
      }
    });

    els.changeCode.addEventListener('click', () => {
      R.code = '';
      R.codeFromUrl = false;
      els.accessCode.value = '';
      applyCodeUi();
      els.accessCode.focus();
      setFormNote('输入电脑控制台显示的 6 位访问码。');
    });

    els.joinForm.addEventListener('submit', (event) => {
      event.preventDefault();
      startListening();
    });

    els.testButton.addEventListener('click', testSpeaker);
    els.localMute.addEventListener('click', () => {
      R.localMuted = !R.localMuted;
      localStorage.setItem('cx.muted', R.localMuted ? '1' : '0');
      updateVolumeUi();
      applyGain();
      updatePlaybackUi();
      sendJson({ type: 'muted', value: R.localMuted });
    });

    els.localVolume.addEventListener('input', () => {
      R.localVolume = Number(els.localVolume.value);
      localStorage.setItem('cx.volume', String(R.localVolume));
      els.localVolumeValue.textContent = String(R.localVolume);
      paintRange(els.localVolume);
      applyGain();
      clearTimeout(R.volumeSendTimer);
      R.volumeSendTimer = window.setTimeout(() => {
        sendJson({ type: 'volume', value: R.localVolume });
      }, 120);
    });

    els.localVolume.addEventListener('change', () => {
      sendJson({ type: 'volume', value: R.localVolume });
    });

    els.playerView.addEventListener('click', () => {
      if (R.ctx && R.ctx.state === 'suspended') {
        R.ctx.resume().catch(() => {});
      }
    });

    els.fullscreen.addEventListener('click', toggleFullscreen);
    els.detailsButton.addEventListener('click', openDetails);
    els.overlayDetails.addEventListener('click', openDetails);
    els.closeDetails.addEventListener('click', closeDetails);
    els.detailsDialog.addEventListener('close', () => {});
    els.detailsDialog.addEventListener('click', (event) => {
      if (event.target !== els.detailsDialog) return;
      const rect = els.detailsDialog.getBoundingClientRect();
      if (event.clientX < rect.left || event.clientX > rect.right ||
          event.clientY < rect.top || event.clientY > rect.bottom) {
        closeDetails();
      }
    });

    els.disconnect.addEventListener('click', () => {
      closeDetails();
      disconnectAndReturn();
    });

    els.reconnect.addEventListener('click', () => {
      closeDetails();
      hideOverlay();
      reconnectNow();
    });

    els.overlayAction.addEventListener('click', () => {
      const handler = R.overlayHandler;
      if (typeof handler === 'function') {
        handler();
      } else if (R.overlayDismissible) {
        hideOverlay();
      }
    });

    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'visible' && R.started) {
        if (R.ctx && R.ctx.state === 'suspended') {
          R.ctx.resume().catch(() => {});
        }
        requestWakeLock();
        if (!R.connected && !R.intentional) {
          reconnectNow();
        }
      }
    });

    window.addEventListener('online', () => {
      if (R.started && !R.connected && !R.intentional) reconnectNow();
    });

    window.addEventListener('pagehide', releaseWakeLock);
    window.addEventListener('beforeunload', () => {
      if (R.ws) {
        try { R.ws.close(); } catch { /* ignored */ }
      }
    });
  }

  function applyCodeUi() {
    const hasCode = R.code.length === 6;
    const linked = hasCode && R.codeFromUrl;
    els.codeField.hidden = linked;
    els.linkedNote.hidden = !linked;
    els.accessCode.value = hasCode ? R.code : '';
    if (linked) {
      els.linkedNote.querySelector('span').innerHTML = '<i></i>已通过链接配对';
    }
  }

  async function validateCode(code) {
    try {
      const response = await fetch('/api/join?code=' + encodeURIComponent(code), {
        headers: { Accept: 'application/json' }
      });
      if (response.ok) {
        const data = await response.json();
        if (data.serverName) {
          R.hostName = data.serverName;
          els.joinHostName.textContent = data.serverName;
        }
        return;
      }

      if (response.status === 403) {
        setFormNote('访问码不正确，请确认电脑控制台上的 6 位数字。', true);
        R.code = '';
        R.codeFromUrl = false;
        applyCodeUi();
      } else if (response.status === 429) {
        setFormNote('尝试次数过多，请稍等一分钟再试。', true);
      }
    } catch {
      // The WebSocket attempt will surface a clearer connection error.
    }
  }

  async function startListening() {
    if (R.started) return;
    const name = els.deviceName.value.trim().slice(0, 32) || detectDeviceName();
    const code = (els.accessCode.value || R.code).replace(/\D/g, '').slice(0, 6);
    if (code.length !== 6) {
      setFormNote('请输入电脑控制台上的 6 位访问码。', true);
      els.accessCode.focus();
      return;
    }

    R.name = name;
    R.code = code;
    localStorage.setItem('cx.name', name);
    localStorage.setItem('cx.code', code);
    els.deviceName.value = name;
    els.accessCode.value = code;
    setLoading(true);
    setFormNote('正在连接电脑并准备音频…');

    try {
      await setupAudio();
    } catch (error) {
      setLoading(false);
      setFormNote(audioErrorMessage(error), true);
      return;
    }

    R.started = true;
    R.gotHello = false;
    R.intentional = false;
    connectAudio();
  }

  async function setupAudio() {
    if (R.ctx && R.ctx.state !== 'closed') {
      await resumeContext(R.ctx);
      return;
    }

    const AudioContextClass = window.AudioContext || window.webkitAudioContext;
    if (!AudioContextClass) throw new Error('这个浏览器不支持 Web Audio。');

    const context = new AudioContextClass({ latencyHint: 'interactive' });
    // Safari/iOS needs resume() to be called directly inside the user gesture.
    // Keep this early call alive while the player is prepared.
    void context.resume().catch(() => {});

    try {
      const player = await createAudioPlayer(context);
      const gain = context.createGain();
      gain.gain.value = 0;
      const analyser = context.createAnalyser();
      analyser.fftSize = 1024;
      analyser.smoothingTimeConstant = 0.83;
      player.node.connect(gain);
      gain.connect(analyser);
      analyser.connect(context.destination);
      player.onMessage = handlePlayerMessage;

      R.ctx = context;
      R.player = player;
      R.audioEngine = player.engine;
      R.gain = gain;
      R.analyser = analyser;
      R.analyserData = new Uint8Array(analyser.frequencyBinCount);
      R.canvasReady = true;
      updateEngineUi();

      await resumeContext(context);
    } catch (error) {
      try { await context.close(); } catch { /* ignored */ }
      throw error;
    }
  }

  async function createAudioPlayer(context) {
    const worklet = await createWorkletPlayer(context);
    if (worklet) return worklet;
    return createScriptProcessorPlayer(context);
  }

  async function createWorkletPlayer(context) {
    if (!context.audioWorklet || typeof window.AudioWorkletNode !== 'function') return null;
    try {
      await context.audioWorklet.addModule('/assets/audio-worklet.js?v=1.2.0');
      const node = new window.AudioWorkletNode(context, 'cx-audio', {
        numberOfInputs: 0,
        numberOfOutputs: 1,
        outputChannelCount: [2]
      });
      const player = {
        engine: 'worklet',
        node,
        onMessage: null,
        sendAudio(buffer) {
          node.port.postMessage({ type: 'audio', buffer }, [buffer]);
        },
        setConfig(sampleRate, channels, targetMs) {
          node.port.postMessage({ type: 'config', sampleRate, channels, targetMs });
        },
        setTarget(targetMs) {
          node.port.postMessage({ type: 'target', targetMs });
        },
        pause() {
          node.port.postMessage({ type: 'pause' });
        },
        resume() {
          node.port.postMessage({ type: 'resume' });
        },
        reset() {
          node.port.postMessage({ type: 'reset' });
        },
        dispose() {
          try { node.port.close(); } catch { /* ignored */ }
          try { node.disconnect(); } catch { /* ignored */ }
        }
      };
      node.port.onmessage = (event) => {
        if (player.onMessage) player.onMessage(event.data);
      };
      node.port.start();
      return player;
    } catch {
      return null;
    }
  }

  function createScriptProcessorPlayer(context) {
    const factory = context.createScriptProcessor || context.createJavaScriptNode;
    if (typeof factory !== 'function') {
      throw new Error('这个浏览器不支持 Web Audio 兼容模式，请使用较新的浏览器。');
    }

    let node = null;
    for (const size of [4096, 2048, 1024]) {
      try {
        node = factory.call(context, size, 0, 2);
        break;
      } catch {
        node = null;
      }
    }
    if (!node) throw new Error('无法创建浏览器音频输出节点。');

    const compatOverheadMs = 60;
    const state = {
      sourceRate: 48000,
      channels: 2,
      outputRate: context.sampleRate || 48000,
      baseTargetMs: 240,
      targetMs: 240 + compatOverheadMs,
      boostMs: 0,
      ring: null,
      ringFrames: 0,
      writePos: 0,
      readPos: 0,
      primed: false,
      paused: true,
      playing: false,
      step: 1,
      baseStep: 1,
      rateBias: 0,
      underruns: 0,
      overruns: 0,
      fade: 0,
      lastStats: 0,
      lastUnderrunAt: 0,
      lastLeft: 0,
      lastRight: 0
    };

    function updateTarget() {
      state.targetMs = Math.min(900, state.baseTargetMs + compatOverheadMs + state.boostMs);
    }

    function allocate(rate) {
      const safeRate = Math.max(8000, Math.min(384000, Number(rate) || 48000));
      state.sourceRate = safeRate;
      state.ringFrames = Math.ceil(safeRate * 2.5);
      state.ring = new Float32Array(state.ringFrames * 2);
      state.baseStep = safeRate / state.outputRate;
      reset();
    }

    function reset() {
      if (state.ring) state.ring.fill(0);
      state.writePos = 0;
      state.readPos = 0;
      state.primed = false;
      state.playing = false;
      state.step = state.baseStep;
      state.fade = 0;
    }

    function push(buffer) {
      if (!(buffer instanceof ArrayBuffer) || !state.ring) return;
      const channels = state.channels;
      const bytesPerFrame = channels * 2;
      const frames = Math.floor(buffer.byteLength / bytesPerFrame);
      if (frames <= 0) return;

      const view = new DataView(buffer);
      const targetFrames = (state.targetMs / 1000) * state.sourceRate;
      let available = state.writePos - state.readPos;
      const safetyCap = Math.min(state.ringFrames - 1, targetFrames + state.sourceRate * 0.35);
      if (available > safetyCap) {
        // Never let a slow/odd audio clock turn into seconds of latency.
        state.readPos = state.writePos - targetFrames;
        available = state.writePos - state.readPos;
        state.overruns += 1;
        state.boostMs = Math.max(0, state.boostMs - 20);
        updateTarget();
      }

      if (available + frames > state.ringFrames) {
        state.readPos = state.primed
          ? state.writePos + frames - state.ringFrames
          : state.writePos;
        state.overruns += 1;
        // Too much buffered audio means the target was raised too aggressively.
        state.boostMs = Math.max(0, state.boostMs - 20);
        updateTarget();
      }

      let index = state.writePos % state.ringFrames;
      let offset = 0;
      for (let frame = 0; frame < frames; frame += 1) {
        const left = view.getInt16(offset, true) / 32768;
        const right = channels > 1 ? view.getInt16(offset + 2, true) / 32768 : left;
        offset += bytesPerFrame;
        const pair = index * 2;
        state.ring[pair] = left;
        state.ring[pair + 1] = right;
        index += 1;
        if (index >= state.ringFrames) index = 0;
      }

      state.writePos += frames;
    }

    function process(event) {
      const output = event.outputBuffer;
      const left = output.getChannelData(0);
      const right = output.numberOfChannels > 1 ? output.getChannelData(1) : left;
      const frames = left.length;

      if (state.paused || !state.ring) {
        reportStats();
        return;
      }

      const targetFrames = (state.targetMs / 1000) * state.sourceRate;
      const primeFrames = targetFrames + state.sourceRate * 0.06;
      if (!state.primed) {
        if (state.writePos - state.readPos < primeFrames) {
          reportStats();
          return;
        }
        state.primed = true;
        state.playing = true;
        state.fade = 0;
      }

      const fadeStep = 1 / Math.max(1, state.outputRate * 0.018);
      for (let i = 0; i < frames; i += 1) {
        if (state.writePos - state.readPos <= 1) {
          state.underruns += 1;
          state.playing = false;
          state.primed = false;
          state.readPos = state.writePos;
          state.fade = 0;
          state.lastUnderrunAt = performance.now();
          // A quick ramp-out avoids the click that a hard zero would produce.
          const tail = Math.min(96, frames - i);
          for (let j = 0; j < tail; j += 1) {
            const gain = 1 - (j + 1) / tail;
            left[i + j] = state.lastLeft * gain;
            if (right !== left) right[i + j] = state.lastRight * gain;
          }
          // Raise the target for the rest of this session. Occasional Wi-Fi
          // scheduling hiccups then become extra latency instead of stutter.
          state.boostMs = Math.min(180, state.boostMs + 35);
          updateTarget();
          break;
        }

        const whole = Math.floor(state.readPos);
        const indexA = whole % state.ringFrames;
        const indexB = (indexA + 1) % state.ringFrames;
        const fraction = state.readPos - whole;
        const a = indexA * 2;
        const b = indexB * 2;
        const sampleLeft = state.ring[a] + (state.ring[b] - state.ring[a]) * fraction;
        const sampleRight = state.ring[a + 1] + (state.ring[b + 1] - state.ring[a + 1]) * fraction;

        state.lastLeft = sampleLeft;
        state.lastRight = sampleRight;
        state.fade = Math.min(1, state.fade + fadeStep);
        left[i] = sampleLeft * state.fade;
        if (right !== left) right[i] = sampleRight * state.fade;
        state.readPos += state.step;
      }

      const available = state.writePos - state.readPos;
      const error = (available - targetFrames) / Math.max(targetFrames, 1);
      state.rateBias = Math.max(-0.03, Math.min(0.03, state.rateBias + error * 0.000018));
      const correction = Math.max(-0.03, Math.min(0.03, error * 0.02 + state.rateBias));
      state.step = state.baseStep * (1 + correction);
      reportStats();
    }

    function reportStats() {
      const now = performance.now();
      if (state.lastStats && now - state.lastStats < 500) return;
      state.lastStats = now;
      const bufferedFrames = Math.max(0, state.writePos - state.readPos);
      if (player.onMessage) {
        player.onMessage({
          type: 'stats',
          engine: 'script',
          bufferMs: Math.round((bufferedFrames / state.sourceRate) * 1000),
          targetMs: Math.round(state.targetMs),
          underruns: state.underruns,
          overruns: state.overruns,
          playing: state.playing,
          primed: state.primed,
          step: Number(state.step.toFixed(6)),
          rateBias: Number(state.rateBias.toFixed(6)),
          sourceRate: state.sourceRate
        });
      }
    }

    const player = {
      engine: 'script',
      node,
      onMessage: null,
      sendAudio: push,
      setConfig(sampleRate, channels, targetMs) {
        state.channels = Math.max(1, Math.min(2, Number(channels) || 2));
        state.baseTargetMs = Math.max(60, Math.min(1200, Number(targetMs) || 240));
        state.boostMs = 0;
        updateTarget();
        const rateChanged = Number(sampleRate) !== state.sourceRate;
        if (rateChanged || !state.ring) allocate(sampleRate);
        else reset();
        state.paused = false;
      },
      setTarget(targetMs) {
        state.baseTargetMs = Math.max(60, Math.min(1200, Number(targetMs) || 240));
        updateTarget();
      },
      pause() {
        state.paused = true;
        reset();
      },
      resume() {
        state.paused = false;
        reset();
      },
      reset,
      dispose() {
        node.onaudioprocess = null;
        try { node.disconnect(); } catch { /* ignored */ }
      }
    };

    node.onaudioprocess = process;
    allocate(48000);
    return player;
  }

  function updateEngineUi() {
    const compatibility = R.audioEngine === 'script';
    if (els.engineChip) {
      els.engineChip.hidden = !compatibility;
      const label = els.engineChip.querySelector('span');
      if (label) {
        label.textContent = compatibility ? '兼容模式' : '';
      }
    }
    if (els.detailEngine) {
      els.detailEngine.textContent = compatibility
        ? '兼容模式 · ScriptProcessor'
        : 'AudioWorklet · 低延迟';
    }
  }

  function resumeContext(context) {
    if (!context || context.state === 'closed') return Promise.resolve();
    return Promise.race([
      context.resume().catch(() => {}),
      new Promise((resolve) => window.setTimeout(resolve, 280))
    ]);
  }

  function connectAudio() {
    disposeSocket();
    const generation = ++R.socketGeneration;
    const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const url = protocol + '//' + location.host + '/ws/audio?code=' +
      encodeURIComponent(R.code) + '&name=' + encodeURIComponent(R.name) +
      '&platform=' + encodeURIComponent(R.platform);

    let socket;
    try {
      socket = new WebSocket(url);
    } catch {
      connectionFailed('无法创建音频连接。');
      return;
    }

    R.ws = socket;
    socket.binaryType = 'arraybuffer';
    setStatus('connecting', '正在连接');
    els.playerNetwork.textContent = '正在连接 · 本地网络';

    socket.addEventListener('open', () => {
      if (generation !== R.socketGeneration) return;
      R.connected = true;
      setStatus(R.gotHello ? 'live' : 'connecting', R.gotHello ? '已连接' : '正在握手');
    });

    socket.addEventListener('message', (event) => {
      if (generation !== R.socketGeneration) return;
      if (typeof event.data === 'string') {
        handleServerMessage(event.data);
      } else if (event.data instanceof ArrayBuffer && R.player) {
        R.player.sendAudio(event.data);
      }
    });

    socket.addEventListener('close', (event) => {
      if (generation !== R.socketGeneration) return;
      R.ws = null;
      R.connected = false;
      handleSocketClose(event);
    });

    socket.addEventListener('error', () => {
      if (generation !== R.socketGeneration) return;
      // The close event follows and decides whether to reconnect.
    });
  }

  function disposeSocket() {
    R.socketGeneration += 1;
    if (!R.ws) return;
    const socket = R.ws;
    R.ws = null;
    socket.onclose = null;
    socket.onmessage = null;
    socket.onerror = null;
    socket.onopen = null;
    try { socket.close(); } catch { /* ignored */ }
  }

  function handleSocketClose(event) {
    R.connected = false;
    resetLoading();

    if (event.code === 4401) {
      R.started = false;
      R.gotHello = false;
      R.codeFromUrl = false;
      R.code = '';
      applyCodeUi();
      setStatus('error', '访问码无效');
      setFormNote('访问码不正确，请向电脑端确认后重试。', true);
      stopAudio();
      showJoin();
      return;
    }

    if (event.code === 4429) {
      R.started = false;
      R.gotHello = false;
      setStatus('error', '尝试次数过多');
      setFormNote('尝试次数过多，请等待一分钟再试。', true);
      stopAudio();
      showJoin();
      return;
    }

    if (event.code === 4001) {
      R.intentional = true;
      R.started = false;
      R.gotHello = false;
      stopAudio();
      showJoin();
      setStatus('idle', '已断开');
      setFormNote('电脑端已断开这台设备。', true);
      return;
    }

    if (R.intentional) return;

    if (!R.gotHello) {
      connectionFailed('连接失败。请确认电脑和这台设备在同一网络，然后重试。');
      return;
    }

    setStatus('paused', '正在重连');
    els.playerNetwork.textContent = '连接中断 · 正在重连';
    showOverlay({
      mode: 'reconnect',
      eyebrow: '连接中断',
      title: '正在重新连接',
      copy: '请保持电脑上的 CX Audio Output 开启，并确认两台设备仍在同一网络。',
      action: '立即重连',
      onAction: reconnectNow
    });
    scheduleReconnect();
  }

  function connectionFailed(message) {
    R.started = false;
    R.gotHello = false;
    R.connected = false;
    resetLoading();
    setStatus('error', '连接失败');
    setFormNote(message, true);
    stopAudio();
    showJoin();
  }

  function scheduleReconnect() {
    clearTimeout(R.reconnectTimer);
    if (R.intentional) return;
    R.reconnectAttempts += 1;
    const delay = Math.min(9000, 900 * Math.pow(1.6, Math.min(R.reconnectAttempts, 5)));
    R.reconnectTimer = window.setTimeout(() => {
      if (!R.connected && !R.intentional && R.started) connectAudio();
    }, delay);
  }

  function reconnectNow() {
    if (!R.started || R.code.length !== 6) {
      if (R.ctx) R.ctx.resume().catch(() => {});
      return;
    }
    clearTimeout(R.reconnectTimer);
    R.intentional = false;
    R.gotHello = true;
    hideOverlay();
    if (R.ctx && R.ctx.state === 'suspended') R.ctx.resume().catch(() => {});
    connectAudio();
  }

  function handleServerMessage(raw) {
    let message;
    try {
      message = JSON.parse(raw);
    } catch {
      return;
    }

    switch (message.type) {
      case 'hello':
        handleHello(message);
        break;
      case 'format':
        if (message.sampleRate) {
          R.sampleRate = message.sampleRate;
          configurePlayer();
          updateSourceText();
          updateDetails();
        }
        break;
      case 'stream':
        handleStream(message);
        break;
      case 'profile':
        R.profile = message.profile || 'balanced';
        R.targetMs = message.targetMs || R.targetMs;
        if (R.player) {
          R.player.setTarget(R.targetMs);
        }
        updateDetails();
        break;
      case 'master':
        R.masterVolume = clampNumber(message.volume, 100, 0, 100);
        R.masterMuted = Boolean(message.muted);
        applyGain();
        updatePlaybackUi();
        break;
      case 'volume':
        R.localVolume = clampNumber(message.value, R.localVolume, 0, 100);
        localStorage.setItem('cx.volume', String(R.localVolume));
        updateVolumeUi();
        applyGain();
        break;
      case 'muted':
        R.localMuted = Boolean(message.value);
        localStorage.setItem('cx.muted', R.localMuted ? '1' : '0');
        updateVolumeUi();
        applyGain();
        updatePlaybackUi();
        break;
      case 'identify':
        playIdentify();
        break;
      case 'rtt':
        R.rtt = clampNumber(message.value, 0, 0, 10000);
        updateStatsUi();
        break;
      case 'kicked':
        R.intentional = true;
        break;
      case 'ping':
        sendJson({ type: 'pong', t: message.t });
        break;
    }
  }

  function handleHello(message) {
    const firstHello = !R.gotHello;
    R.gotHello = true;
    R.connected = true;
    R.started = true;
    R.reconnectAttempts = 0;
    R.hostName = message.serverName || R.hostName;
    R.captureStatus = message.captureStatus || 'starting';
    R.streamPlaying = Boolean(message.playing);
    R.masterVolume = clampNumber(message.masterVolume, 100, 0, 100);
    R.masterMuted = Boolean(message.muted);
    R.profile = message.profile || 'balanced';
    R.targetMs = message.targetMs || 240;
    R.sampleRate = message.sampleRate || 48000;

    els.joinHostName.textContent = R.hostName;
    els.hostName.textContent = R.hostName;
    updateSourceText();
    updateDetails();
    applyGain();

    if (firstHello) {
      R.startTime = Date.now();
      showPlayer();
      removeCodeFromUrl();
      requestWakeLock();
    }

    configurePlayer();
    if (R.streamPlaying) {
      R.player?.resume();
      hideOverlay();
      setStatus('live', '正在播放');
    } else {
      R.player?.pause();
      handleStream({ playing: false, reason: R.captureStatus });
    }

    resetLoading();
    setFormNote('点击后请允许浏览器播放声音。');
    updatePlaybackUi();
    updateConnectionAge();
    sendJson({ type: 'stats', bufferMs: R.stats.bufferMs, playback: playbackName(), underruns: R.stats.underruns, overruns: R.stats.overruns });
  }

  function handleStream(message) {
    R.streamPlaying = Boolean(message.playing);
    if (R.streamPlaying) {
      R.captureStatus = 'live';
      R.player?.resume();
      hideOverlay();
      setStatus('live', '正在播放');
    } else {
      const reason = message.reason || 'paused';
      R.captureStatus = reason;
      R.player?.pause();
      setStatus(reason === 'starting' ? 'connecting' : reason === 'paused' ? 'paused' : 'error',
        reason === 'starting' ? '正在启动' : reason === 'paused' ? '已暂停' : '信号中断');

      if (reason === 'paused') {
        showOverlay({
          mode: 'paused',
          eyebrow: '串流已暂停',
          title: '电脑端暂停了播放',
          copy: '在电脑上的 CX Audio Output 控制台点击“继续串流”，声音会立刻恢复。',
          action: '知道了',
          onAction: hideOverlay,
          dismissible: true
        });
      } else if (reason === 'starting') {
        showOverlay({
          mode: 'starting',
          eyebrow: '正在准备',
          title: '电脑正在启动音频',
          copy: '正在连接系统输出设备，通常只需要几秒钟。',
          action: '知道了',
          onAction: hideOverlay,
          dismissible: true
        });
      } else if (reason === 'no-device') {
        showOverlay({
          mode: 'error',
          eyebrow: '没有输出设备',
          title: '电脑上找不到扬声器',
          copy: '请在电脑上连接扬声器或耳机，CX Audio Output 会自动重试。',
          action: '连接详情',
          onAction: openDetails
        });
      } else if (reason === 'error') {
        showOverlay({
          mode: 'error',
          eyebrow: '信号中断',
          title: '音频捕获出现问题',
          copy: '电脑端正在自动重试。你可以在连接详情中查看状态。',
          action: '连接详情',
          onAction: openDetails
        });
      }
    }

    applyGain();
    updatePlaybackUi();
  }

  function configurePlayer() {
    if (!R.player) return;
    R.player.setConfig(R.sampleRate || 48000, 2, R.targetMs);
  }

  function handlePlayerMessage(message) {
    if (!message || typeof message.type !== 'string') return;
    if (message.type === 'stats') {
      if (message.engine) {
        R.audioEngine = message.engine;
        updateEngineUi();
      }
      R.stats.bufferMs = Number(message.bufferMs) || 0;
      R.stats.underruns = Number(message.underruns) || 0;
      R.stats.overruns = Number(message.overruns) || 0;
      R.stats.playing = Boolean(message.playing);
      R.stats.primed = Boolean(message.primed);
      R.stats.step = Number(message.step) || 1;
      R.stats.targetMs = Number(message.targetMs) || R.targetMs;
      updateStatsUi();
      updatePlaybackUi();
    }
  }

  function applyGain() {
    if (!R.gain || !R.ctx) return;
    const master = Math.pow(clampNumber(R.masterVolume, 100, 0, 100) / 100, 1.35);
    const local = Math.pow(clampNumber(R.localVolume, 100, 0, 100) / 100, 1.35);
    const target = R.streamPlaying && !R.masterMuted && !R.localMuted ? master * local : 0;
    R.gain.gain.setTargetAtTime(target, R.ctx.currentTime, 0.04);
  }

  function updatePlaybackUi() {
    if (!R.started) return;
    let label = '正在播放';
    if (!R.streamPlaying) {
      label = R.captureStatus === 'paused' ? '已暂停'
        : R.captureStatus === 'starting' ? '正在启动' : '等待信号';
    } else if (R.masterMuted) {
      label = '电脑端已静音';
    } else if (R.localMuted) {
      label = '已静音';
    } else if (!R.stats.playing) {
      label = '正在缓冲';
    }

    els.orbState.textContent = label;
    els.orbState.dataset.state = !R.streamPlaying ? 'paused'
      : (R.masterMuted || R.localMuted) ? 'muted'
      : R.stats.playing ? 'playing' : 'buffering';
    updateStatsUi();
  }

  function updateStatsUi() {
    if (!R.started) return;
    if (!R.streamPlaying) {
      els.buffer.textContent = R.captureStatus === 'paused' ? '串流已暂停' : '等待音频';
    } else {
      els.buffer.textContent = '缓冲 ' + Math.max(0, Math.round(R.stats.bufferMs)) + ' ms';
    }
    els.buffer.parentElement.classList.toggle('is-warn', R.stats.bufferMs > R.targetMs * 2.2);
    els.rtt.textContent = R.rtt > 0 ? '网络 ' + R.rtt + ' ms' : '网络 --';
    els.rtt.parentElement.classList.toggle('is-warn', R.rtt > 120);
    updateDetails();
  }

  function updateVolumeUi() {
    els.localVolume.value = String(R.localVolume);
    els.localVolumeValue.textContent = String(R.localVolume);
    paintRange(els.localVolume);
    els.localMute.setAttribute('aria-pressed', String(R.localMuted));
  }

  function updateSourceText() {
    const rate = Math.round((R.sampleRate || 48000) / 100) / 10;
    els.source.textContent = '来自 ' + R.hostName + ' · ' + rate + ' kHz · 立体声';
  }

  function updateConnectionAge() {
    if (!R.started || !R.startTime) return;
    const seconds = Math.floor((Date.now() - R.startTime) / 1000);
    const minutes = Math.floor(seconds / 60);
    const hours = Math.floor(minutes / 60);
    const label = hours > 0
      ? hours + ' 小时 ' + (minutes % 60) + ' 分钟'
      : minutes > 0 ? minutes + ' 分钟'
      : seconds < 5 ? '刚刚连接' : seconds + ' 秒';
    els.playerNetwork.textContent = (R.connected ? '已连接 · ' : '重连中 · ') + label;
  }

  function sendStats() {
    if (!R.started || !R.connected) return;
    sendJson({
      type: 'stats',
      bufferMs: R.stats.bufferMs,
      playback: playbackName(),
      underruns: R.stats.underruns,
      overruns: R.stats.overruns
    });
  }

  function playbackName() {
    if (!R.streamPlaying) return R.captureStatus === 'paused' ? 'paused' : 'buffering';
    if (!R.stats.playing) return 'buffering';
    return 'playing';
  }

  function sendJson(payload) {
    if (!R.ws || R.ws.readyState !== WebSocket.OPEN) return;
    try {
      R.ws.send(JSON.stringify(payload));
    } catch {
      // The close handler will drive recovery.
    }
  }

  function showPlayer() {
    els.joinView.hidden = true;
    els.playerView.hidden = false;
    els.body.classList.add('is-playing');
    setStatus(R.streamPlaying ? 'live' : 'paused', R.streamPlaying ? '正在播放' : '等待信号');
    window.scrollTo(0, 0);
  }

  function showJoin() {
    els.playerView.hidden = true;
    els.joinView.hidden = false;
    els.body.classList.remove('is-playing');
    updateConnectionAge();
  }

  function setStatus(mode, label) {
    els.rxStatus.dataset.state = mode;
    els.rxStatus.querySelector('span').textContent = label;
  }

  function setLoading(loading) {
    els.startButton.classList.toggle('is-loading', loading);
    els.startButton.disabled = loading;
    els.startLabel.textContent = loading ? '正在连接…' : '开始聆听';
  }

  function resetLoading() {
    setLoading(false);
  }

  function setFormNote(message, isError = false) {
    els.formNote.textContent = message;
    els.formNote.classList.toggle('is-error', Boolean(isError));
  }

  function showOverlay({ mode, eyebrow, title, copy, action, onAction, dismissible = false }) {
    R.overlayHandler = onAction || null;
    R.overlayDismissible = dismissible;
    els.overlay.dataset.mode = mode || 'info';
    els.overlayEyebrow.textContent = eyebrow;
    els.overlayTitle.textContent = title;
    els.overlayCopy.textContent = copy;
    els.overlayAction.textContent = action || '知道了';
    els.overlay.hidden = false;
  }

  function hideOverlay() {
    els.overlay.hidden = true;
    R.overlayHandler = null;
  }

  function openDetails() {
    updateDetails();
    if (typeof els.detailsDialog.showModal === 'function') {
      els.detailsDialog.showModal();
    } else {
      els.detailsDialog.setAttribute('open', '');
    }
  }

  function closeDetails() {
    if (typeof els.detailsDialog.close === 'function') {
      els.detailsDialog.close();
    } else {
      els.detailsDialog.removeAttribute('open');
    }
  }

  function updateDetails() {
    els.detailHost.textContent = R.hostName || '—';
    els.detailRate.textContent = R.sampleRate ? (R.sampleRate / 1000).toFixed(R.sampleRate % 1000 === 0 ? 0 : 1) + ' kHz' : '—';
    const profileLabels = { low: '低延迟 · 120 ms', balanced: '均衡 · 240 ms', stable: '稳定 · 450 ms' };
    els.detailProfile.textContent = profileLabels[R.profile] || (R.targetMs + ' ms');
    els.detailBuffer.textContent = R.stats.bufferMs > 0 ? R.stats.bufferMs + ' ms' : '缓冲中';
    els.detailRtt.textContent = R.rtt > 0 ? R.rtt + ' ms' : '—';
    els.detailUnderruns.textContent = String(R.stats.underruns || 0);
  }

  function disconnectAndReturn() {
    R.intentional = true;
    clearTimeout(R.reconnectTimer);
    disposeSocket();
    releaseWakeLock();
    stopAudio();
    R.started = false;
    R.gotHello = false;
    R.streamPlaying = false;
    resetLoading();
    setStatus('idle', '已断开');
    showJoin();
    setFormNote('已断开连接。可以随时重新开始。');
    applyCodeUi();
  }

  function stopAudio() {
    if (R.player) {
      try { R.player.dispose(); } catch { /* ignored */ }
      R.player = null;
    }
    R.audioEngine = 'unknown';
    updateEngineUi();
    if (R.gain) {
      try { R.gain.disconnect(); } catch { /* ignored */ }
      R.gain = null;
    }
    if (R.analyser) {
      try { R.analyser.disconnect(); } catch { /* ignored */ }
      R.analyser = null;
    }
    R.analyserData = null;
    if (R.ctx) {
      const context = R.ctx;
      R.ctx = null;
      try { context.close(); } catch { /* ignored */ }
    }
    R.stats = { bufferMs: 0, underruns: 0, overruns: 0, playing: false, primed: false };
  }

  async function testSpeaker() {
    els.testButton.disabled = true;
    const oldLabel = els.testButton.innerHTML;
    els.testButton.textContent = '正在播放测试音…';
    try {
      const AudioContextClass = window.AudioContext || window.webkitAudioContext;
      if (!AudioContextClass) throw new Error('no audio');
      const context = new AudioContextClass();
      await resumeContext(context);
      playChime(context, 0.08);
      window.setTimeout(() => {
        context.close().catch(() => {});
        els.testButton.disabled = false;
        els.testButton.innerHTML = oldLabel;
      }, 1500);
    } catch {
      els.testButton.disabled = false;
      els.testButton.innerHTML = oldLabel;
      setFormNote('无法播放测试音，请检查浏览器的声音权限。', true);
    }
  }

  function playIdentify() {
    R.identifyUntil = Date.now() + 1100;
    els.body.classList.add('is-identifying');
    window.setTimeout(() => els.body.classList.remove('is-identifying'), 1100);
    if (navigator.vibrate) {
      try { navigator.vibrate([50, 70, 50]); } catch { /* ignored */ }
    }

    if (R.ctx && R.ctx.state === 'running') {
      playChime(R.ctx, 0.1);
      return;
    }

    const AudioContextClass = window.AudioContext || window.webkitAudioContext;
    if (!AudioContextClass) return;
    const context = new AudioContextClass();
    context.resume().then(() => {
      playChime(context, 0.1);
      setTimeout(() => context.close().catch(() => {}), 1400);
    }).catch(() => context.close().catch(() => {}));
  }

  function playChime(context, volume) {
    const now = context.currentTime;
    const master = context.createGain();
    master.gain.setValueAtTime(0, now);
    master.gain.linearRampToValueAtTime(volume, now + 0.02);
    master.gain.exponentialRampToValueAtTime(0.0001, now + 0.95);
    master.connect(context.destination);

    [880, 1318.5, 1760].forEach((frequency, index) => {
      const oscillator = context.createOscillator();
      const gain = context.createGain();
      oscillator.type = 'sine';
      oscillator.frequency.value = frequency;
      gain.gain.value = index === 0 ? 0.6 : index === 1 ? 0.3 : 0.16;
      oscillator.connect(gain);
      gain.connect(master);
      oscillator.start(now + index * 0.055);
      oscillator.stop(now + 1.05);
    });
  }

  async function requestWakeLock() {
    if (!('wakeLock' in navigator) || document.visibilityState !== 'visible') return;
    if (R.wakeLock) return;
    try {
      R.wakeLock = await navigator.wakeLock.request('screen');
      R.wakeLock.addEventListener('release', () => { R.wakeLock = null; });
    } catch {
      R.wakeLock = null;
    }
  }

  function releaseWakeLock() {
    if (!R.wakeLock) return;
    try { R.wakeLock.release(); } catch { /* ignored */ }
    R.wakeLock = null;
  }

  function toggleFullscreen() {
    if (document.fullscreenElement) {
      document.exitFullscreen().catch(() => {});
    } else if (document.documentElement.requestFullscreen) {
      document.documentElement.requestFullscreen().catch(() => {
        toast('此浏览器暂不支持全屏。', true);
      });
    } else {
      toast('此浏览器暂不支持全屏。', true);
    }
  }

  function removeCodeFromUrl() {
    if (!location.search.includes('code=')) return;
    const url = new URL(location.href);
    url.searchParams.delete('code');
    history.replaceState(null, '', url.pathname + url.search + url.hash);
  }

  function initCanvas() {
    const canvas = els.orbCanvas;
    if (!canvas) return;
    const context = canvas.getContext('2d');
    if (!context) return;
    R.canvasContext = context;
    R.canvasReady = true;
  }

  function drawOrb(now) {
    requestAnimationFrame(drawOrb);
    if (!R.canvasReady || els.playerView.hidden) return;
    // ScriptProcessor runs on the main thread. Keep the decorative visualizer
    // at a lower frame rate in compatibility mode so it cannot starve audio.
    if (R.audioEngine === 'script' && now - R.lastOrbDraw < 80) return;
    R.lastOrbDraw = now;
    const canvas = els.orbCanvas;
    const ctx = R.canvasContext;
    const size = canvas.width;
    const center = size / 2;
    const t = R.reducedMotion ? 0 : now / 1000;
    ctx.clearRect(0, 0, size, size);

    const live = R.streamPlaying && !R.masterMuted && !R.localMuted;
    const identifying = Date.now() < R.identifyUntil;
    let data = R.analyserData;

    if (R.analyser && R.analyserData) {
      R.analyser.getByteFrequencyData(R.analyserData);
      data = R.analyserData;
    }

    const baseRadius = size * 0.345;
    const bars = 96;
    ctx.save();
    ctx.translate(center, center);
    ctx.rotate(t * 0.035);
    ctx.lineCap = 'round';

    for (let i = 0; i < bars; i += 1) {
      const normalized = i / bars;
      const dataIndex = Math.min(
        (data ? data.length : 1) - 1,
        Math.max(0, Math.floor(Math.pow(normalized, 1.72) * (data ? data.length * 0.68 : 1)))
      );
      let value = data ? data[dataIndex] / 255 : 0;
      if (!live) value *= 0.22;
      if (identifying) value = Math.max(value, 0.58 + 0.42 * Math.abs(Math.sin(t * 4 + i * 0.25)));
      const idle = live ? 0.065 + 0.026 * Math.sin(t * 1.4 + i * 0.32) : 0.022;
      const height = Math.max(idle, value) * size * 0.11;
      const angle = (i / bars) * Math.PI * 2 - Math.PI / 2;
      const inner = baseRadius;
      const outer = baseRadius + height;
      const alpha = live ? 0.24 + value * 0.7 : 0.08 + value * 0.15;
      ctx.beginPath();
      ctx.strokeStyle = identifying
        ? 'rgba(239,255,198,' + (0.45 + value * 0.5) + ')'
        : 'rgba(216,255,101,' + alpha + ')';
      ctx.lineWidth = size * 0.0036;
      ctx.moveTo(Math.cos(angle) * inner, Math.sin(angle) * inner);
      ctx.lineTo(Math.cos(angle) * outer, Math.sin(angle) * outer);
      ctx.stroke();
    }

    ctx.restore();

    ctx.beginPath();
    ctx.arc(center, center, size * 0.285, 0, Math.PI * 2);
    ctx.strokeStyle = live ? 'rgba(216,255,101,0.17)' : 'rgba(216,255,101,0.08)';
    ctx.lineWidth = 1;
    ctx.stroke();

    ctx.beginPath();
    ctx.arc(center, center, size * 0.405, 0, Math.PI * 2);
    ctx.strokeStyle = 'rgba(216,255,101,0.06)';
    ctx.lineWidth = 1;
    ctx.setLineDash([2, 9]);
    ctx.stroke();
    ctx.setLineDash([]);

    const dotAngle = -Math.PI / 2 + t * 0.42;
    ctx.beginPath();
    ctx.arc(
      center + Math.cos(dotAngle) * size * 0.405,
      center + Math.sin(dotAngle) * size * 0.405,
      size * 0.0065,
      0,
      Math.PI * 2
    );
    ctx.fillStyle = identifying ? '#f2ffd0' : 'rgba(216,255,101,0.65)';
    ctx.fill();
  }

  function paintRange(input) {
    if (!input) return;
    const min = Number(input.min) || 0;
    const max = Number(input.max) || 100;
    const value = Number(input.value) || 0;
    const percent = max === min ? 0 : ((value - min) / (max - min)) * 100;
    input.style.setProperty('--fill', Math.max(0, Math.min(100, percent)) + '%');
  }

  function clampNumber(value, fallback, min, max) {
    if (value === null || value === undefined || value === '') return fallback;
    const number = Number(value);
    if (!Number.isFinite(number)) return fallback;
    return Math.max(min, Math.min(max, number));
  }

  function audioErrorMessage(error) {
    const text = String(error?.message || error || '');
    if (text.includes('兼容模式') || text.includes('音频输出节点')) {
      return '这个浏览器既不支持 AudioWorklet，也不支持兼容音频引擎，请换用较新的 Chrome、Edge 或 Safari。';
    }
    if (text.includes('not allowed') || text.includes('suspended')) {
      return '浏览器拦截了声音播放，请再点一次“开始聆听”。';
    }
    return '无法初始化音频：' + (text || '未知错误');
  }

  function detectPlatform() {
    const ua = navigator.userAgent || '';
    if (/iPhone|iPod/.test(ua)) return 'iOS';
    if (/iPad/.test(ua)) return 'iPadOS';
    if (/Android/.test(ua)) return 'Android';
    if (/Macintosh|Mac OS X/.test(ua)) return 'macOS';
    if (/Windows/.test(ua)) return 'Windows';
    if (/Linux/.test(ua)) return 'Linux';
    return '浏览器';
  }

  function detectDeviceName() {
    const platform = detectPlatform();
    const map = {
      iOS: '我的 iPhone',
      iPadOS: '我的 iPad',
      Android: 'Android 设备',
      macOS: 'Mac 扬声器',
      Windows: 'Windows PC',
      Linux: 'Linux 设备'
    };
    return map[platform] || '浏览器扬声器';
  }


  function toast(message, isError = false) {
    clearTimeout(R.toastTimer);
    els.toast.textContent = message;
    els.toast.classList.toggle('is-error', Boolean(isError));
    els.toast.classList.add('is-visible');
    R.toastTimer = window.setTimeout(() => els.toast.classList.remove('is-visible'), 2600);
  }
})();

