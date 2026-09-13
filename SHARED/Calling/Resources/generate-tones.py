#!/usr/bin/env python3
"""Generate the original LEGEND harmonic bell signature (PCM, no dependencies)."""
from pathlib import Path
import math, struct, wave
RATE = 44100
ROOT = Path(__file__).parent / 'raw'
def render(name, notes, seconds, gain):
    samples = [0.0] * int(seconds * RATE)
    for start, frequency, length, strength in notes:
        for i in range(int(length * RATE)):
            t = i / RATE
            envelope = (1 - math.exp(-t * 90)) * math.exp(-t * 4.2) * min(1, (length-t)*30)
            tone = sum(a * math.sin(2*math.pi*frequency*h*t) for h,a in [(1,1), (2,0.24), (3,0.07)])
            j = int(start*RATE)+i
            if j < len(samples): samples[j] += tone * envelope * strength
    peak = max(abs(x) for x in samples)
    with wave.open(str(ROOT / name), 'wb') as out:
        out.setparams((1,2,RATE,0,'NONE','not compressed'))
        out.writeframes(b''.join(struct.pack('<h',round(x/peak*gain*32767)) for x in samples))
if __name__ == '__main__':
    # Original major-ninth bell voicing: a warm foundation with a clear rising
    # signature. Gentle attacks and silent loop tails avoid clicks and fatigue.
    motif = [(0,329.628,1.8,.65),(0,659.255,1.5,1),
             (.24,830.609,1.5,.8),(.48,987.767,1.6,.85),
             (.82,739.989,1.8,.55),(.82,1318.51,1.8,.6)]
    render('legend_incoming.wav',motif+[(s+3.2,f,d,a*.9) for s,f,d,a in motif],7.2,.62)
    render('legend_ringback.wav',[(0,329.628,1.5,.55),(0,659.255,1.4,.85),
                                (.3,987.767,1.5,.65),(.3,739.989,1.5,.4)],4.2,.34)
