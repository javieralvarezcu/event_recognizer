// Panel de eventos: calendario FullCalendar + modal de detalle.
// Los eventos llegan de GET /api/events (mismo origen, sin CORS).

const DAY_NAMES = ['lunes', 'martes', 'miércoles', 'jueves', 'viernes', 'sábado', 'domingo']; // índice = n - 1 (1=lunes...7=domingo)

const $ = (id) => document.getElementById(id);

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

init();
