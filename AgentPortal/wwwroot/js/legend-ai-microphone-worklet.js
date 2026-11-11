// Audio is held only by the active composer recording, never persisted here.
class LegendMicrophoneCapture extends AudioWorkletProcessor {
    process(inputs) {
        const channel = inputs[0]?.[0];
        if (channel) {
            const copy = channel.slice();
            this.port.postMessage(copy, [copy.buffer]);
        }
        return true;
    }
}
registerProcessor('legend-microphone-capture', LegendMicrophoneCapture);
