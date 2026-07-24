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

  const chapters = [
    {
      reference: 'Psalm 23',
      translation: 'KJV (public domain fallback)',
      verses: [
        'The LORD is my shepherd; I shall not want.',
        'He maketh me to lie down in green pastures: he leadeth me beside the still waters.',
        'He restoreth my soul: he leadeth me in the paths of righteousness for his name\'s sake.',
        'Yea, though I walk through the valley of the shadow of death, I will fear no evil: for thou art with me; thy rod and thy staff they comfort me.',
        'Thou preparest a table before me in the presence of mine enemies: thou anointest my head with oil; my cup runneth over.',
        'Surely goodness and mercy shall follow me all the days of my life: and I will dwell in the house of the LORD for ever.'
      ]
    },
    {
      reference: 'Psalm 121',
      translation: 'KJV (public domain fallback)',
      verses: [
        'I will lift up mine eyes unto the hills, from whence cometh my help.',
        'My help cometh from the LORD, which made heaven and earth.',
        'He will not suffer thy foot to be moved: he that keepeth thee will not slumber.',
        'Behold, he that keepeth Israel shall neither slumber nor sleep.',
        'The LORD is thy keeper: the LORD is thy shade upon thy right hand.',
        'The sun shall not smite thee by day, nor the moon by night.',
        'The LORD shall preserve thee from all evil: he shall preserve thy soul.',
        'The LORD shall preserve thy going out and thy coming in from this time forth, and even for evermore.'
      ]
    },
    {
      reference: 'Psalm 1',
      translation: 'KJV (public domain fallback)',
      verses: [
        'Blessed is the man that walketh not in the counsel of the ungodly, nor standeth in the way of sinners, nor sitteth in the seat of the scornful.',
        'But his delight is in the law of the LORD; and in his law doth he meditate day and night.',
        'And he shall be like a tree planted by the rivers of water, that bringeth forth his fruit in his season; his leaf also shall not wither; and whatsoever he doeth shall prosper.',
        'The ungodly are not so: but are like the chaff which the wind driveth away.',
        'Therefore the ungodly shall not stand in the judgment, nor sinners in the congregation of the righteous.',
        'For the LORD knoweth the way of the righteous: but the way of the ungodly shall perish.'
      ]
    },
    {
      reference: 'Psalm 100',
      translation: 'KJV (public domain fallback)',
      verses: [
        'Make a joyful noise unto the LORD, all ye lands.',
        'Serve the LORD with gladness: come before his presence with singing.',
        'Know ye that the LORD he is God: it is he that hath made us, and not we ourselves; we are his people, and the sheep of his pasture.',
        'Enter into his gates with thanksgiving, and into his courts with praise: be thankful unto him, and bless his name.',
        'For the LORD is good; his mercy is everlasting; and his truth endureth to all generations.'
      ]
    }
  ];

  function selectChapter(date) {
    const dayKey = date.toISOString().slice(0, 10);
    let hash = 0;
    for (let i = 0; i < dayKey.length; i += 1) {
      hash = (hash * 31 + dayKey.charCodeAt(i)) >>> 0;
    }
    return { dayKey, chapter: chapters[hash % chapters.length] };
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

  function openModal() {
    const { dayKey, chapter } = selectChapter(new Date());
    dayEl.textContent = dayKey;
    refEl.textContent = chapter.reference;
    translationEl.textContent = chapter.translation;
    textEl.replaceChildren(...chapter.verses.map(verse => {
      const paragraph = document.createElement('p');
      paragraph.textContent = verse;
      return paragraph;
    }));
    applyStreakVisual(computeStreak(dayKey));
    modal.classList.add('open');
    modal.setAttribute('aria-hidden', 'false');
    document.body.classList.add('no-scroll');
    cardEl?.focus({ preventScroll: true });
  }

  function closeModal() {
    modal.classList.remove('open');
    modal.setAttribute('aria-hidden', 'true');
    document.body.classList.remove('no-scroll');
    trigger.focus({ preventScroll: true });
  }

  trigger.addEventListener('click', openModal);
  closeEl?.addEventListener('click', closeModal);
  modal.addEventListener('click', event => {
    if (event.target === modal) closeModal();
  });
  document.addEventListener('keydown', event => {
    if (event.key === 'Escape' && modal.classList.contains('open')) closeModal();
  });
})();
