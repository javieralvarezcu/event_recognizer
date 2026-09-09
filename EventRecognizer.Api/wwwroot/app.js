// Panel de eventos: calendario FullCalendar + modal de detalle.
// Los eventos llegan de GET /api/events (mismo origen, sin CORS).

const DAY_NAMES = ['lunes', 'martes', 'miércoles', 'jueves', 'viernes', 'sábado', 'domingo']; // índice = n - 1 (1=lunes...7=domingo)

const $ = (id) => document.getElementById(id);

/** Mes visible en el calendario, "YYYY-MM". Se actualiza en cada datesSet. */
let currentMonth = null;

function escapeHtml(text) {
  return String(text)
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}

/** "2026-09-19T20:00:00Z" -> "2026-09-19" (fecha UTC, sin desplazamientos de zona). */
function toDateOnly(dt) {
  return dt ? String(dt).slice(0, 10) : null;
}

/** Suma días a una fecha "YYYY-MM-DD" usando aritmética local (sin zona horaria). */
function addDays(dateOnly, n) {
  const [y, m, d] = dateOnly.split('-').map(Number);
  const date = new Date(y, m - 1, d + n);
  const pad = (x) => String(x).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

/** "2026-09-19" -> "19 de septiembre de 2026" (constructor local, nunca new Date(str)). */
function formatDateOnly(dateOnly) {
  const [y, m, d] = dateOnly.split('-').map(Number);
  return new Date(y, m - 1, d).toLocaleDateString('es-ES', {
    day: 'numeric',
    month: 'long',
    year: 'numeric'
  });
}

/**
 * Convierte un DTO de evento en un objeto FullCalendar, o null si no tiene
 * ninguna fecha computable (entonces se muestra en "Eventos sin fecha").
 * Todo se renderiza allDay con fechas "YYYY-MM-DD" para evitar desplazamientos UTC.
 */
function buildCalendarEvent(e) {
  const base = { title: e.title || 'Sin título', allDay: true, extendedProps: e };

  if (e.recurrenceType === 'weekly') {
    const days = (e.recurrenceDaysOfWeek || '')
      .split(',')
      .map((s) => parseInt(s, 10))
      .filter(Number.isInteger);
    const startRecur = toDateOnly(e.recurrenceStartDate) || toDateOnly(e.eventDate);
    if (days.length === 0 || !startRecur) return null;

    base.start = startRecur;
    base.startRecur = startRecur;
    base.daysOfWeek = days.map((n) => n % 7); // BD: 1=lunes...7=domingo -> FC: 0=domingo...6=sábado
    const endRecur = toDateOnly(e.recurrenceEndDate);
    if (endRecur && endRecur > startRecur) base.endRecur = endRecur; // si end <= start, FC no renderiza nada
  } else if ((e.recurrenceType === 'daily' || e.isRecurrent) &&
             (e.recurrenceStartDate || e.recurrenceEndDate)) {
    // Rango de varios días ("daily" u otro recurrente con tramo): un único evento que abarca el tramo.
    const start = toDateOnly(e.recurrenceStartDate) || toDateOnly(e.eventDate);
    const end = toDateOnly(e.recurrenceEndDate);
    if (!start && !end) return null;
    base.start = start || end;
    // El final de FullCalendar es exclusivo: "del 10 al 12" necesita end = día 13.
    base.end = end && start && end > start ? addDays(end, 1) : addDays(base.start, 1);
  } else {
    const start = toDateOnly(e.eventDate);
    if (!start) return null;
    base.start = start;
  }

  return base;
}

/** Humaniza la recurrencia en español para el modal. */
function formatRecurrence(e) {
  const desde = e.recurrenceStartDate ? `, desde ${formatDateOnly(toDateOnly(e.recurrenceStartDate))}` : '';
  const hasta = e.recurrenceEndDate ? `, hasta ${formatDateOnly(toDateOnly(e.recurrenceEndDate))}` : '';

  if (e.recurrenceType === 'weekly') {
    const days = (e.recurrenceDaysOfWeek || '')
      .split(',')
      .map((s) => parseInt(s, 10))
      .filter((n) => Number.isInteger(n) && n >= 1 && n <= 7);
    if (days.length > 0) {
      return `Semanal: ${days.map((n) => DAY_NAMES[n - 1]).join(', ')}${desde}${hasta}`;
    }
    return `Semanal${desde}${hasta}`;
  }
  if (e.recurrenceType === 'daily') {
    if (e.recurrenceStartDate && e.recurrenceEndDate) {
      return `Diario, del ${formatDateOnly(toDateOnly(e.recurrenceStartDate))} al ${formatDateOnly(toDateOnly(e.recurrenceEndDate))}`;
    }
    return `Diario${desde}${hasta}`;
  }
  if (e.isRecurrent) {
    return `Evento recurrente${desde}${hasta}`;
  }
  return 'No es recurrente';
}

function openModal(e) {
  $('modal-title').textContent = e.title || 'Sin título';
  $('modal-summary').textContent = e.summary || '—';
  $('modal-date-description').textContent = e.eventDateDescription || '—';
  $('modal-account').textContent = e.account || '—';
  $('modal-recurrence').textContent = formatRecurrence(e);

  const dateOnly = toDateOnly(e.eventDate) || toDateOnly(e.recurrenceStartDate);
  $('modal-date').textContent = dateOnly ? formatDateOnly(dateOnly) : '—';

  $('modal-post-datetime').textContent = e.postDatetime
    ? new Date(e.postDatetime).toLocaleString('es-ES', { dateStyle: 'long', timeStyle: 'short' })
    : '—';

  const link = $('modal-url');
  if (e.url) {
    link.href = e.url;
    link.classList.remove('hidden');
  } else {
    link.classList.add('hidden');
  }

  const img = $('modal-image');
  const fallback = $('modal-image-fallback');
  fallback.classList.add('hidden');
  if (e.imageUrl) {
    img.classList.remove('hidden');
    img.onerror = () => {
      img.classList.add('hidden');
      fallback.classList.remove('hidden');
    };
    img.src = e.imageUrl;
  } else {
    img.classList.add('hidden');
  }

  $('modal-caption').textContent = e.caption || '';

  $('modal-overlay').classList.remove('hidden');
}

function closeModal() {
  $('modal-overlay').classList.add('hidden');
}

function renderNoDateList(events) {
  const list = $('no-date-list');
  list.replaceChildren();
  const noDateEvents = events.filter((e) => buildCalendarEvent(e) === null);
  $('no-date-empty').classList.toggle('hidden', noDateEvents.length > 0);

  for (const e of noDateEvents) {
    const li = document.createElement('li');
    const title = document.createElement('div');
    title.className = 'no-date-title';
    title.textContent = e.title || 'Sin título';
    const summary = document.createElement('p');
    summary.className = 'no-date-summary';
    summary.textContent = e.summary || '';
    li.append(title, summary);
    li.addEventListener('click', () => openModal(e));
    list.appendChild(li);
  }
}

function renderCalendar(events) {
  const calendarEl = $('calendar');
  if (window.calendarInstance) {
    window.calendarInstance.destroy();
  }

  const calendar = new FullCalendar.Calendar(calendarEl, {
    initialView: 'dayGridMonth',
    locale: 'es',
    firstDay: 1, // la semana empieza en lunes
    headerToolbar: {
      left: 'prev,next today',
      center: 'title',
      right: 'dayGridMonth,listMonth'
    },
    buttonText: { today: 'Hoy', month: 'Mes', list: 'Lista' },
    displayEventEnd: false,
    events: events.map(buildCalendarEvent).filter(Boolean),
    datesSet: (info) => {
      // El mes mostrado = el del punto medio del rango visible (el inicio del rango
      // puede caer en el mes anterior por el primer día de la semana).
      const mid = new Date((info.start.getTime() + info.end.getTime()) / 2);
      currentMonth = `${mid.getFullYear()}-${String(mid.getMonth() + 1).padStart(2, '0')}`;
    },
    eventClick: (info) => openModal(info.event.extendedProps),
    eventContent: (arg) => {
      const lines = [escapeHtml(arg.event.title)];
      const endStr = arg.event.endStr;
      // Solo los eventos con rango explícito muestran "inicio – fin"
      // (un evento de un día tiene end = start + 1, y no se muestra).
      if (endStr && addDays(arg.event.startStr, 1) !== endStr) {
        lines.push(`${formatDateOnly(arg.event.startStr)} – ${formatDateOnly(addDays(endStr, -1))}`);
      } else if (arg.event.extendedProps.recurrenceType === 'weekly') {
        lines.push('Semanal');
      }
      return { html: lines.join('<br>') };
    }
  });

  calendar.render();
  window.calendarInstance = calendar;
}

function showError(message) {
  $('error-message').textContent = message;
  $('error-banner').classList.remove('hidden');
}

async function init() {
  $('error-banner').classList.add('hidden');

  if (typeof FullCalendar === 'undefined') {
    showError('No se pudo cargar la librería del calendario (se necesita acceso a Internet).');
    return;
  }

  try {
    const res = await fetch('/api/events');
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const events = await res.json();
    renderCalendar(events);
    renderNoDateList(events);
  } catch {
    showError('No se pudieron cargar los eventos. Comprueba que la API está en marcha.');
  }
}

// Modal: cerrar con botón, clic fuera o Escape.
$('modal-close').addEventListener('click', closeModal);
$('modal-overlay').addEventListener('click', (event) => {
  if (event.target === $('modal-overlay')) closeModal();
});
document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape') closeModal();
});
$('retry-button').addEventListener('click', init);

// --- Limpieza del mes (duplicados) ---
const cleanupKeyInput = $('cleanup-key');
const cleanupButton = $('cleanup-button');
const cleanupResult = $('cleanup-result');

cleanupKeyInput.value = localStorage.getItem('deepseekApiKey') || '';

function showCleanupResult(kind, title, lines) {
  cleanupResult.replaceChildren();
  cleanupResult.className = kind;
  const heading = document.createElement('p');
  heading.textContent = title;
  cleanupResult.appendChild(heading);
  if (lines && lines.length > 0) {
    const list = document.createElement('ul');
    for (const line of lines) {
      const item = document.createElement('li');
      item.textContent = line;
      list.appendChild(item);
    }
    cleanupResult.appendChild(list);
  }
}

async function runCleanup() {
  const key = cleanupKeyInput.value.trim();
  if (!key) {
    cleanupKeyInput.focus();
    showCleanupResult('error', 'Introduce tu API key de DeepSeek para poder limpiar el mes.');
    return;
  }
  localStorage.setItem('deepseekApiKey', key);

  if (!currentMonth) {
    showCleanupResult('error', 'No se pudo determinar el mes visible del calendario.');
    return;
  }

  const [year, month] = currentMonth.split('-').map(Number);
  const monthName = new Date(year, month - 1, 1).toLocaleDateString('es-ES', { month: 'long', year: 'numeric' });
  if (!confirm(
    `¿Limpiar ${monthName}? Se enviarán los eventos de ese mes (y los eventos sin fecha) al LLM, ` +
    'que decidirá cuáles son duplicados y los eliminará conservando uno por evento.')) {
    return;
  }

  cleanupButton.disabled = true;
  cleanupButton.textContent = 'Analizando…';
  showCleanupResult('ok', 'Analizando con el LLM…');
  try {
    const res = await fetch(`/api/events/cleanup?month=${currentMonth}`, {
      method: 'POST',
      headers: { 'X-DeepSeek-API-Key': key }
    });
    if (!res.ok) {
      let detail = `HTTP ${res.status}`;
      try {
        const err = await res.json();
        detail = err.detail || err.error || detail;
      } catch {
        /* respuesta sin JSON */
      }
      showCleanupResult('error', `La limpieza falló: ${detail}`);
      return;
    }

    const result = await res.json();
    const lines = [];
    if (result.deletedCount === 0) {
      lines.push('El LLM no encontró duplicados entre los eventos analizados.');
    }
    for (const group of result.groups || []) {
      const removed = group.removed
        .map((e) => `«${e.title || e.eventUniqueId}» (@${e.account || 'cuenta desconocida'})`)
        .join(', ');
      const reason = group.reason ? ` — ${group.reason}` : '';
      lines.push(`Se conserva «${group.keepTitle || group.keepEventId}»; eliminados: ${removed}${reason}`);
    }
    showCleanupResult('ok',
      `Limpieza de ${monthName}: ${result.eventsAnalyzed} eventos analizados, ` +
      `${result.deletedCount} duplicados eliminados.`, lines);
    await init(); // refresca calendario y lista de sin fecha
  } catch {
    showCleanupResult('error', 'No se pudo contactar con la API.');
  } finally {
    cleanupButton.disabled = false;
    cleanupButton.textContent = 'Limpiar mes';
  }
}

cleanupButton.addEventListener('click', runCleanup);

init();
