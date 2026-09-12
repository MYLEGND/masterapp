#!/usr/bin/env python3
"""Generate the original LEGEND ascending glass-chime signature (PCM, no dependencies)."""
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
    motif = [(0,659.255,1.1,1),(.22,830.609,1.1,.85),(.46,987.767,1.4,.8),(.82,1318.51,1.6,.65)]
    render('legend_incoming.wav',motif+[(s+2.8,f,d,a*.85) for s,f,d,a in motif],6.5,.70)
    render('legend_ringback.wav',[(0,659.255,.8,.8),(.28,987.767,1.1,.6)],3.8,.40)
