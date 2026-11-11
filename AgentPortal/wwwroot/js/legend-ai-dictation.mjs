export function encodeDictationWave(chunks, sampleRate) {
    if (!Number.isFinite(sampleRate) || sampleRate < 16000 || sampleRate > 192000) throw new Error('Unsupported microphone sample rate.');
    const length = chunks.reduce((sum, chunk) => sum + chunk.length, 0);
    if (length < sampleRate / 10 || length > sampleRate * 60) throw new Error('Record between 0.1 and 60 seconds.');
    const samples = new Float32Array(length);
    let offset = 0;
    for (const chunk of chunks) { samples.set(chunk, offset); offset += chunk.length; }
    const count = Math.floor(length * 16000 / sampleRate);
    const result = new Uint8Array(44 + count * 2);
    const view = new DataView(result.buffer);
    const tag = (at, value) => [...value].forEach((c, i) => view.setUint8(at + i, c.charCodeAt(0)));
    tag(0, 'RIFF'); view.setUint32(4, result.length - 8, true); tag(8, 'WAVE'); tag(12, 'fmt ');
    view.setUint32(16, 16, true); view.setUint16(20, 1, true); view.setUint16(22, 1, true);
    view.setUint32(24, 16000, true); view.setUint32(28, 32000, true); view.setUint16(32, 2, true); view.setUint16(34, 16, true);
    tag(36, 'data'); view.setUint32(40, count * 2, true);
    for (let i = 0; i < count; i++) {
        const from = Math.floor(i * sampleRate / 16000), to = Math.floor((i + 1) * sampleRate / 16000);
        let value = 0;
        for (let j = from; j < to; j++) value += Number.isFinite(samples[j]) ? samples[j] : 0;
        value = Math.max(-1, Math.min(1, value / (to - from)));
        view.setInt16(44 + i * 2, Math.round(value * (value < 0 ? 32768 : 32767)), true);
    }
    return result;
}

export function appendDictation(draft, transcript, maximum = 20000) {
    const combined = draft + (draft && !/\s$/.test(draft) ? ' ' : '') + transcript;
    if (combined.length > maximum) throw new Error('Transcript exceeds the message limit. Your typed text was preserved; record a shorter phrase.');
    return combined;
}
