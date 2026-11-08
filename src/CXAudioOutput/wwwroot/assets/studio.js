(() => {
  'use strict';

  const $ = (selector, root = document) => root.querySelector(selector);
  const $$ = (selector, root = document) => Array.from(root.querySelectorAll(selector));

  const els = {
    body: document.body,
    connection: $('#connection-chip'),
    liveDot: $('#live-dot'),
    streamEyebrow: $('#stream-eyebrow'),
    heroTitle: $('#hero-title'),
    heroSub: $('#hero-sub'),
    deviceName: $('#device-name'),
    pauseButton: $('#pause-button'),
    pauseLabel: $('#pause-label'),
    peakDb: $('#peak-db'),
    sampleMeta: $('#sample-meta'),
    masterVolume: $('#master-volume'),
    masterValue: $('#master-value'),
    muteButton: $('#mute-button'),
    profileGroup: $('#profile-group'),
    profileValue: $('#profile-value'),
    qrTarget: $('#qr-target'),
    accessCode: $('#access-code'),
    addressSelect: $('#address-select'),
    shareUrl: $('#share-url'),
    copyButton: $('#copy-button'),
    topShare: $('#top-share'),
    rotateCode: $('#rotate-code'),
    netWarning: $('#net-warning'),
    hostPreview: $('#host-preview'),
    speakerGrid: $('#speaker-grid'),
    emptyState: $('#empty-state'),
    receiverCount: $('#receiver-count'),
    navCount: $('#nav-count'),
    footerVersion: $('#footer-version'),
    speakerTemplate: $('#speaker-template'),
    setupDialog: $('#setup-dialog'),
    setupButton: $('#setup-button'),
    setupLink: $('#setup-link'),
    closeSetup: $('#close-setup'),
    doneSetup: $('#done-setup'),
    quitButton: $('#quit-button'),
    firewallCommand: $('#firewall-command'),
    copyFirewall: $('#copy-firewall'),
    toast: $('#toast'),
    canvas: $('#level-canvas'),
    waveStage: $('#wave-stage')
  };

  const state = {
    ws: null,
    data: null,
    connected: false,
    reconnectTimer: 0,
    reconnectAttempts: 0,
    selectedIp: localStorage.getItem('cx.studio.ip') || '',
    addressSignature: '',
    shareUrl: '',
    qrUrl: '',
    masterTimer: 0,
    cardVolumeTimers: new Map(),
    cards: new Map(),
    displayLevels: new Array(56).fill(0),
    targetLevels: new Array(56).fill(0),
    canvasReady: false,
    toastTimer: 0,
    setupOpen: false
  };

  const statusCopy = {
    live: '系统音频 · 正在直播',
    starting: '系统音频 · 正在启动',
    paused: '系统音频 · 已暂停',
    error: '系统音频 · 发生错误',
    'no-device': '系统音频 · 未找到输出设备'
  };

  const heroCopy = {
    live: '让每个房间，<br><span>都有声音。</span>',
    starting: '正在连接，<br><span>系统音频。</span>',
    paused: '串流已暂停，<br><span>随时继续。</span>',
    error: '输出设备，<br><span>需要检查。</span>',
    'no-device': '没有找到，<br><span>输出设备。</span>'
  };

  const playbackCopy = {
    playing: '播放中',
    buffering: '缓冲中',
    paused: '已暂停',
    silent: '静音'
  };

  init();

  function init() {
    bindEvents();
    initCanvas();
    updateRange(els.masterVolume);
    requestAnimationFrame(drawLevels);
    connect();
    fetch('/api/health')
      .then((response) => response.json())
      .then((health) => {
        if (health && health.version) {
          els.footerVersion.textContent = 'v' + health.version;
        }
      })
      .catch(() => {});
  }

  function bindEvents() {
    els.pauseButton.addEventListener('click', () => {
      const status = state.data?.capture?.status;
      const paused = status === 'paused';
      if (status === 'error' || status === 'no-device') {
        sendAction('pause', { value: false });
        toast('正在重新检测输出设备…');
        return;
      }
      sendAction('pause', { value: !paused });
      els.pauseButton.disabled = true;
      setTimeout(() => { els.pauseButton.disabled = false; }, 450);
    });

    els.muteButton.addEventListener('click', () => {
      const muted = Boolean(state.data?.muted);
      els.muteButton.setAttribute('aria-pressed', String(!muted));
      sendAction('muted', { value: !muted });
    });

    els.masterVolume.addEventListener('input', () => {
      updateRange(els.masterVolume);
      els.masterValue.textContent = els.masterVolume.value + '%';
      clearTimeout(state.masterTimer);
      state.masterTimer = window.setTimeout(() => {
        sendAction('masterVolume', { value: Number(els.masterVolume.value) });
      }, 55);
    });

    els.masterVolume.addEventListener('change', () => {
      clearTimeout(state.masterTimer);
      sendAction('masterVolume', { value: Number(els.masterVolume.value) });
    });

    els.profileGroup.addEventListener('click', (event) => {
      const button = event.target.closest('[data-profile]');
      if (!button) return;
      const profile = button.dataset.profile;
      $$('.segmented button', els.profileGroup).forEach((item) => {
        item.classList.toggle('active', item === button);
      });
      sendAction('profile', { value: profile });
    });

    els.copyButton.addEventListener('click', () => copyShareLink());
    els.topShare.addEventListener('click', () => copyShareLink());

    els.rotateCode.addEventListener('click', () => {
      if (!window.confirm('更换访问码后，新的连接需要使用新访问码。已连接的设备不会中断。继续？')) return;
      sendAction('rotateCode');
      toast('正在生成新的访问码…');
    });

    els.addressSelect.addEventListener('change', () => {
      state.selectedIp = els.addressSelect.value;
      localStorage.setItem('cx.studio.ip', state.selectedIp);
      updateShareTarget();
    });

    els.speakerGrid.addEventListener('input', (event) => {
      const range = event.target.closest('.card-range');
      if (!range) return;
      const card = range.closest('.speaker-card');
      const id = card?.dataset.id;
      if (!id) return;
      const value = Number(range.value);
      updateRange(range);
      const label = $('[data-field="volume"]', card);
      if (label) label.textContent = value + '%';
      clearTimeout(state.cardVolumeTimers.get(id));
      state.cardVolumeTimers.set(id, window.setTimeout(() => {
        sendAction('deviceVolume', { id, value });
      }, 50));
    });

    els.speakerGrid.addEventListener('change', (event) => {
      const range = event.target.closest('.card-range');
      if (!range) return;
      const card = range.closest('.speaker-card');
      if (!card?.dataset.id) return;
      sendAction('deviceVolume', { id: card.dataset.id, value: Number(range.value) });
    });

    els.speakerGrid.addEventListener('click', (event) => {
      const actionButton = event.target.closest('[data-act]');
      if (!actionButton) return;
      const card = actionButton.closest('.speaker-card');
      const id = card?.dataset.id;
      if (!id) return;
      const name = $('.speaker-name', card)?.textContent || '这台设备';

      switch (actionButton.dataset.act) {
        case 'identify':
          sendAction('identify', { id });
          actionButton.classList.add('is-flashing');
          setTimeout(() => actionButton.classList.remove('is-flashing'), 900);
          toast('已让 ' + name + ' 播放提示音');
          break;
        case 'mute':
          sendAction('deviceMuted', { id, value: actionButton.getAttribute('aria-pressed') !== 'true' });
          break;
        case 'kick':
          if (!window.confirm('断开 ' + name + ' 的连接？')) return;
          sendAction('kick', { id });
          break;
      }
    });

    const openSetup = (event) => {
      event.preventDefault();
      state.setupOpen = true;
      if (typeof els.setupDialog.showModal === 'function') {
        els.setupDialog.showModal();
      } else {
        els.setupDialog.setAttribute('open', '');
      }
    };

    els.setupButton.addEventListener('click', openSetup);
    els.setupLink.addEventListener('click', openSetup);
    els.closeSetup.addEventListener('click', closeSetup);
    els.doneSetup.addEventListener('click', closeSetup);
    els.setupDialog.addEventListener('close', () => { state.setupOpen = false; });
    els.setupDialog.addEventListener('click', (event) => {
      if (event.target !== els.setupDialog) return;
      const rect = els.setupDialog.getBoundingClientRect();
      if (event.clientX < rect.left || event.clientX > rect.right ||
          event.clientY < rect.top || event.clientY > rect.bottom) {
        closeSetup();
      }
    });

    els.copyFirewall.addEventListener('click', async () => {
      const ok = await copyText(els.firewallCommand.textContent);
      if (ok) toast('防火墙命令已复制');
    });

    els.quitButton.addEventListener('click', async () => {
      if (!window.confirm('确定要退出 CX Audio Output 吗？所有设备将停止播放。')) return;
      try {
        await fetch('/api/shutdown', { method: 'POST' });
        toast('正在退出…');
        setTimeout(() => {
          document.body.innerHTML = '<div style="min-height:100vh;display:grid;place-items:center;background:#08090b;color:#8d9588;font-family:sans-serif">CX Audio Output 已停止。可以关闭此页面。</div>';
        }, 700);
      } catch {
        toast('退出失败，请关闭应用窗口。', true);
      }
    });

    window.addEventListener('online', () => {
      if (!state.connected) connect();
    });

    window.addEventListener('beforeunload', () => {
      if (state.ws) {
        try { state.ws.close(); } catch { /* ignore */ }
      }
    });
  }

  function connect() {
    clearTimeout(state.reconnectTimer);
    if (state.ws && (state.ws.readyState === WebSocket.OPEN || state.ws.readyState === WebSocket.CONNECTING)) {
      return;
    }

    const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    let socket;
    try {
      socket = new WebSocket(protocol + '//' + location.host + '/ws/host');
    } catch {
      scheduleReconnect();
      return;
    }

    state.ws = socket;
    setConnection('connecting', '正在连接');

    socket.addEventListener('open', () => {
      state.connected = true;
      state.reconnectAttempts = 0;
      setConnection('online', '控制台已连接');
      sendAction('sync');
    });

    socket.addEventListener('message', (event) => {
      if (typeof event.data !== 'string') return;
      try {
        const message = JSON.parse(event.data);
        if (message.type === 'state') render(message);
      } catch {
        // Ignore malformed host messages.
      }
    });

    socket.addEventListener('close', () => {
      state.connected = false;
      setConnection('offline', '连接已断开');
      state.ws = null;
      if (!state.setupOpen) scheduleReconnect();
    });

    socket.addEventListener('error', () => {
      try { socket.close(); } catch { /* ignore */ }
    });
  }

  function scheduleReconnect() {
    clearTimeout(state.reconnectTimer);
    state.reconnectAttempts += 1;
    const delay = Math.min(8000, 900 * Math.pow(1.55, Math.min(state.reconnectAttempts, 6)));
    state.reconnectTimer = window.setTimeout(connect, delay);
  }

  function setConnection(mode, label) {
    els.connection.dataset.state = mode;
    const text = $('span', els.connection);
    if (text) text.textContent = label;
  }

  function sendAction(action, payload = {}) {
    if (!state.ws || state.ws.readyState !== WebSocket.OPEN) return false;
    try {
      state.ws.send(JSON.stringify({ action, ...payload }));
      return true;
    } catch {
      return false;
    }
  }

  function render(message) {
    state.data = message;
    const capture = message.capture || {};
    const status = capture.status || 'starting';
    const live = status === 'live';
    const device = capture.deviceName || (message.demo ? '演示信号' : '系统输出');

    els.body.dataset.capture = status;
    els.streamEyebrow.textContent = statusCopy[status] || statusCopy.starting;
    els.heroTitle.innerHTML = heroCopy[status] || heroCopy.starting;

    if (status === 'live') {
      els.heroSub.innerHTML = '正在从 <strong>' + escapeHtml(device) + '</strong> 捕获音频';
    } else if (status === 'paused') {
      els.heroSub.textContent = '串流已暂停。点击“继续串流”即可恢复播放。';
    } else if (status === 'starting') {
      els.heroSub.textContent = '正在准备 Windows 系统音频…';
    } else if (status === 'no-device') {
      els.heroSub.textContent = '未找到默认音频输出设备。请连接扬声器或耳机，应用会自动重试。';
    } else {
      els.heroSub.innerHTML = '<span class="sub-error">' + escapeHtml(capture.error || '音频捕获失败，正在重试。') + '</span>';
    }

    const rate = Number(capture.sampleRate) || 0;
    els.sampleMeta.textContent = live && rate
      ? (rate / 1000).toFixed(rate % 1000 === 0 ? 0 : 1) + ' kHz · STEREO · 16-BIT'
      : '等待音频信号';
    els.peakDb.textContent = formatPeak(message.peak);

    els.pauseButton.classList.toggle('is-resume', status === 'paused' ||
      status === 'error' || status === 'no-device');
    els.pauseLabel.textContent = status === 'paused' ? '继续串流'
      : (status === 'error' || status === 'no-device') ? '重新检测' : '暂停串流';

    const muted = Boolean(message.muted);
    els.muteButton.setAttribute('aria-pressed', String(muted));
    if (document.activeElement !== els.masterVolume) {
      els.masterVolume.value = String(message.masterVolume ?? 100);
      updateRange(els.masterVolume);
    }
    els.masterValue.textContent = (message.masterVolume ?? 100) + '%';

    const profile = message.profile || 'balanced';
    $$('.segmented button', els.profileGroup).forEach((button) => {
      button.classList.toggle('active', button.dataset.profile === profile);
    });
    els.profileValue.textContent = (message.targetMs || 240) + ' ms';

    els.accessCode.textContent = message.accessCode || '······';
    els.receiverCount.textContent = String(message.receivers?.length || 0);
    els.navCount.textContent = String(message.receivers?.length || 0);
    els.firewallCommand.textContent =
      'New-NetFirewallRule -DisplayName "CX Audio Output" -Direction Inbound -Protocol TCP -LocalPort ' +
      (message.port || 5240) + ' -Action Allow -Profile Private';

    renderAddresses(message);
    renderSpeakers(message.receivers || []);
    updateTargetLevels(message, status);

    document.title = message.receivers?.length
      ? 'CX Audio Output · ' + message.receivers.length + ' 台扬声器'
      : 'CX Audio Output · 控制台';
  }

  function renderAddresses(message) {
    const addresses = Array.isArray(message.addresses) ? message.addresses : [];
    const signature = JSON.stringify(addresses.map((item) => [item.ip, item.url, item.network]));
    const code = message.accessCode || '';
    const hasLan = addresses.some((item) => !String(item.ip).startsWith('127.'));

    if (signature !== state.addressSignature) {
      state.addressSignature = signature;
      els.addressSelect.innerHTML = '';
      addresses.forEach((item) => {
        const option = document.createElement('option');
        option.value = item.ip;
        option.textContent = String(item.ip).startsWith('127.')
          ? '本机 · ' + item.ip
          : item.ip + (item.network ? ' · ' + shortenNetwork(item.network) : '');
        els.addressSelect.appendChild(option);
      });

      const stillThere = addresses.some((item) => item.ip === state.selectedIp);
      if (!stillThere) {
        state.selectedIp = (addresses.find((item) => !String(item.ip).startsWith('127.')) || addresses[0])?.ip || '';
      }
      if (state.selectedIp) {
        els.addressSelect.value = state.selectedIp;
      }
    }

    if (addresses.length === 0) {
      els.netWarning.textContent = '没有可用网卡。请在 WinUI 控制台选择至少一个已连接网卡。';
    } else if (!hasLan) {
      els.netWarning.textContent = '只检测到本机地址。其他设备暂时无法连接。';
    }
    els.netWarning.hidden = hasLan;
    els.hostPreview.href = '/listen?code=' + encodeURIComponent(code);
    updateShareTarget();
  }

  function shortenNetwork(name) {
    if (!name) return '';
    return name.length > 22 ? name.slice(0, 22) + '…' : name;
  }

  function updateShareTarget() {
    const message = state.data;
    if (!message) return;
    const addresses = Array.isArray(message.addresses) ? message.addresses : [];
    const selected = addresses.find((item) => item.ip === state.selectedIp) || addresses[0];
    if (!selected) {
      state.shareUrl = '';
      els.shareUrl.textContent = '未选择可用网卡';
      els.addressSelect.disabled = true;
      els.copyButton.disabled = true;
      updateQr(null);
      return;
    }

    const url = selected.url + '/listen?code=' + encodeURIComponent(message.accessCode || '');
    state.shareUrl = url;
    els.shareUrl.textContent = url;
    els.addressSelect.disabled = false;
    els.copyButton.disabled = false;
    els.addressSelect.value = selected.ip;
    updateQr(url);
  }

  function updateQr(url) {
    if (url === state.qrUrl) return;
    if (!url) {
      state.qrUrl = '';
      els.qrTarget.innerHTML =
        '<div class="qr-empty"><span>CX</span><small>选择可用网卡后<br>显示二维码</small></div>';
      return;
    }

    try {
      if (typeof qrcode !== 'function') return;
      const qr = qrcode(0, 'M');
      qr.addData(url);
      qr.make();
      els.qrTarget.innerHTML = qr.createSvgTag({
        cellSize: 4,
        margin: 0,
        scalable: true,
        alt: 'CX Audio Output 收听二维码'
      });
      state.qrUrl = url;
    } catch {
      els.qrTarget.textContent = '二维码生成失败';
    }
  }

  async function copyShareLink() {
    updateShareTarget();
    if (!state.shareUrl) {
      toast('还没有可用的局域网地址。', true);
      return;
    }
    const ok = await copyText(state.shareUrl);
    toast(ok ? '收听链接已复制' : '复制失败，请手动选择链接', !ok);
  }

  async function copyText(text) {
    try {
      if (navigator.clipboard && window.isSecureContext) {
        await navigator.clipboard.writeText(text);
        return true;
      }
    } catch {
      // Fall through to the legacy path.
    }

    try {
      const area = document.createElement('textarea');
      area.value = text;
      area.setAttribute('readonly', '');
      area.style.position = 'fixed';
      area.style.left = '-9999px';
      area.style.top = '0';
      document.body.appendChild(area);
      area.select();
      const ok = document.execCommand('copy');
      document.body.removeChild(area);
      return ok;
    } catch {
      return false;
    }
  }

  function renderSpeakers(receivers) {
    const ids = new Set(receivers.map((receiver) => receiver.id));
    for (const [id, card] of state.cards) {
      if (!ids.has(id)) {
        card.remove();
        state.cards.delete(id);
      }
    }

    receivers.forEach((receiver) => {
      let card = state.cards.get(receiver.id);
      if (!card) {
        card = createSpeakerCard(receiver);
        state.cards.set(receiver.id, card);
        els.speakerGrid.appendChild(card);
      }
      updateSpeakerCard(card, receiver);
    });

    els.emptyState.hidden = receivers.length > 0;
    els.receiverCount.textContent = String(receivers.length);
    els.navCount.textContent = String(receivers.length);
  }

  function createSpeakerCard(receiver) {
    const fragment = els.speakerTemplate.content.cloneNode(true);
    const card = fragment.querySelector('.speaker-card');
    card.dataset.id = receiver.id;
    const avatar = $('.speaker-avatar span', card);
    if (avatar) avatar.textContent = firstGlyph(receiver.name);
    const title = $('.speaker-name', card);
    if (title) title.textContent = receiver.name || '扬声器';
    return card;
  }

  function updateSpeakerCard(card, receiver) {
    const playback = receiver.playback || 'buffering';
    card.dataset.playback = playback;
    card.dataset.platform = receiver.platform || '';

    const name = $('.speaker-name', card);
    if (name) name.textContent = receiver.name || '扬声器';
    const avatar = $('.speaker-avatar span', card);
    if (avatar) avatar.textContent = firstGlyph(receiver.name);

    const meta = $('.speaker-meta', card);
    if (meta) meta.textContent = (receiver.wired ? 'USB 有线 · ' : '') +
      (receiver.platform || '浏览器') + ' · ' + (receiver.address || '局域网');

    setField(card, 'rtt', receiver.rttMs > 0 ? receiver.rttMs + ' ms' : '-- ms');
    setField(card, 'buffer', receiver.bufferMs > 0 ? receiver.bufferMs + ' ms' : '-- ms');
    setField(card, 'state', playbackCopy[playback] || '缓冲中');
    setField(card, 'volume', (receiver.volume ?? 100) + '%');

    const underruns = $('[data-field="underruns"]', card);
    if (underruns) {
      const count = Number(receiver.underruns) || 0;
      underruns.textContent = count > 0 ? '欠载 ' + count + ' 次' : '连接稳定';
      underruns.classList.toggle('is-warning', count > 3);
    }

    const mute = $('.card-mute', card);
    if (mute) mute.setAttribute('aria-pressed', String(Boolean(receiver.muted)));

    const range = $('.card-range', card);
    if (range && document.activeElement !== range) {
      range.value = String(receiver.volume ?? 100);
      updateRange(range);
    }
  }

  function setField(card, name, value) {
    const field = $('[data-field="' + name + '"]', card);
    if (field) field.textContent = value;
  }

  function firstGlyph(value) {
    const text = String(value || '?').trim();
    return Array.from(text)[0] || '?';
  }

  function updateRange(input) {
    if (!input) return;
    const min = Number(input.min) || 0;
    const max = Number(input.max) || 100;
    const value = Number(input.value) || 0;
    const percent = max === min ? 0 : ((value - min) / (max - min)) * 100;
    input.style.setProperty('--fill', Math.max(0, Math.min(100, percent)) + '%');
  }

  function updateTargetLevels(message, status) {
    const levels = Array.isArray(message.levels) ? message.levels : [];
    for (let i = 0; i < state.targetLevels.length; i += 1) {
      let value = Number(levels[i]) || 0;
      if (status !== 'live') value *= 0.2;
      state.targetLevels[i] = Math.max(0, Math.min(1, value));
    }
  }

  function formatPeak(peak) {
    const value = Number(peak) || 0;
    if (value <= 0.00001) return '−∞ dB';
    return (20 * Math.log10(value)).toFixed(1).replace('-', '−') + ' dB';
  }

  function initCanvas() {
    if (!els.canvas) return;
    const context = els.canvas.getContext('2d');
    if (!context) return;
    state.canvasReady = true;
    state.canvasContext = context;

    const resize = () => {
      const rect = els.canvas.getBoundingClientRect();
      const dpr = Math.min(2, window.devicePixelRatio || 1);
      els.canvas.width = Math.max(1, Math.round(rect.width * dpr));
      els.canvas.height = Math.max(1, Math.round(rect.height * dpr));
      context.setTransform(dpr, 0, 0, dpr, 0, 0);
    };

    resize();
    if (typeof ResizeObserver === 'function') {
      new ResizeObserver(resize).observe(els.waveStage);
    } else {
      window.addEventListener('resize', resize);
    }
  }

  function drawLevels(now) {
    requestAnimationFrame(drawLevels);
    if (!state.canvasReady) return;
    const canvas = els.canvas;
    const ctx = state.canvasContext;
    const width = canvas.clientWidth;
    const height = canvas.clientHeight;
    if (width < 2 || height < 2) return;

    ctx.clearRect(0, 0, width, height);
    const count = 56;
    const gap = Math.max(2, Math.min(4, width / 180));
    const barWidth = Math.max(1, (width - gap * (count - 1)) / count);
    const centerY = height * 0.53;
    const maxHeight = height * 0.74;
    const live = state.data?.capture?.status === 'live';
    const reduced = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    const t = reduced ? 0 : now / 1000;

    const gradient = ctx.createLinearGradient(0, centerY - maxHeight / 2, 0, centerY + maxHeight / 2);
    gradient.addColorStop(0, live ? 'rgba(216,255,101,0.96)' : 'rgba(154,163,144,0.45)');
    gradient.addColorStop(0.5, live ? 'rgba(198,240,80,0.9)' : 'rgba(130,139,121,0.35)');
    gradient.addColorStop(1, live ? 'rgba(147,190,49,0.32)' : 'rgba(100,108,94,0.16)');

    ctx.fillStyle = gradient;

    for (let i = 0; i < count; i += 1) {
      let target = state.targetLevels[i] || 0;
      if (live && target < 0.008) {
        target = 0.012 + 0.008 * Math.sin(t * 1.15 + i * 0.52);
      }
      const previous = state.displayLevels[i] || 0;
      const eased = previous + (target - previous) * 0.24;
      state.displayLevels[i] = eased;
      const amplitude = Math.max(1.5, Math.pow(Math.max(0, eased), 0.55) * maxHeight);
      const x = i * (barWidth + gap);
      roundRect(ctx, x, centerY - amplitude / 2, barWidth, amplitude, Math.min(barWidth / 2, 3));
      ctx.fill();
    }

    ctx.fillStyle = live ? 'rgba(216,255,101,0.28)' : 'rgba(255,255,255,0.08)';
    ctx.fillRect(0, centerY, width, 1);

    if (live && state.data) {
      const peak = Number(state.data.peak) || 0;
      if (peak > 0.01) {
        const y = centerY - (peak * maxHeight) / 2 - 8;
        ctx.fillStyle = 'rgba(216,255,101,0.45)';
        ctx.fillRect(0, y, width, 1);
      }
    }
  }

  function roundRect(ctx, x, y, width, height, radius) {
    const r = Math.max(0, Math.min(radius, width / 2, height / 2));
    ctx.beginPath();
    ctx.moveTo(x + r, y);
    ctx.arcTo(x + width, y, x + width, y + height, r);
    ctx.arcTo(x + width, y + height, x, y + height, r);
    ctx.arcTo(x, y + height, x, y, r);
    ctx.arcTo(x, y, x + width, y, r);
    ctx.closePath();
  }

  function closeSetup() {
    state.setupOpen = false;
    if (typeof els.setupDialog.close === 'function') {
      els.setupDialog.close();
    } else {
      els.setupDialog.removeAttribute('open');
    }
    if (!state.connected) scheduleReconnect();
  }

  function toast(message, isError = false) {
    clearTimeout(state.toastTimer);
    els.toast.textContent = message;
    els.toast.classList.toggle('is-error', Boolean(isError));
    els.toast.classList.add('is-visible');
    state.toastTimer = window.setTimeout(() => {
      els.toast.classList.remove('is-visible');
    }, 2600);
  }

  function escapeHtml(value) {
    return String(value)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#039;');
  }
})();
