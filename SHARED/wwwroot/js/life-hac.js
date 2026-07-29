(() => {
  const trigger = document.getElementById('psalmsTrigger');
  const modal = document.getElementById('psalmsModal');
  if (!trigger || !modal) return;

  const textEl = document.getElementById('psalmsText');
  const refEl = document.getElementById('psalmsReference');
  const translationEl = document.getElementById('psalmsTranslation');
  const dayEl = document.getElementById('psalmsDay');
  const closeEl = document.getElementById('psalmsClose');
  const cardEl = modal.querySelector('.scripture-card');
  const streakEl = document.getElementById('psalmsStreak');
  const streakNoteEl = document.getElementById('psalmsStreakNote');
  let cachedDailyScripture = null;

  async function loadDailyScripture() {
    if (cachedDailyScripture) return cachedDailyScripture;
    const response = await fetch('/api/shared/daily-scripture', {
      method: 'GET',
      credentials: 'same-origin',
      headers: { Accept: 'application/json' }
    });
    if (!response.ok) throw new Error(`Daily scripture unavailable (${response.status})`);
    cachedDailyScripture = await response.json();
    return cachedDailyScripture;
  }

  function computeStreak(dayKey) {
    const todayTs = Date.parse(`${dayKey}T00:00:00Z`);
    let lastDay = null;
    let lastStreak = 0;
    try {
      lastDay = localStorage.getItem('psalmsLastDay');
      const stored = parseInt(localStorage.getItem('psalmsStreak') || '0', 10);
      if (!Number.isNaN(stored)) lastStreak = stored;
    } catch { }
    let nextStreak = 1;
    if (lastDay) {
      const lastTs = Date.parse(`${lastDay}T00:00:00Z`);
      const diffDays = Math.floor((todayTs - lastTs) / 86400000);
      if (diffDays === 0) nextStreak = Math.max(lastStreak, 1);
      else if (diffDays === 1) nextStreak = Math.max(lastStreak, 0) + 1;
    }
    try {
      localStorage.setItem('psalmsLastDay', dayKey);
      localStorage.setItem('psalmsStreak', String(nextStreak));
    } catch { }
    return nextStreak;
  }

  function applyStreakVisual(streak) {
    const hue = Math.min(120, Math.max(0, streak * 6));
    if (streakEl) {
      streakEl.textContent = `${streak} day${streak === 1 ? '' : 's'}`;
      streakEl.style.color = `hsl(${hue}, 76%, 54%)`;
    }
    if (streakNoteEl) {
      streakNoteEl.textContent = streak >= 7 ? 'On fire!' : streak >= 3 ? 'Keep it going' : 'Day one — let\'s roll';
    }
  }

  async function openModal() {
    trigger.setAttribute('aria-busy', 'true');
    try {
      const daily = await loadDailyScripture();
      dayEl.textContent = daily.date;
      refEl.textContent = daily.reference;
      translationEl.textContent = daily.translation;
      textEl.replaceChildren(...daily.verses.map(verse => {
        const paragraph = document.createElement('p');
        paragraph.textContent = verse;
        return paragraph;
      }));
      applyStreakVisual(computeStreak(daily.date));
      modal.classList.add('open');
      modal.setAttribute('aria-hidden', 'false');
      document.body.classList.add('no-scroll');
      cardEl?.focus({ preventScroll: true });
    } catch (error) {
      console.error('Unable to load the shared daily scripture.', error);
    } finally {
      trigger.removeAttribute('aria-busy');
    }
  }

  function closeModal() {
    modal.classList.remove('open');
    modal.setAttribute('aria-hidden', 'true');
    document.body.classList.remove('no-scroll');
    trigger.focus({ preventScroll: true });
  }

  trigger.addEventListener('click', openModal);
  closeEl?.addEventListener('click', closeModal);
  modal.addEventListener('click', event => { if (event.target === modal) closeModal(); });
  document.addEventListener('keydown', event => {
    if (event.key === 'Escape' && modal.classList.contains('open')) closeModal();
  });
})();
