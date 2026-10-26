'use strict';

/* ThodMedia — UI for the embedded ThodHUB module.
   Talks to the .NET worker through the ThodHUB bridge (worker.*). */

const $ = id => document.getElementById(id);
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

const IMAGE_ICON = '#i-photo';
const VIDEO_ICON = '#i-video';
const AUDIO_ICON = '#i-audio';

const state = {
  folder: null,
  recursive: true,
  items: [],
  selected: new Set(),
  filter: 'all',
  search: '',
  tab: 'photo',
  jobId: null,
  jobLock: false,
  env: null,
  pendingMuxAudio: '',
  truncated: false,
  thumbs: new Map(),
  thumbRequested: new Set(),
  thumbQueue: [],
  thumbActive: 0,
  settingsReady: false
};

let saveTimer = 0;
let toastTimer = 0;

/* ------------------------------------------------------------------ */
/* Bridge                                                              */
/* ------------------------------------------------------------------ */

function bridgeReady() {
  return typeof window.thodHub === 'object' && window.thodHub !== null;
}

async function call(method, params) {
  if (!bridgeReady()) throw new Error('Нет связи с ThodHUB. Откройте ThodMedia через хаб.');
  return window.thodHub.call(method, params === undefined ? null : params);
}

const worker = (method, params) => call('worker.' + method, params);

/* ------------------------------------------------------------------ */
/* Small helpers                                                       */
/* ------------------------------------------------------------------ */

function toast(text, isError) {
  const el = $('toast');
  el.textContent = text;
  el.classList.toggle('err', !!isError);
  el.classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.classList.remove('show'), isError ? 6000 : 3600);
}

function setStatus(text, warn) {
  $('statusText').textContent = text;
  $('statusbar').classList.toggle('warn', !!warn);
}

function formatBytes(bytes) {
  const n = Number(bytes);
  if (!Number.isFinite(n) || n < 0) return '';
  if (n < 1024) return n + ' Б';
  const units = ['КБ', 'МБ', 'ГБ', 'ТБ'];
  let value = n / 1024;
  let i = 0;
  while (value >= 1024 && i < units.length - 1) { value /= 1024; i++; }
  return (value >= 100 ? value.toFixed(0) : value >= 10 ? value.toFixed(1) : value.toFixed(2)) + ' ' + units[i];
}

function formatDuration(seconds) {
  const total = Math.max(0, Math.round(Number(seconds) || 0));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const pad = v => String(v).padStart(2, '0');
  return h > 0 ? `${h}:${pad(m)}:${pad(s)}` : `${m}:${pad(s)}`;
}

function baseName(path) {
  const parts = String(path).split(/[\\/]/);
  return parts[parts.length - 1] || path;
}

function iconFor(kind) {
  return kind === 'video' ? VIDEO_ICON : kind === 'audio' ? AUDIO_ICON : IMAGE_ICON;
}

function kindLabel(kind) {
  return kind === 'image' ? 'фото' : kind === 'video' ? 'видео' : 'звук';
}

function currentItems(kind) {
  return [...state.selected]
    .map(path => state.items.find(item => item.path === path))
    .filter(item => item && (!kind || item.kind === kind));
}

function byKind(kind) {
  const order = state.items.filter(item => item.kind === kind).map(item => item.path);
  return currentItems(kind)
    .sort((a, b) => order.indexOf(a.path) - order.indexOf(b.path))
    .map(item => item.path);
}

function selectedOutputDir(id) {
  const value = $(id).value.trim();
  return value ? value : null;
}

/* ------------------------------------------------------------------ */
/* Settings                                                            */
/* ------------------------------------------------------------------ */

const SETTINGS_IDS = [
  'recursive',
  'photoFormat', 'photoResize', 'photoW', 'photoH', 'photoPercent', 'photoAllowUpscale',
  'photoQuality', 'photoBg', 'photoKeepMeta', 'photoOutput', 'photoSuffix', 'photoOverwrite',
  'videoPreset', 'videoSpeed', 'videoCrf', 'videoResolution', 'videoW', 'videoFps',
  'videoTrimStart', 'videoTrimEnd', 'videoFast', 'videoAudio', 'videoAudioBitrate',
  'videoOutput', 'videoSuffix', 'videoOverwrite',
  'muxAudio', 'muxMode', 'muxBitrate', 'muxSuffix', 'muxOutput',
  'audioFormat', 'audioBitrate', 'audioTrimStart', 'audioTrimEnd', 'audioOutput', 'audioSuffix', 'audioOverwrite',
  'bgOutput', 'bgSuffix',
  'edSize', 'edHardness', 'edFeather', 'edRefine', 'edOutput', 'edSuffix', 'edOverwrite'
];

function collectSettings() {
  const data = { version: 1, folder: state.folder, tab: state.tab };
  for (const id of SETTINGS_IDS) {
    const el = $(id);
    if (!el) continue;
    data[id] = el.type === 'checkbox' ? el.checked : el.value;
  }
  return data;
}

function applySettings(saved) {
  if (!saved || typeof saved !== 'object') return;
  if (typeof saved.folder === 'string') state.folder = saved.folder;
  for (const id of SETTINGS_IDS) {
    const el = $(id);
    if (!el || saved[id] === undefined || saved[id] === null) continue;
    if (el.type === 'checkbox') el.checked = !!saved[id];
    else el.value = String(saved[id]);
  }
  if (typeof saved.tab === 'string') state.tab = saved.tab;
  if (typeof saved.muxAudio === 'string') state.pendingMuxAudio = saved.muxAudio;
}

function scheduleSave() {
  if (!state.settingsReady) return;
  clearTimeout(saveTimer);
  saveTimer = setTimeout(saveSettings, 450);
}

async function saveSettings() {
  if (!bridgeReady()) return;
  try {
    await call('settings.set', collectSettings());
  } catch {
    /* settings are best-effort */
  }
}

/* ------------------------------------------------------------------ */
/* Folder scan                                                         */
/* ------------------------------------------------------------------ */

async function pickFolder() {
  try {
    const folder = await call('dialog.folder');
    if (!folder) return;
    state.folder = folder;
    state.selected.clear();
    scheduleSave();
    await scan();
  } catch (err) {
    toast(err.message || 'Не удалось выбрать папку.', true);
  }
}

async function scan() {
  if (!state.folder) return;
  setStatus('Сканирую папку…');
  try {
    const result = await worker('scan', {
      folder: state.folder,
      recursive: $('recursive').checked
    });
    state.recursive = result.recursive;
    state.items = result.items || [];
    state.truncated = !!result.truncated;
    const alive = new Set(state.items.map(item => item.path));
    for (const path of [...state.selected]) if (!alive.has(path)) state.selected.delete(path);
    renderList();
    updateAudioOptions();
    updateSelectionInfo();
    const suffix = state.truncated ? ' · показаны первые 5000' : '';
    setStatus(`${state.folder} · ${state.items.length} файлов${suffix}`);
  } catch (err) {
    state.items = [];
    renderList();
    setStatus('Не удалось прочитать папку.', true);
    toast(err.message || 'Не удалось прочитать папку.', true);
  }
}

/* ------------------------------------------------------------------ */
/* File list                                                           */
/* ------------------------------------------------------------------ */

function filteredItems() {
  const query = state.search.trim().toLowerCase();
  return state.items.filter(item => {
    if (state.filter !== 'all' && item.kind !== state.filter) return false;
    if (query && !item.name.toLowerCase().includes(query)) return false;
    return true;
  });
}

let listObserver = null;

function renderList() {
  const host = $('fileList');
  host.textContent = '';
  if (listObserver) { listObserver.disconnect(); listObserver = null; }

  if (!state.folder) {
    host.append(emptyBlock('Папка не выбрана', 'Нажмите «Выбрать папку» сверху.', '#i-folder'));
    updateFooter();
    return;
  }

  const items = filteredItems();
  if (items.length === 0) {
    host.append(emptyBlock('Ничего не найдено', state.items.length ? 'Измените фильтр или поиск.' : 'В папке нет фото, видео или звука.', '#i-search'));
    updateFooter();
    return;
  }

  const fragment = document.createDocumentFragment();
  for (const item of items) fragment.append(buildRow(item));
  host.append(fragment);
  updateFooter();

  if (!window.IntersectionObserver) {
    for (const item of items) enqueueThumb(item.path);
    return;
  }

  listObserver = new IntersectionObserver(entries => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      listObserver.unobserve(entry.target);
      enqueueThumb(entry.target.dataset.path);
    }
  }, { root: host, rootMargin: '140px' });

  for (const thumb of host.querySelectorAll('.thumb[data-path]')) listObserver.observe(thumb);
}

function emptyBlock(title, text, icon) {
  const div = document.createElement('div');
  div.className = 'empty';
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
  use.setAttribute('href', icon);
  svg.append(use);
  const body = document.createElement('div');
  const strong = document.createElement('b');
  strong.textContent = title;
  body.append(strong, document.createElement('br'), document.createTextNode(text));
  div.append(svg, body);
  return div;
}

function buildRow(item) {
  const row = document.createElement('div');
  const selected = state.selected.has(item.path);
  row.className = 'file-item' + (selected ? ' selected' : '');
  row.dataset.path = item.path;

  const box = document.createElement('input');
  box.type = 'checkbox';
  box.checked = selected;
  box.setAttribute('aria-label', 'Выбрать ' + item.name);
  box.addEventListener('click', event => event.stopPropagation());
  box.addEventListener('change', () => setSelected(item.path, box.checked));

  const thumb = document.createElement('div');
  thumb.className = 'thumb';
  thumb.dataset.path = item.path;
  const cached = state.thumbs.get(item.path);
  if (cached) {
    const img = document.createElement('img');
    img.src = cached;
    img.alt = '';
    img.decoding = 'async';
    thumb.append(img);
  } else {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
    use.setAttribute('href', iconFor(item.kind));
    svg.append(use);
    thumb.append(svg);
  }

  const meta = document.createElement('div');
  meta.className = 'file-meta';
  const name = document.createElement('div');
  name.className = 'file-name';
  name.textContent = item.name;
  name.title = item.relative;
  const sub = document.createElement('div');
  sub.className = 'file-sub';
  sub.textContent = describe(item);
  meta.append(name, sub);

  row.append(box, thumb, meta);
  row.addEventListener('click', () => setSelected(item.path, !state.selected.has(item.path)));
  if (item.kind === 'image') {
    row.addEventListener('dblclick', () => {
      setSelected(item.path, true);
      worker('open', { path: item.path }).catch(err => toast(err.message, true));
    });
  } else {
    row.addEventListener('dblclick', () => worker('open', { path: item.path }).catch(err => toast(err.message, true)));
  }
  return row;
}

function describe(item) {
  const ext = (item.extension || '').toUpperCase();
  const parts = [ext || kindLabel(item.kind), formatBytes(item.size)];
  const probe = item.probe;
  if (probe) {
    if (probe.duration > 0) parts.push(formatDuration(probe.duration));
    if (probe.width > 0 && probe.height > 0) parts.push(`${probe.width}×${probe.height}`);
  }
  return parts.filter(Boolean).join(' · ');
}

function updateFooter() {
  const visible = state.search.trim() || state.filter !== 'all' ? filteredItems().length : state.items.length;
  const base = state.items.length ? `${visible} из ${state.items.length} файлов` : '0 файлов';
  $('fileCount').textContent = state.selected.size ? `${base} · выбрано ${state.selected.size}` : base;
}

function setSelected(path, on) {
  if (on) state.selected.add(path); else state.selected.delete(path);
  for (const row of $('fileList').querySelectorAll('.file-item')) {
    if (row.dataset.path !== path) continue;
    row.classList.toggle('selected', on);
    const box = row.querySelector('input[type=checkbox]');
    if (box) box.checked = on;
  }
  updateFooter();
  updateSelectionInfo();
}

/* ------------------------------------------------------------------ */
/* Thumbnails                                                          */
/* ------------------------------------------------------------------ */

function enqueueThumb(path) {
  if (!path || state.thumbs.has(path) || state.thumbRequested.has(path)) return;
  state.thumbRequested.add(path);
  state.thumbQueue.push(path);
  pumpThumbs();
}

function pumpThumbs() {
  while (state.thumbActive < 3 && state.thumbQueue.length) {
    const path = state.thumbQueue.shift();
    state.thumbActive++;
    worker('thumb', { path, size: 160 })
      .then(result => {
        if (result && result.dataUrl) state.thumbs.set(path, result.dataUrl);
        const item = state.items.find(x => x.path === path);
        if (item && result && result.probe) {
          item.probe = result.probe;
          refreshRow(item);
        }
        paintThumb(path);
      })
      .catch(() => { /* not fatal: keep the format icon */ })
      .finally(() => {
        state.thumbActive--;
        pumpThumbs();
      });
  }
}

function paintThumb(path) {
  const url = state.thumbs.get(path);
  if (!url) return;
  for (const thumb of $('fileList').querySelectorAll('.thumb[data-path]')) {
    if (thumb.dataset.path !== path || thumb.querySelector('img')) continue;
    thumb.textContent = '';
    const img = document.createElement('img');
    img.src = url;
    img.alt = '';
    img.decoding = 'async';
    thumb.append(img);
  }
  for (const row of $('fileList').querySelectorAll('.file-item')) {
    if (row.dataset.path === path) refreshRow(state.items.find(x => x.path === path));
  }
}

function refreshRow(item) {
  if (!item) return;
  for (const row of $('fileList').querySelectorAll('.file-item')) {
    if (row.dataset.path !== item.path) continue;
    const sub = row.querySelector('.file-sub');
    if (sub) sub.textContent = describe(item);
  }
  updateSelectionInfo();
}

/* ------------------------------------------------------------------ */
/* Selection summaries                                                 */
/* ------------------------------------------------------------------ */

function updateSelectionInfo() {
  const images = currentItems('image');
  const videos = currentItems('video');
  const audible = currentItems().filter(item => item.kind === 'video' || item.kind === 'audio');

  $('photoSelectionInfo').textContent = images.length
    ? `Выбрано изображений: ${images.length}.`
    : 'Выберите изображения в списке слева.';
  $('videoSelectionInfo').textContent = videos.length
    ? `Выбрано видео: ${videos.length}.`
    : 'Выберите видео в списке слева.';
  $('audioSelectionInfo').textContent = audible.length
    ? `Выбрано файлов со звуком: ${audible.length}.`
    : 'Выберите видео или аудио в списке.';

  const firstVideo = videos[0];
  $('muxVideoInfo').textContent = firstVideo
    ? `${firstVideo.name}${firstVideo.probe && firstVideo.probe.duration ? ' · ' + formatDuration(firstVideo.probe.duration) : ''}`
    : 'Выберите одно видео в списке.';

  const image = images[0];
  if (image) {
    $('bgSelectedName').textContent = image.name;
    const holder = $('bgSelectedThumb');
    const cached = state.thumbs.get(image.path);
    if (cached) {
      holder.textContent = '';
      const img = document.createElement('img');
      img.src = cached;
      img.alt = '';
      holder.append(img);
    } else {
      enqueueThumb(image.path);
    }
  } else {
    $('bgSelectedName').textContent = 'Выберите изображение в списке слева — откроется редактор с мягкой кистью и предпросмотром.';
  }
}

/* ------------------------------------------------------------------ */
/* Jobs                                                                */
/* ------------------------------------------------------------------ */

function normalizeOutputs(result) {
  const outputs = [];
  if (result && Array.isArray(result.outputs)) outputs.push(...result.outputs);
  else if (result && result.output) outputs.push(result);
  const errors = result && Array.isArray(result.errors) ? result.errors.slice() : [];
  return { outputs, errors };
}

function showJobPanel(title) {
  $('jobPanel').hidden = false;
  $('jobSpinner').style.display = '';
  $('jobTitle').textContent = title || 'Обработка…';
  $('jobPct').textContent = '0%';
  $('jobBar').style.width = '0%';
  $('jobResult').textContent = '';
  $('jobCancel').disabled = false;
}

function renderJobOutputs(result) {
  const { outputs, errors } = normalizeOutputs(result);
  const host = $('jobResult');
  host.textContent = '';
  for (const item of outputs) {
    const row = document.createElement('div');
    row.className = 'job-file';
    const label = document.createElement('span');
    const size = item.size ? ' · ' + formatBytes(item.size) : '';
    label.textContent = baseName(item.output || '') + size;
    label.title = item.output || '';
    const reveal = document.createElement('button');
    reveal.className = 'btn ghost';
    reveal.textContent = 'Показать';
    reveal.addEventListener('click', () => worker('reveal', { path: item.output }).catch(err => toast(err.message, true)));
    const open = document.createElement('button');
    open.className = 'btn ghost';
    open.textContent = 'Открыть';
    open.addEventListener('click', () => worker('open', { path: item.output }).catch(err => toast(err.message, true)));
    row.append(label, reveal, open);
    host.append(row);
  }
  for (const error of errors) {
    const row = document.createElement('div');
    row.className = 'job-file';
    const label = document.createElement('span');
    label.textContent = error;
    label.style.color = 'var(--danger)';
    row.append(label);
    host.append(row);
  }
  return outputs;
}

async function runJob(kind, params, title, onProgress) {
  if (state.jobId || state.jobLock) {
    toast('Дождитесь завершения текущего задания.', true);
    throw new Error('busy');
  }
  state.jobLock = true;
  showJobPanel(title);
  let status;
  try {
    const started = await worker('job.start', { kind, params });
    state.jobId = started.jobId;
  } catch (err) {
    state.jobLock = false;
    $('jobSpinner').style.display = 'none';
    $('jobCancel').disabled = true;
    throw err;
  }

  try {
    for (;;) {
      status = await worker('job.status', { id: state.jobId });
      const pct = Math.max(0, Math.min(100, Number(status.progress) || 0));
      $('jobBar').style.width = pct + '%';
      $('jobPct').textContent = Math.round(pct) + '%';
      if (status.message) $('jobTitle').textContent = `${title} · ${status.message}`;
      if (onProgress) onProgress(pct, status.message || '');
      if (status.state === 'done') {
        $('jobSpinner').style.display = 'none';
        $('jobCancel').disabled = true;
        $('jobBar').style.width = '100%';
        $('jobPct').textContent = '100%';
        $('jobTitle').textContent = title + ' · готово';
        const outputs = renderJobOutputs(status.result);
        return { outputs, result: status.result };
      }
      if (status.state === 'error') {
        $('jobSpinner').style.display = 'none';
        $('jobCancel').disabled = true;
        renderJobOutputs({ errors: [status.error || 'Задание завершилось с ошибкой.'] });
        $('jobTitle').textContent = title + ' · ошибка';
        throw new Error(status.error || 'Задание завершилось с ошибкой.');
      }
      if (status.state === 'canceled') {
        $('jobSpinner').style.display = 'none';
        $('jobCancel').disabled = true;
        $('jobTitle').textContent = title + ' · отменено';
        throw new Error('Отменено.');
      }
      await sleep(360);
    }
  } finally {
    state.jobId = null;
    state.jobLock = false;
  }
}

async function cancelJob() {
  if (!state.jobId) return;
  try { await worker('job.cancel', { id: state.jobId }); } catch (err) { toast(err.message, true); }
}

/* ------------------------------------------------------------------ */
/* Forms                                                               */
/* ------------------------------------------------------------------ */

function updatePhotoDims() {
  const mode = $('photoResize').value;
  const wrap = $('photoDims');
  wrap.hidden = mode === 'none';
  const wField = $('photoW').parentElement;
  const hField = $('photoHField');
  const pField = $('photoPercentField');
  wField.hidden = mode === 'percent' || mode === 'height';
  hField.hidden = mode === 'percent' || mode === 'width' || mode === 'longEdge';
  pField.hidden = mode !== 'percent';
  const wLabel = wField.querySelector('label');
  wLabel.textContent = mode === 'longEdge' ? 'Длинная сторона, px' : 'Ширина, px';
  $('photoBgField').hidden = $('photoFormat').value !== 'jpg';
}

function updateVideoFields() {
  const custom = $('videoResolution').value === 'custom';
  $('videoWidthField').hidden = !custom;
}

function updateEditorLabels() {
  $('edSizeVal').textContent = $('edSize').value + ' px';
  $('edHardnessVal').textContent = $('edHardness').value + '%';
  $('edFeatherVal').textContent = $('edFeather').value + ' px';
  $('edCursorInfo').textContent = `${$('edSize').value} px · ${$('edHardness').value}%`;
  ed.size = Number($('edSize').value) || 48;
  ed.hardness = Number($('edHardness').value) || 45;
  editorUpdateCursor();
}

function syncQualityLabels() {
  $('photoQualityValue').textContent = $('photoQuality').value;
  $('videoCrfValue').textContent = $('videoCrf').value;
}

async function runPhoto() {
  const paths = byKind('image');
  if (paths.length === 0) { toast('Выберите хотя бы одно изображение.', true); return; }
  const mode = $('photoResize').value;
  const width = Number($('photoW').value) || 0;
  const height = Number($('photoH').value) || 0;
  const params = {
    paths,
    format: $('photoFormat').value,
    quality: Number($('photoQuality').value),
    resizeMode: mode,
    width: mode === 'longEdge' ? width : width,
    height: mode === 'longEdge' ? 0 : height,
    percent: Number($('photoPercent').value) || 100,
    allowUpscale: $('photoAllowUpscale').checked,
    keepMetadata: $('photoKeepMeta').checked,
    background: $('photoBg').value || '#ffffff',
    outputDir: selectedOutputDir('photoOutput'),
    suffix: $('photoSuffix').value,
    overwrite: $('photoOverwrite').checked,
    root: state.folder,
    preserveStructure: !!selectedOutputDir('photoOutput') && $('recursive').checked
  };
  try {
    const { outputs } = await runJob('image.convert', params, 'Конвертирую изображения');
    if (outputs.length) toast(`Готово: ${outputs.length} файл(ов).`);
  } catch (err) {
    if (err.message !== 'busy') toast(err.message, true);
  }
}

async function runVideo() {
  const paths = byKind('video');
  if (paths.length === 0) { toast('Выберите хотя бы одно видео.', true); return; }
  const preset = $('videoPreset').value || 'mp4-h264';
  const resolution = $('videoResolution').value;
  let width = 0;
  let height = 0;
  if (resolution === 'custom') width = Number($('videoW').value) || 0;
  else if (resolution !== 'original') height = Number(resolution) || 0;
  let fps = Number($('videoFps').value) || 0;
  if (preset === 'gif' && fps === 0) fps = 15;
  const params = {
    paths,
    preset,
    speed: $('videoSpeed').value,
    crf: Number($('videoCrf').value),
    width,
    height,
    fps,
    trimStart: $('videoTrimStart').value,
    trimEnd: $('videoTrimEnd').value,
    fastTrim: $('videoFast').checked,
    audio: $('videoAudio').value,
    audioBitrate: Number($('videoAudioBitrate').value),
    outputDir: selectedOutputDir('videoOutput'),
    suffix: $('videoSuffix').value,
    overwrite: $('videoOverwrite').checked,
    root: state.folder,
    preserveStructure: !!selectedOutputDir('videoOutput') && $('recursive').checked
  };
  try {
    const { outputs } = await runJob('video.convert', params, 'Обрабатываю видео');
    if (outputs.length) toast(`Готово: ${outputs.length} видео.`);
  } catch (err) {
    if (err.message !== 'busy') toast(err.message, true);
  }
}

async function runAudio() {
  const items = currentItems().filter(item => item.kind === 'video' || item.kind === 'audio');
  if (items.length === 0) { toast('Выберите видео или аудио.', true); return; }
  const params = {
    paths: items.map(item => item.path),
    format: $('audioFormat').value,
    bitrate: Number($('audioBitrate').value),
    trimStart: $('audioTrimStart').value,
    trimEnd: $('audioTrimEnd').value,
    outputDir: selectedOutputDir('audioOutput'),
    suffix: $('audioSuffix').value,
    overwrite: $('audioOverwrite').checked,
    root: state.folder,
    preserveStructure: !!selectedOutputDir('audioOutput') && $('recursive').checked
  };
  try {
    const { outputs } = await runJob('audio.extract', params, 'Извлекаю звук');
    if (outputs.length) toast(`Готово: ${outputs.length} файл(ов).`);
  } catch (err) {
    if (err.message !== 'busy') toast(err.message, true);
  }
}

async function runMux() {
  const videos = byKind('video');
  const videoPath = videos[0];
  const audioPath = $('muxAudio').value;
  if (!videoPath) { toast('Выберите видео в списке.', true); return; }
  if (!audioPath) { toast('В папке не найден аудиофайл для дорожки.', true); return; }
  const params = {
    videoPath,
    audioPath,
    mode: $('muxMode').value,
    audioBitrate: Number($('muxBitrate').value),
    outputDir: selectedOutputDir('muxOutput'),
    suffix: $('muxSuffix').value,
    overwrite: false
  };
  try {
    const { outputs } = await runJob('video.muxAudio', params, 'Собираю видео со звуком');
    if (outputs.length) toast('Новая дорожка добавлена.');
  } catch (err) {
    if (err.message !== 'busy') toast(err.message, true);
  }
}

async function updateAudioOptions(selected) {
  const audio = state.items.filter(item => item.kind === 'audio');
  const select = $('muxAudio');
  const keep = selected !== undefined ? selected : (state.pendingMuxAudio || select.value);
  select.textContent = '';
  if (audio.length === 0) {
    const option = document.createElement('option');
    option.value = '';
    option.textContent = 'Нет аудиофайлов в папке';
    select.append(option);
    select.disabled = true;
    return;
  }
  select.disabled = false;
  for (const item of audio) {
    const option = document.createElement('option');
    option.value = item.path;
    option.textContent = item.relative;
    select.append(option);
  }
  if (keep && audio.some(item => item.path === keep)) select.value = keep;
  else if (keep) state.pendingMuxAudio = '';
}

/* ------------------------------------------------------------------ */
/* Background editor                                                   */
/* ------------------------------------------------------------------ */

const ed = {
  open: false,
  path: null,
  name: '',
  w: 0,
  h: 0,
  img: null,
  canvas: null,
  ctx: null,
  mask: null,
  mctx: null,
  scale: 1,
  fit: 1,
  x: 0,
  y: 0,
  tool: 'brush',
  size: 48,
  hardness: 45,
  history: [],
  redo: [],
  drawing: false,
  last: null,
  space: false,
  panning: false,
  panStart: null,
  raf: 0,
  busy: false,
  bg: 'checker',
  lastOutput: null,
  closedOnce: false
};

function editorSetup() {
  ed.canvas = $('edCanvas');
  ed.ctx = ed.canvas.getContext('2d');
  ed.mask = document.createElement('canvas');
  ed.mctx = ed.mask.getContext('2d', { willReadFrequently: true });
  $('edSize').addEventListener('input', updateEditorLabels);
  $('edHardness').addEventListener('input', updateEditorLabels);
  $('edFeather').addEventListener('input', updateEditorLabels);
  $('edBrush').addEventListener('click', () => editorSetTool('brush'));
  $('edEraser').addEventListener('click', () => editorSetTool('eraser'));
  $('edUndo').addEventListener('click', editorUndo);
  $('edRedo').addEventListener('click', editorRedo);
  $('edFit').addEventListener('click', () => editorFit(true));
  $('edZoomIn').addEventListener('click', () => editorZoomCenter(1.25));
  $('edZoomOut').addEventListener('click', () => editorZoomCenter(1 / 1.25));
  $('edClose').addEventListener('click', closeEditor);
  $('edApply').addEventListener('click', editorApply);
  $('edOutputPick').addEventListener('click', () => pickInto('edOutput'));
  $('edMobilePanel').addEventListener('click', () => $('editorShell').classList.toggle('mobile-side'));
  $('edSwatches').addEventListener('click', event => {
    const button = event.target.closest('.swatch');
    if (!button) return;
    for (const swatch of $('edSwatches').querySelectorAll('.swatch')) swatch.classList.toggle('active', swatch === button);
    ed.bg = button.dataset.bg;
    applyEditorBackground();
  });

  const viewport = $('edViewport');
  viewport.addEventListener('wheel', editorWheel, { passive: false });
  viewport.addEventListener('pointerdown', editorPointerDown);
  viewport.addEventListener('pointermove', editorPointerMove);
  viewport.addEventListener('pointerup', editorPointerUp);
  viewport.addEventListener('pointercancel', editorPointerUp);
  viewport.addEventListener('pointerleave', () => {
    if (!ed.drawing && !ed.panning) $('edCursor').style.display = 'none';
  });
  viewport.addEventListener('contextmenu', event => event.preventDefault());

  window.addEventListener('resize', () => {
    if (!ed.open) return;
    const wasFit = Math.abs(ed.scale - ed.fit) < 0.001;
    if (wasFit) editorFit(true);
    else applyEditorTransform();
  });

  if (window.matchMedia) {
    const narrow = window.matchMedia('(max-width: 840px)');
    const update = () => { $('edMobilePanel').style.display = narrow.matches ? '' : 'none'; };
    update();
    if (narrow.addEventListener) narrow.addEventListener('change', update);
  }
}

async function openEditor(path) {
  if (ed.open || state.jobId || state.jobLock) {
    if (state.jobId || state.jobLock) toast('Дождитесь завершения текущего задания.', true);
    return;
  }
  const item = state.items.find(x => x.path === path);
  if (!item || item.kind !== 'image') { toast('Выберите изображение.', true); return; }

  ed.open = true;
  ed.busy = true;
  ed.path = path;
  ed.name = item.name;
  ed.lastOutput = null;
  ed.history = [];
  ed.redo = [];
  ed.img = null;
  ed.w = 0;
  ed.h = 0;
  ed.canvas.width = 1;
  ed.canvas.height = 1;
  $('edTitle').textContent = 'Удаление фона · ' + item.name;
  $('edSub').textContent = 'нейросеть выделяет объект…';
  $('edStatus').textContent = 'Загрузка модели';
  $('edMessage').hidden = true;
  $('edMessage').textContent = '';
  $('edProgressWrap').hidden = false;
  $('edProgress').style.width = '2%';
  $('bgEditor').hidden = false;
  $('editorShell').classList.remove('mobile-side');
  $('edOutput').value = $('edOutput').value || $('bgOutput').value;
  $('edSuffix').value = $('edSuffix').value || $('bgSuffix').value || '-cutout';
  applyEditorBackground();
  editorSetTool('brush');
  updateEditorLabels();

  try {
    const { result } = await runJob('bg.preview', { path, maxSize: 1200 }, 'Нейросеть выделяет объект', pct => {
      $('edProgressWrap').hidden = false;
      $('edProgress').style.width = Math.max(2, pct) + '%';
    });
    if (!ed.open) return;
    await loadEditorResult(result);
    $('edStatus').textContent = 'Готово к правке';
  } catch (err) {
    if (err.message === 'Отменено.') {
      ed.busy = false;
      closeEditor();
      return;
    }
    $('edSub').textContent = 'не удалось открыть';
    $('edStatus').textContent = 'Ошибка';
    $('edMessage').hidden = false;
    $('edMessage').textContent = err.message;
    toast(err.message, true);
  } finally {
    if (ed.open) {
      ed.busy = false;
      $('edProgressWrap').hidden = true;
    }
  }
}

function loadEditorResult(result) {
  return new Promise((resolve, reject) => {
    if (!result || !result.image || !result.mask) { reject(new Error('Нейросеть вернула пустой результат.')); return; }
    const img = new Image();
    img.onload = () => {
      const maskImg = new Image();
      maskImg.onload = () => {
        try {
          const w = img.naturalWidth || result.width;
          const h = img.naturalHeight || result.height;
          ed.img = img;
          ed.w = w;
          ed.h = h;
          ed.canvas.width = w;
          ed.canvas.height = h;
          ed.canvas.style.width = w + 'px';
          ed.canvas.style.height = h + 'px';
          ed.mask.width = w;
          ed.mask.height = h;
          const tmp = document.createElement('canvas');
          tmp.width = w;
          tmp.height = h;
          const tctx = tmp.getContext('2d', { willReadFrequently: true });
          tctx.drawImage(maskImg, 0, 0, w, h);
          const data = tctx.getImageData(0, 0, w, h).data;
          for (let i = 0; i < data.length; i += 4) {
            const alpha = data[i];
            data[i] = 255;
            data[i + 1] = 255;
            data[i + 2] = 255;
            data[i + 3] = alpha;
          }
          ed.mctx.putImageData(new ImageData(data, w, h), 0, 0);
          ed.history = [snapshotMask()];
          ed.redo = [];
          $('edStack').style.width = w + 'px';
          $('edStack').style.height = h + 'px';
          $('edSub').textContent = `${ed.name} · ${result.originalWidth || w}×${result.originalHeight || h} · IS-Net ${result.modelMs || 0} мс`;
          editorFit(true);
          editorCompose();
          resolve();
        } catch (err) { reject(err); }
      };
      maskImg.onerror = () => reject(new Error('Не удалось разобрать маску.'));
      maskImg.src = result.mask;
    };
    img.onerror = () => reject(new Error('Не удалось загрузить предпросмотр.'));
    img.src = result.image;
  });
}

function editorCompose() {
  if (!ed.img) return;
  ed.ctx.clearRect(0, 0, ed.w, ed.h);
  ed.ctx.globalCompositeOperation = 'source-over';
  ed.ctx.drawImage(ed.img, 0, 0, ed.w, ed.h);
  ed.ctx.globalCompositeOperation = 'destination-in';
  ed.ctx.drawImage(ed.mask, 0, 0);
  ed.ctx.globalCompositeOperation = 'source-over';
}

function editorInvalidate() {
  if (ed.raf) return;
  ed.raf = requestAnimationFrame(() => {
    ed.raf = 0;
    editorCompose();
  });
}

function editorFit(center) {
  const viewport = $('edViewport');
  const vw = Math.max(32, viewport.clientWidth);
  const vh = Math.max(32, viewport.clientHeight);
  if (!ed.w || !ed.h) return;
  ed.fit = Math.min(vw / ed.w, vh / ed.h, 1);
  ed.scale = ed.fit;
  if (center) {
    ed.x = (vw - ed.w * ed.scale) / 2;
    ed.y = (vh - ed.h * ed.scale) / 2;
  }
  applyEditorTransform();
}

function applyEditorTransform() {
  const stack = $('edStack');
  stack.style.transform = `translate(${ed.x}px, ${ed.y}px) scale(${ed.scale})`;
  $('edZoomVal').textContent = Math.round(ed.scale * 100) + '%';
  editorUpdateCursor();
}

function editorZoomCenter(factor) {
  const viewport = $('edViewport');
  editorZoomAt(viewport.clientWidth / 2, viewport.clientHeight / 2, factor);
}

function editorZoomAt(cx, cy, factor) {
  if (!ed.w) return;
  const next = Math.min(Math.max(ed.scale * factor, Math.max(0.02, ed.fit * 0.15)), Math.max(1, ed.fit) * 16);
  if (!Number.isFinite(next) || next <= 0) return;
  const ix = (cx - ed.x) / ed.scale;
  const iy = (cy - ed.y) / ed.scale;
  ed.scale = next;
  ed.x = cx - ix * next;
  ed.y = cy - iy * next;
  applyEditorTransform();
}

function editorWheel(event) {
  if (!ed.open) return;
  event.preventDefault();
  const rect = $('edViewport').getBoundingClientRect();
  const factor = Math.exp(-event.deltaY * (event.deltaMode === 1 ? 0.05 : 0.0016));
  editorZoomAt(event.clientX - rect.left, event.clientY - rect.top, factor);
}

function editorSetTool(tool) {
  ed.tool = tool;
  $('edBrush').classList.toggle('active', tool === 'brush');
  $('edEraser').classList.toggle('active', tool === 'eraser');
}

function editorPointFromEvent(event) {
  const rect = $('edViewport').getBoundingClientRect();
  return {
    vx: event.clientX - rect.left,
    vy: event.clientY - rect.top,
    x: (event.clientX - rect.left - ed.x) / ed.scale,
    y: (event.clientY - rect.top - ed.y) / ed.scale
  };
}

function editorUpdateCursor() {
  const cursor = $('edCursor');
  if (!ed.open || !ed.scale) { cursor.style.display = 'none'; return; }
  const diameter = Math.max(4, ed.size * ed.scale);
  cursor.style.width = diameter + 'px';
  cursor.style.height = diameter + 'px';
}

function editorPointerDown(event) {
  if (!ed.open || ed.busy || !ed.img) return;
  const point = editorPointFromEvent(event);
  $('edCursor').style.display = '';

  if (event.button === 1 || ed.space) {
    event.preventDefault();
    ed.panning = true;
    ed.panStart = { x: point.vx, y: point.vy, ox: ed.x, oy: ed.y };
    $('edViewport').setPointerCapture(event.pointerId);
    return;
  }
  if (event.button !== 0) return;
  event.preventDefault();
  $('edViewport').setPointerCapture(event.pointerId);
  $('edCursor').style.left = event.clientX + 'px';
  $('edCursor').style.top = event.clientY + 'px';
  ed.drawing = true;
  pushHistory();
  ed.last = { x: point.x, y: point.y };
  stampBrush(point.x, point.y);
  editorInvalidate();
}

function editorPointerMove(event) {
  if (!ed.open) return;
  const point = editorPointFromEvent(event);
  const cursor = $('edCursor');
  cursor.style.display = '';
  cursor.style.left = event.clientX + 'px';
  cursor.style.top = event.clientY + 'px';
  editorUpdateCursor();

  if (ed.panning && ed.panStart) {
    ed.x = ed.panStart.ox + (point.vx - ed.panStart.x);
    ed.y = ed.panStart.oy + (point.vy - ed.panStart.y);
    applyEditorTransform();
    return;
  }
  if (!ed.drawing) return;
  const events = typeof event.getCoalescedEvents === 'function' ? event.getCoalescedEvents() : [event];
  for (const raw of events) {
    const rect = $('edViewport').getBoundingClientRect();
    const x = (raw.clientX - rect.left - ed.x) / ed.scale;
    const y = (raw.clientY - rect.top - ed.y) / ed.scale;
    const last = ed.last || { x, y };
    const dx = x - last.x;
    const dy = y - last.y;
    const distance = Math.hypot(dx, dy);
    const step = Math.max(1, ed.size * 0.22);
    const count = Math.max(1, Math.ceil(distance / step));
    for (let i = 1; i <= count; i++) {
      stampBrush(last.x + dx * i / count, last.y + dy * i / count);
    }
    ed.last = { x, y };
  }
  $('edViewport').setPointerCapture(event.pointerId);
  editorInvalidate();
}

function editorPointerUp(event) {
  if (!ed.open) return;
  if (ed.drawing || ed.panning) {
    try { $('edViewport').releasePointerCapture(event.pointerId); } catch { }
  }
  ed.drawing = false;
  ed.panning = false;
  ed.panStart = null;
  ed.last = null;
}

function stampBrush(x, y) {
  const radius = Math.max(1, ed.size / 2);
  const ctx = ed.mctx;
  const gradient = ctx.createRadialGradient(x, y, 0, x, y, radius);
  const core = Math.max(0, Math.min(0.995, ed.hardness / 100));
  gradient.addColorStop(0, 'rgba(255,255,255,1)');
  if (core > 0.001) gradient.addColorStop(core, 'rgba(255,255,255,1)');
  gradient.addColorStop(1, 'rgba(255,255,255,0)');
  ctx.globalCompositeOperation = ed.tool === 'brush' ? 'source-over' : 'destination-out';
  ctx.fillStyle = gradient;
  ctx.beginPath();
  ctx.arc(x, y, radius, 0, Math.PI * 2);
  ctx.fill();
  ctx.globalCompositeOperation = 'source-over';
}

function snapshotMask() {
  if (!ed.w || !ed.h) return new Uint8Array(0);
  const data = ed.mctx.getImageData(0, 0, ed.w, ed.h).data;
  const alpha = new Uint8Array(ed.w * ed.h);
  for (let i = 0; i < alpha.length; i++) alpha[i] = data[i * 4 + 3];
  return alpha;
}

function restoreMask(alpha) {
  if (!alpha || alpha.length !== ed.w * ed.h) return;
  const image = ed.mctx.createImageData(ed.w, ed.h);
  const data = image.data;
  for (let i = 0; i < alpha.length; i++) {
    const v = alpha[i];
    data[i * 4] = 255;
    data[i * 4 + 1] = 255;
    data[i * 4 + 2] = 255;
    data[i * 4 + 3] = v;
  }
  ed.mctx.putImageData(image, 0, 0);
  editorInvalidate();
}

function pushHistory() {
  if (!ed.w) return;
  ed.history.push(snapshotMask());
  if (ed.history.length > 18) ed.history.shift();
  ed.redo = [];
  $('edStatus').textContent = 'Изменено';
}

function editorUndo() {
  if (ed.history.length <= 1) return;
  ed.redo.push(ed.history.pop());
  restoreMask(ed.history[ed.history.length - 1]);
  $('edStatus').textContent = 'Отменено';
}

function editorRedo() {
  if (!ed.redo.length) return;
  const alpha = ed.redo.pop();
  ed.history.push(alpha);
  restoreMask(alpha);
  $('edStatus').textContent = 'Возвращено';
}

function applyEditorBackground() {
  const stack = $('edStack');
  const colors = { white: '#ffffff', black: '#000000', green: '#00b140' };
  if (ed.bg === 'checker') stack.style.background = '';
  else stack.style.background = colors[ed.bg] || '';
}

function editorExportMask() {
  const canvas = document.createElement('canvas');
  canvas.width = ed.w;
  canvas.height = ed.h;
  const ctx = canvas.getContext('2d');
  const source = ed.mctx.getImageData(0, 0, ed.w, ed.h).data;
  const out = ctx.createImageData(ed.w, ed.h);
  for (let i = 0; i < source.length; i += 4) {
    const alpha = source[i + 3];
    out.data[i] = alpha;
    out.data[i + 1] = alpha;
    out.data[i + 2] = alpha;
    out.data[i + 3] = 255;
  }
  ctx.putImageData(out, 0, 0);
  return canvas.toDataURL('image/png');
}

async function editorApply() {
  if (!ed.open || !ed.img || ed.busy) return;
  const mask = editorExportMask();
  if (mask.length > 7_500_000) {
    toast('Маска получилась слишком большой для передачи. Попробуйте другое изображение.', true);
    return;
  }
  ed.busy = true;
  $('edProgressWrap').hidden = false;
  $('edProgress').style.width = '3%';
  $('edMessage').hidden = true;
  $('edStatus').textContent = 'Сохраняю…';
  const params = {
    path: ed.path,
    mask,
    outputDir: $('edOutput').value.trim() || $('bgOutput').value.trim() || null,
    suffix: $('edSuffix').value || '-cutout',
    overwrite: $('edOverwrite').checked,
    feather: Number($('edFeather').value) || 0,
    refine: $('edRefine').checked
  };
  try {
    const { outputs } = await runJob('bg.apply', params, 'Сохраняю PNG', pct => {
      $('edProgress').style.width = Math.max(3, pct) + '%';
    });
    const output = outputs[0] && outputs[0].output;
    if (output) {
      ed.lastOutput = output;
      $('edStatus').textContent = 'Сохранено: ' + baseName(output);
      $('edMessage').hidden = false;
      $('edMessage').textContent = 'PNG с прозрачностью: ' + output;
      toast('Сохранено: ' + baseName(output));
    }
  } catch (err) {
    if (err.message === 'Отменено.') {
      $('edStatus').textContent = 'Сохранение отменено';
    } else {
      $('edStatus').textContent = 'Ошибка сохранения';
      $('edMessage').hidden = false;
      $('edMessage').textContent = err.message;
      toast(err.message, true);
    }
  } finally {
    ed.busy = false;
    $('edProgressWrap').hidden = true;
  }
}

function closeEditor() {
  if (!ed.open) return;
  if (ed.busy) { toast('Дождитесь завершения операции.', true); return; }
  ed.open = false;
  ed.img = null;
  ed.drawing = false;
  ed.panning = false;
  ed.history = [];
  ed.redo = [];
  ed.path = null;
  $('bgEditor').hidden = true;
  $('edCursor').style.display = 'none';
  $('edStack').style.background = '';
  scheduleSave();
}

/* ------------------------------------------------------------------ */
/* Tab / wiring                                                        */
/* ------------------------------------------------------------------ */

function setTab(name, persist = true) {
  if (name === 'bg') name = 'photo';
  const known = ['photo', 'video', 'audio', 'bg'];
  if (!known.includes(name)) name = 'photo';
  state.tab = name;
  for (const button of $('tabs').querySelectorAll('.tab')) {
    button.classList.toggle('active', button.dataset.tab === name);
  }
  for (const panel of $('tabBody').querySelectorAll('.tab-panel')) {
    panel.hidden = panel.id !== 'tab-' + name;
  }
  if (persist) scheduleSave();
}

async function pickInto(id) {
  try {
    const folder = await call('dialog.folder');
    if (folder) {
      $(id).value = folder;
      scheduleSave();
    }
  } catch (err) {
    toast(err.message, true);
  }
}

function wireForms() {
  $('pickFolder').addEventListener('click', pickFolder);
  $('refresh').addEventListener('click', () => state.folder ? scan() : pickFolder());
  $('recursive').addEventListener('change', () => { state.recursive = $('recursive').checked; scheduleSave(); if (state.folder) scan(); });
  $('photoResize').addEventListener('change', () => { updatePhotoDims(); scheduleSave(); });
  $('photoFormat').addEventListener('change', () => { updatePhotoDims(); scheduleSave(); });
  $('videoResolution').addEventListener('change', () => { updateVideoFields(); scheduleSave(); });
  $('videoPreset').addEventListener('change', () => {
    if ($('videoPreset').value === 'gif' && $('videoFps').value === '0') $('videoFps').value = '15';
    scheduleSave();
  });
  $('photoQuality').addEventListener('input', syncQualityLabels);
  $('videoCrf').addEventListener('input', syncQualityLabels);
  $('selectAll').addEventListener('click', () => {
    for (const item of filteredItems()) state.selected.add(item.path);
    renderList();
    updateSelectionInfo();
  });
  $('clearSelection').addEventListener('click', () => {
    state.selected.clear();
    renderList();
    updateSelectionInfo();
  });
  $('photoRun').addEventListener('click', runPhoto);
  $('videoRun').addEventListener('click', runVideo);
  $('audioRun').addEventListener('click', runAudio);
  $('muxRun').addEventListener('click', runMux);
  $('bgOpen').addEventListener('click', () => {
    const images = currentItems('image');
    if (!images.length) { toast('Сначала выберите изображение.', true); return; }
    openEditor(images[0].path);
  });
  $('bgOutputPick').addEventListener('click', () => pickInto('bgOutput'));
  for (const [button, input] of [['photoOutputPick', 'photoOutput'], ['videoOutputPick', 'videoOutput'], ['audioOutputPick', 'audioOutput'], ['muxOutputPick', 'muxOutput']]) {
    $(button).addEventListener('click', () => pickInto(input));
  }
  $('jobCancel').addEventListener('click', cancelJob);

  $('tabs').addEventListener('click', event => {
    const button = event.target.closest('.tab');
    if (button) setTab(button.dataset.tab);
  });

  $('filterChips').addEventListener('click', event => {
    const chip = event.target.closest('.chip');
    if (!chip) return;
    state.filter = chip.dataset.filter;
    for (const button of $('filterChips').querySelectorAll('.chip')) button.classList.toggle('active', button === chip);
    renderList();
  });

  $('search').addEventListener('input', () => {
    state.search = $('search').value;
    renderList();
  });

  document.addEventListener('change', event => {
    if (event.target.closest('.form, .tool-params, .topbar, .editor-side')) scheduleSave();
  });
  document.addEventListener('input', event => {
    if (event.target.matches('input[type=range]')) scheduleSave();
  });

  document.addEventListener('keydown', event => {
    if (!ed.open) return;
    if (event.key === 'Escape') {
      if (!ed.busy) closeEditor();
      return;
    }
    if (event.target && /^(INPUT|SELECT|TEXTAREA)$/.test(event.target.tagName)) return;
    if (event.key === ' ') {
      ed.space = true;
      $('edViewport').style.cursor = 'grab';
      event.preventDefault();
      return;
    }
    if (event.key === 'b' || event.key === 'B' || event.key === 'и' || event.key === 'И') editorSetTool('brush');
    else if (event.key === 'e' || event.key === 'E' || event.key === 'у' || event.key === 'У') editorSetTool('eraser');
    else if (event.key === '0') editorFit(true);
    else if ((event.ctrlKey || event.metaKey) && (event.key === 'z' || event.key === 'Z' || event.key === 'я')) {
      event.preventDefault();
      if (event.shiftKey) editorRedo(); else editorUndo();
    } else if ((event.ctrlKey || event.metaKey) && (event.key === 'y' || event.key === 'Y' || event.key === 'н')) {
      event.preventDefault();
      editorRedo();
    }
  });
  document.addEventListener('keyup', event => {
    if (event.key === ' ') {
      ed.space = false;
      $('edViewport').style.cursor = '';
    }
  });
}

/* ------------------------------------------------------------------ */
/* Environment                                                         */
/* ------------------------------------------------------------------ */

async function loadEnvironment() {
  try {
    const env = await worker('environment');
    state.env = env;
    const badge = $('engineBadge');
    badge.hidden = false;
    if (env.ffmpegFound) {
      badge.className = 'badge ok';
      badge.textContent = 'ffmpeg готов';
      badge.title = env.ffmpeg || '';
    } else {
      badge.className = 'badge warn';
      badge.textContent = 'ffmpeg не найден';
      badge.title = 'Переустановите ThodMedia, чтобы восстановить движок.';
    }
    const modelOk = !!env.modelFound;
    const modelBadge = $('bgModelBadge');
    modelBadge.textContent = modelOk ? 'IS-Net готова' : 'модель не найдена';
    modelBadge.className = 'badge ' + (modelOk ? 'ok' : 'warn');
    const edBadge = $('edModelBadge');
    edBadge.textContent = modelOk ? 'IS-Net · офлайн' : 'модель не найдена';
    edBadge.className = 'badge ' + (modelOk ? 'ok' : 'warn');
    if (!env.ffmpegFound) setStatus('ffmpeg не найден в пакете — переустановите ThodMedia.', true);
  } catch (err) {
    $('engineBadge').hidden = false;
    $('engineBadge').className = 'badge warn';
    $('engineBadge').textContent = 'движок недоступен';
    $('engineBadge').title = err.message || '';
  }
}

async function loadPresets() {
  const fallback = [
    { id: 'mp4-h264', name: 'MP4 · H.264 + AAC' },
    { id: 'mp4-h265', name: 'MP4 · H.265 + AAC' },
    { id: 'webm-vp9', name: 'WebM · VP9 + Opus' },
    { id: 'mkv-h264', name: 'MKV · H.264 + AAC' },
    { id: 'mov-prores', name: 'MOV · ProRes' },
    { id: 'gif', name: 'GIF' },
    { id: 'copy', name: 'Без перекодирования' }
  ];
  let list = fallback;
  try {
    const presets = await worker('presets');
    if (presets && Array.isArray(presets.video) && presets.video.length) list = presets.video;
  } catch { /* fallback */ }
  const select = $('videoPreset');
  const keep = select.value;
  select.textContent = '';
  for (const preset of list) {
    const option = document.createElement('option');
    option.value = preset.id;
    option.textContent = preset.name + (preset.hint ? ' — ' + preset.hint : '');
    select.append(option);
  }
  if (keep && list.some(p => p.id === keep)) select.value = keep;
}

/* ------------------------------------------------------------------ */
/* Boot                                                                */
/* ------------------------------------------------------------------ */

async function boot() {
  wireForms();
  editorSetup();
  syncQualityLabels();
  updateEditorLabels();

  if (bridgeReady()) {
    try {
      const saved = await call('settings.get');
      applySettings(saved);
    } catch { /* first run */ }
  } else {
    setStatus('ThodMedia запущен вне ThodHUB — интерфейс работает, но движок недоступен.', true);
  }

  state.recursive = $('recursive').checked;
  setTab(state.tab, false);
  updatePhotoDims();
  updateVideoFields();
  syncQualityLabels();
  updateEditorLabels();
  $('edOutput').value = $('edOutput').value || $('bgOutput').value;
  state.settingsReady = true;

  await saveSettings();
  loadEnvironment();
  loadPresets().then(() => {
    updateAudioOptions();
    scheduleSave();
  });

  if (state.folder) await scan();
  else renderList();

  updateSelectionInfo();
}

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
else boot();
