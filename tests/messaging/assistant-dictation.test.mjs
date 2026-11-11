import { test } from 'node:test';
import assert from 'node:assert/strict';
import { encodeDictationWave, appendDictation } from '../../AgentPortal/wwwroot/js/legend-ai-dictation.mjs';

test('48kHz microphone audio becomes bounded 16kHz mono PCM', () => {
    const wave = encodeDictationWave([new Float32Array(4800).fill(0.5)], 48000);
    const view = new DataView(wave.buffer);
    assert.equal(wave.length, 3244);
    assert.equal(view.getUint32(24, true), 16000);
    assert.equal(view.getInt16(44, true), 16384);
    assert.equal(new TextDecoder().decode(wave.subarray(0, 4)), 'RIFF');
});
test('rejects oversized, empty and unsupported capture without truncation', () => {
    assert.throws(() => encodeDictationWave([], 48000));
    assert.throws(() => encodeDictationWave([new Float32Array(960001)], 16000));
    assert.throws(() => encodeDictationWave([new Float32Array(1600)], 8000));
});
test('transcript appends to current typed draft and never truncates it', () => {
    assert.equal(appendDictation('typed while recording', 'spoken words'), 'typed while recording spoken words');
    assert.equal(appendDictation('draft\n', 'spoken'), 'draft\nspoken');
    assert.throws(() => appendDictation('keep me', 'long transcript', 10));
});
