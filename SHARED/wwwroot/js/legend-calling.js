/* Browser media adapter for the existing authenticated MessagingHub.Call authority.
   No call policy, account identity, or recipient authorization is owned here. */
(() => {
  'use strict';
  const stop = stream => stream?.getTracks().forEach(track => { track.onended = null; track.stop(); });
  class LegendBrowserCalling {
    constructor({ connection, deviceId, isActor, present, media, failure, warning }) {
      Object.assign(this, { connection, deviceId, isActor, present, media, failure, warning });
      this.retired = false; this.generation = 0; this.events = Promise.resolve(); this.localCandidates = []; this.remoteCandidates = [];
      this.finished = new Set(); this.quality = 2; this.healthySamples = 0;
      this.connection.on('callUpdated', event => {
        this.events = this.events.then(async () => {
          const scope = { id: event.call?.id, generation: this.generation };
          try { await this.receive(event); } catch (error) { await this.fail(error, scope); }
        });
      });
    }
    get caller() { return this.call?.status === 'preparing' || (this.call && this.isActor(this.call.callerUserId, this.call.callerType, this.call.callerUserIds)); }
    scope() { return { id: this.call?.id, generation: this.generation }; }
    current(scope) { return !this.retired && scope.generation === this.generation && scope.id === this.call?.id; }
    applySnapshot(call, scope) {
      if (!this.current(scope) || !call || call.id !== scope.id) return false;
      if (['ended', 'declined', 'missed'].includes(call.status)) {
        this.close(); if (call.failureMessage) this.failure(call.failureMessage); return false;
      }
      const rank = { preparing: 0, ringing: 1, connecting: 2, active: 3, ended: 4, declined: 4, missed: 4 };
      if (call.epoch < (this.call.epoch || 0) || rank[call.status] < rank[this.call.status] ||
          (this.call.receivedUtc && !call.receivedUtc && call.status === 'ringing')) return false;
      this.call = call; return true;
    }
    async command(action, extra = {}, scope = this.scope()) {
      if (this.retired) throw new Error('This account session has ended.');
      let timer, timedOut = false;
      const request = { action, deviceId: this.deviceId, callId: scope.id, ...extra };
      const timeout = Math.min(30000, Math.max(1000, Number(this.connection.serverTimeoutInMilliseconds) || 12000));
      const invocation = Promise.resolve().then(() => {
        if (this.retired) throw new Error('This account session has ended.');
        return this.connection.invoke('Call', request);
      });
      // A timed-out accept/invite can still succeed on the server. Compensate
      // only that original call; never retry the consequential action itself.
      invocation.then(result => {
        if (timedOut && result?.succeeded && ['accept', 'invite'].includes(action))
          this.command(action === 'invite' ? 'cancel' : 'end',
            { callId: request.callId, conversationId: request.conversationId }, scope).catch(() => {});
      }, () => {});
      const result = await Promise.race([invocation, new Promise((_, reject) => {
        timer = setTimeout(() => { timedOut = true; reject(new Error('The calling service did not respond.')); }, timeout);
      })]).finally(() => clearTimeout(timer));
      if (!result?.succeeded) throw new Error(result?.error || 'The call could not be completed.');
      if (result.policy && this.current(scope)) this.policy = result.policy;
      return result;
    }
    async sync() {
      if (this.retired) return;
      const scope = this.scope();
      const result = await this.command('sync', {}, scope);
      if (!this.current(scope)) return;
      for (const call of result.activeCalls || []) {
        if (this.generation !== scope.generation) return;
        await this.receive({ call });
      }
      if (this.current(scope) && this.call && !(result.activeCalls || []).some(call => call.id === scope.id)) this.close();
    }
    async start(conversationId, video, recipientName = '') {
      if (this.retired) throw new Error('This account session has ended.');
      if (this.call) throw new Error('A call is already in progress.');
      if (!navigator.mediaDevices?.getUserMedia || !globalThis.RTCPeerConnection) throw new Error('Calling requires a supported secure browser.');
      ++this.generation;
      const id = crypto.randomUUID();
      this.call = { id, conversationId, video, status: 'preparing', callerDeviceId: this.deviceId, calleeName: recipientName };
      this.present(this.call, true);
      const scope = this.scope();
      let stream, transferred = false;
      try {
        stream = await navigator.mediaDevices.getUserMedia({ audio: true, video });
        if (!this.current(scope)) return;
        this.stream = stream; transferred = true;
        const result = await this.command('invite', { callId: id, conversationId, video }, scope);
        if (!this.current(scope)) { await this.command('cancel', { callId: id, conversationId }, scope); return; }
        if (this.applySnapshot(result.call, scope)) { this.present(this.call, this.caller); this.armLease(scope); }
      } catch (error) { await this.fail(error, scope); }
      finally { if (stream && !transferred) stop(stream); }
    }
    async receive(event) {
      if (this.retired) return;
      const call = event.call;
      if (!call || this.finished.has(call.id) || (this.call && call.id !== this.call.id)) return;
      if (!this.call && call.status !== 'ringing') return;
      if (!Number.isFinite(Date.parse(call.expiresUtc)) || Date.parse(call.expiresUtc) <= Date.now()) {
        if (this.call) this.close(); return;
      }
      const caller = this.isActor(call.callerUserId, call.callerType, call.callerUserIds);
      if (caller && call.callerDeviceId !== this.deviceId) return;
      if (!caller && !this.isActor(call.calleeUserId, call.calleeType, call.calleeUserIds)) return;
      if (event.toDeviceId && event.toDeviceId !== this.deviceId) return;
      if (event.fromDeviceId === this.deviceId) return;
      if (!caller && call.calleeDeviceId && call.calleeDeviceId !== this.deviceId) { if (this.call) this.close(); return; }
      if (['ended', 'declined', 'missed'].includes(call.status)) {
        if (this.call) { this.close(); if (call.failureMessage) this.failure(call.failureMessage); } return;
      }
      const scope = { id: call.id, generation: this.generation };
      if (this.call) { if (!this.applySnapshot(call, scope)) return; }
      else this.call = call;
      this.present(call, caller); this.armLease(scope);
      if (!caller && call.status === 'ringing' && !call.receivedUtc && this.receipt !== call.id) {
        this.receipt = call.id;
        try { await this.command('received', {}, scope); }
        catch (error) { if (this.current(scope)) this.receipt = null; throw error; }
        if (!this.current(scope)) return;
      }
      if (call.status !== 'ringing') {
        if (!this.policy) await this.command('get', {}, scope);
        if (!this.current(scope)) return;
        await this.ensurePeer(scope);
        if (!this.current(scope)) return;
        if (caller && !this.offered && call.status === 'connecting') await this.offer(false, scope);
        if (this.current(scope) && event.signalKind) await this.signal(event, scope);
      }
    }
    accept() {
      if (this.retired) return Promise.resolve();
      if (this.accepting) return this.accepting;
      if (!this.call || this.caller || this.call.status !== 'ringing') return Promise.resolve();
      const scope = this.scope(), call = this.call;
      let task;
      task = (async () => {
        let stream, transferred = false;
        try {
          stream = await navigator.mediaDevices.getUserMedia({ audio: true, video: call.video });
          if (!this.current(scope)) return;
          this.stream = stream; transferred = true;
          const result = await this.command('accept', {}, scope);
          if (!this.current(scope)) { await this.command('end', { callId: call.id }, scope); return; }
          await this.receive({ call: result.call });
        } catch (error) { await this.fail(error, scope); }
        finally {
          if (stream && !transferred) stop(stream);
          if (this.accepting === task) this.accepting = null;
        }
      })();
      this.accepting = task;
      return task;
    }
    async ensurePeer(scope = this.scope()) {
      if (!this.current(scope)) return;
      if (this.peerSetup) return this.peerSetup;
      if (this.peer) return;
      if (!this.stream) throw new Error('Reopen the call and allow microphone access.');
      if (!this.policy) throw new Error('Call settings are unavailable.');
      const servers = (this.policy.stunUrls || []).map(url => ({ urls: url }));
      if (this.policy.relay) servers.push({ urls: this.policy.relay.urls, username: this.policy.relay.username, credential: this.policy.relay.credential });
      const peer = this.peer = new RTCPeerConnection({ iceServers: servers, bundlePolicy: 'max-bundle' });
      const current = () => this.current(scope) && this.peer === peer;
      this.stream.getTracks().forEach(track => peer.addTrack(track, this.stream));
      if (!this.stream.getVideoTracks().length) peer.addTransceiver('video', { direction: 'sendrecv' });
      const remote = this.remoteStream = new MediaStream();
      peer.ontrack = event => {
        if (!current()) return;
        for (const track of [...(event.streams || []).flatMap(stream => stream.getTracks()), event.track]) {
          if (track && !remote.getTracks().some(existing => existing === track || existing.id === track.id)) remote.addTrack(track);
        }
        this.media(remote, this.stream, Boolean(this.remoteScreenSharing));
      };
      peer.onicecandidate = event => {
        if (!event.candidate || !current()) return;
        const ufrag = event.candidate.usernameFragment || /(?:^|\s)ufrag\s+(\S+)/.exec(event.candidate.candidate || '')?.[1];
        if (this.localIceUfrags?.size && (!ufrag || !this.localIceUfrags.has(ufrag))) return;
        if (this.localCandidates.length >= 256) { this.fail(new Error('The ICE candidate limit was exceeded.'), scope); return; }
        if (this.localEpoch !== null && this.localEpoch !== undefined)
          this.localCandidates.push({ epoch: this.localEpoch, candidate: event.candidate.toJSON() });
        if (this.localReady) this.flushCandidates(scope).catch(error => this.fail(error, scope));
      };
      peer.onconnectionstatechange = () => {
        if (!current()) return;
        if (peer.connectionState === 'connected') {
          clearTimeout(this.connectTimer); clearTimeout(this.recovery); this.recovery = null; this.recoveries = 0;
          if (this.connectedReceipt) return;
          this.connectedReceipt = this.command('connected', {}, scope).then(async result => {
            if (current() && this.applySnapshot(result.call, scope)) { this.present(this.call, this.caller); this.armLease(scope); }
            if (current()) await this.sendMediaState(scope, true);
          }).catch(error => this.fail(error, scope)).finally(() => { if (current()) this.connectedReceipt = null; });
        } else if (['failed', 'disconnected'].includes(peer.connectionState)) this.recover(scope);
      };
      this.media(null, this.stream);
      this.scheduleHeartbeat(scope);
      this.armConnectionDeadline(scope);
      const task = this.applyMediaPolicy(scope);
      this.scheduleQualitySample(scope);
      this.peerSetup = task;
      try { await task; } finally { if (this.peerSetup === task) this.peerSetup = null; }
    }
    mediaLimits() {
      const policy = this.policy, tuning = policy?.adaptation;
      if (!policy) return null;
      const cellular = navigator.connection?.type === 'cellular' || navigator.connection?.saveData === true;
      const level = cellular ? Math.min(this.quality, 1) : this.quality;
      if (this.display && policy.screenShare) {
        const prefix = ['low', 'medium', 'high'][level], sharing = policy.screenShare;
        let bitrate = sharing[prefix + 'Bitrate'];
        if (Number.isFinite(this.availableBandwidth) && this.availableBandwidth >= 0)
          bitrate = Math.max(0, Math.min(bitrate, Math.floor(this.availableBandwidth * (1 - Math.max(0, Math.min(1, sharing.transportHeadroomFraction))) - policy.audioBitrate)));
        return { width: sharing[prefix + 'Width'], height: sharing[prefix + 'Height'], fps: sharing[prefix + 'Fps'], bitrate };
      }
      const prefix = ['low', 'medium'][level];
      if (prefix && tuning) return { width: cellular ? Math.min(tuning[prefix + 'Width'], policy.cellularWidth) : tuning[prefix + 'Width'], height: cellular ? Math.min(tuning[prefix + 'Height'], policy.cellularHeight) : tuning[prefix + 'Height'], fps: cellular ? Math.min(tuning[prefix + 'Fps'], policy.cellularFps) : tuning[prefix + 'Fps'], bitrate: tuning[prefix + 'Bitrate'] };
      const network = cellular ? 'cellular' : 'wifi';
      return { width: policy[network + 'Width'], height: policy[network + 'Height'], fps: policy[network + 'Fps'], bitrate: policy.videoBitrate };
    }
    async applyMediaPolicy(scope = this.scope()) {
      if (!this.current(scope) || !this.peer || !this.policy) return;
      if (this.policyApply) { await this.policyApply; return this.applyMediaPolicy(scope); }
      const peer = this.peer;
      const task = (async () => {
        const limits = this.mediaLimits(), video = (this.display || this.stream)?.getVideoTracks()[0];
        if (video?.applyConstraints && limits && Number.isFinite(limits.width)) {
          const settings = video.getSettings();
          const portrait = this.display && settings.height > settings.width;
          const boundWidth = portrait ? Math.min(limits.width, limits.height) : limits.width;
          const boundHeight = portrait ? Math.max(limits.width, limits.height) : limits.height;
          const scale = settings.width > 0 && settings.height > 0 ? Math.min(1, boundWidth / settings.width, boundHeight / settings.height) : 1;
          const width = settings.width > 0 ? Math.max(2, Math.floor(settings.width * scale / 2) * 2) : boundWidth;
          const height = settings.height > 0 ? Math.max(2, Math.floor(settings.height * scale / 2) * 2) : boundHeight;
          await video.applyConstraints({ width: { ideal: width, max: boundWidth }, height: { ideal: height, max: boundHeight }, frameRate: { max: limits.fps } });
        }
        for (const sender of peer.getSenders()) {
          if (!this.current(scope) || this.peer !== peer) return;
          const audio = sender.track?.kind === 'audio';
          const params = sender.getParameters();
          if (!params.encodings?.length) continue;
          if (!audio) params.degradationPreference = this.display ? 'maintain-resolution' : 'balanced';
          params.encodings.forEach(encoding => {
            encoding.maxBitrate = audio ? this.policy.audioBitrate : Math.max(1, limits.bitrate);
            encoding.active = audio || limits.bitrate > 0;
            encoding.priority = audio && (this.policy.adaptation?.audioPriority || 1) > 1 ? 'high' : 'low';
            if (!audio && Number.isFinite(limits.fps)) encoding.maxFramerate = limits.fps;
          });
          await sender.setParameters(params);
        }
      })();
      this.policyApply = task;
      try { await task; } finally { if (this.policyApply === task) this.policyApply = null; }
    }
    scheduleQualitySample(scope) {
      clearTimeout(this.qualityTimer);
      const tuning = this.policy?.adaptation;
      if (!this.current(scope) || !tuning || !this.peer?.getStats) return;
      this.qualityTimer = setTimeout(async () => {
        const peer = this.peer;
        if (!this.current(scope) || !peer) return;
        try {
          const report = await peer.getStats();
          if (!this.current(scope) || this.peer !== peer) return;
          const transport = [...report.values()].find(row => row.type === 'transport' && row.selectedCandidatePairId);
          const pair = transport && report.get(transport.selectedCandidatePairId);
          const bandwidth = pair?.availableOutgoingBitrate, latency = pair?.currentRoundTripTime;
          const validBandwidth = Number.isFinite(bandwidth) && bandwidth >= 0;
          if (validBandwidth) this.availableBandwidth = bandwidth;
          const target = Number.isFinite(latency) && latency > tuning.highLatencySeconds ? 0
            : validBandwidth ? (bandwidth < tuning.lowBandwidth ? 0 : bandwidth < tuning.highBandwidth ? 1 : 2) : null;
          if (target === null) this.healthySamples = 0;
          else if (target < this.quality) { this.quality = target; this.healthySamples = 0; }
          else if (target > this.quality) { if (++this.healthySamples >= tuning.recoverySamples) { this.quality++; this.healthySamples = 0; } }
          else this.healthySamples = 0;
          await this.applyMediaPolicy(scope);
        } catch (error) {
          if (this.current(scope)) { this.healthySamples = 0; this.warning?.('The browser could not apply the call quality settings.'); }
        } finally { if (this.current(scope)) this.scheduleQualitySample(scope); }
      }, Math.max(1, tuning.sampleSeconds) * 1000);
    }
    scheduleHeartbeat(scope) {
      clearTimeout(this.heartbeat);
      this.heartbeat = setTimeout(async () => {
        if (!this.current(scope)) return;
        try {
          const result = await this.command('heartbeat', {}, scope);
          if (!this.current(scope)) return;
          if (this.applySnapshot(result.call, scope)) this.armLease(scope);
          if (this.current(scope)) this.scheduleHeartbeat(scope);
        } catch (error) { await this.fail(error, scope); }
      }, 20000);
    }
    armConnectionDeadline(scope) {
      clearTimeout(this.connectTimer);
      const seconds = Number(this.policy?.connectSeconds);
      if (!Number.isFinite(seconds) || seconds <= 0 || seconds > 120) throw new Error('The connection deadline is invalid.');
      this.connectTimer = setTimeout(() => {
        if (this.current(scope) && this.peer?.connectionState !== 'connected') this.recover(scope);
      }, seconds * 1000);
    }
    async offer(restart, scope = this.scope()) {
      if (!this.current(scope) || this.negotiating || !this.peer) return;
      this.negotiating = true;
      const peer = this.peer;
      try {
        this.localReady = false; this.localEpoch = null; this.localCandidates = [];
        const description = await peer.createOffer({ iceRestart: restart });
        if (!this.current(scope)) return;
        const epoch = this.call.epoch + 1;
        this.localEpoch = epoch;
        this.localIceUfrags = new Set([...description.sdp.matchAll(/^a=ice-ufrag:(\S+)/gm)].map(match => match[1]));
        await peer.setLocalDescription(description);
        if (!this.current(scope)) return;
        const result = await this.command('signal', { signalKind: 'offer', signalData: description.sdp, epoch }, scope);
        if (!this.current(scope)) return;
        this.applySnapshot(result.call, scope); this.offered = true; this.localReady = true;
        await this.flushCandidates(scope);
      } finally { if (this.current(scope)) this.negotiating = false; }
    }
    async flushCandidates(scope = this.scope()) {
      if (!this.current(scope) || this.flushing || !this.localReady) return;
      this.flushing = true;
      try {
        while (this.current(scope) && this.localCandidates.length && this.localReady) {
          const item = this.localCandidates.shift();
          if (item.epoch !== this.call.epoch) continue;
          await this.command('signal', { signalKind: 'candidate', signalData: JSON.stringify(item.candidate), epoch: item.epoch }, scope);
        }
      } finally { if (this.current(scope)) this.flushing = false; }
    }
    async signal(event, scope = this.scope()) {
      if (!this.current(scope) || !this.peer) return;
      const peer = this.peer, epoch = event.call.epoch;
      if (event.signalKind === 'candidate') {
        const candidate = JSON.parse(event.signalData);
        if (this.remoteEpoch === epoch) await peer.addIceCandidate(candidate);
        else if (this.remoteCandidates.length < 256) this.remoteCandidates.push({ epoch, candidate });
        return;
      }
      if (event.signalKind === 'restart') { if (this.caller) await this.offer(true, scope); return; }
      if (event.signalKind === 'media-state') {
        let state;
        try { state = JSON.parse(event.signalData); } catch (_) { return; }
        if (!state || typeof state !== 'object' || typeof state.screenSharing !== 'boolean' ||
            (state.request !== undefined && typeof state.request !== 'boolean')) return;
        this.remoteScreenSharing = state.screenSharing;
        this.media(this.remoteStream || null, this.stream, state.screenSharing);
        if (state.request === true) await this.sendMediaState(scope);
        return;
      }
      if (!['offer', 'answer'].includes(event.signalKind) || (event.signalKind === 'offer') === Boolean(this.caller)) return;
      const descriptionIdentity = JSON.stringify([event.signalKind, epoch, event.signalData]);
      if (this.remoteDescriptionIdentity === descriptionIdentity) return;
      if (this.remoteEpoch === epoch && this.remoteDescriptionIdentity)
        throw new Error('The call received conflicting session descriptions for one epoch.');
      if (event.signalKind === 'offer') {
        this.localReady = false; this.localEpoch = null; this.localCandidates = [];
      }
      await peer.setRemoteDescription({ type: event.signalKind, sdp: event.signalData });
      if (!this.current(scope)) return;
      this.remoteEpoch = epoch; this.remoteDescriptionIdentity = descriptionIdentity;
      for (const item of this.remoteCandidates.splice(0)) {
        if (!this.current(scope)) return;
        if (item.epoch === epoch) await peer.addIceCandidate(item.candidate);
      }
      if (event.signalKind === 'offer' && this.current(scope)) {
        this.localReady = false;
        const answer = await peer.createAnswer();
        if (!this.current(scope)) return;
        this.localEpoch = epoch;
        this.localIceUfrags = new Set([...answer.sdp.matchAll(/^a=ice-ufrag:(\S+)/gm)].map(match => match[1]));
        await peer.setLocalDescription(answer);
        if (!this.current(scope)) return;
        await this.command('signal', { signalKind: 'answer', signalData: answer.sdp, epoch }, scope);
        if (!this.current(scope)) return;
        this.localReady = true; await this.flushCandidates(scope);
      }
    }
    recover(scope = this.scope()) {
      if (!this.current(scope) || this.recovery) return;
      const attempts = Number(this.policy?.recoveryAttempts);
      if (!Number.isInteger(attempts) || attempts < 0 || attempts > 10 || (this.recoveries || 0) >= attempts) {
        this.fail(new Error('The network could not reconnect this call.'), scope); return;
      }
      this.recoveries = (this.recoveries || 0) + 1;
      this.recovery = setTimeout(async () => {
        if (!this.current(scope)) return;
        this.recovery = null;
        if (!this.peer || this.peer.connectionState === 'connected') return;
        try {
          if (this.caller) await this.offer(true, scope);
          else await this.command('signal', { signalKind: 'restart', epoch: this.call.epoch }, scope);
          if (this.current(scope)) this.armConnectionDeadline(scope);
        } catch (error) { await this.fail(error, scope); }
      }, 2000);
    }
    share() {
      if (this.retired) return Promise.reject(new Error('This account session has ended.'));
      if (this.sharing) return this.sharing;
      if (this.stoppingShare) return this.stoppingShare;
      if (!this.peer || !navigator.mediaDevices.getDisplayMedia) return Promise.reject(new Error('Screen sharing is not supported by this browser.'));
      const scope = this.scope(), peer = this.peer;
      // This call stays synchronous with the user's click; do not move display
      // capture behind negotiation, a timer, or another awaited operation.
      const acquisition = this.display ? null : navigator.mediaDevices.getDisplayMedia({ video: true, audio: false });
      let task;
      task = (async () => {
        let display, transferred = false;
        try {
          if (!acquisition) { await this.stopSharing(scope); return; }
          display = await acquisition;
          if (!this.current(scope)) return;
          const track = display.getVideoTracks()[0];
          const sender = peer.getSenders().find(item => item.track?.kind === 'video') || peer.getTransceivers().find(item => item.receiver.track.kind === 'video')?.sender;
          if (!sender || !track) throw new Error('Video sharing could not start.');
          this.display = display; transferred = true;
          track.contentHint = 'detail';
          await sender.replaceTrack(track);
          if (!this.current(scope)) return;
          await this.applyMediaPolicy(scope);
          if (!this.current(scope)) return;
          track.onended = () => this.stopSharing(scope).catch(error => this.fail(error, scope));
          if (track.readyState === 'ended') { await this.stopSharing(scope); return; }
          await this.command('signal', { signalKind: 'media-state', signalData: JSON.stringify({ screenSharing: true }), epoch: this.call.epoch }, scope);
        } catch (error) {
          if (this.current(scope)) {
            await this.stopSharing(scope).catch(() => {});
            throw error;
          }
        } finally {
          if (display && !transferred) stop(display);
          if (this.sharing === task) this.sharing = null;
        }
      })();
      this.sharing = task;
      return task;
    }
    async sendMediaState(scope, request = false) {
      if (!this.current(scope)) return;
      await this.command('signal', { signalKind: 'media-state',
        signalData: JSON.stringify({ screenSharing: Boolean(this.display), ...(request ? { request: true } : {}) }),
        epoch: this.call.epoch }, scope);
    }
    stopSharing(scope = this.scope()) {
      if (!this.current(scope)) return Promise.resolve();
      if (this.stoppingShare) return this.stoppingShare;
      const peer = this.peer, display = this.display; this.display = null;
      stop(display);
      let task;
      task = (async () => {
        try {
          if (!peer || !this.call) return;
          const sender = peer.getSenders().find(item => item.track?.kind === 'video');
          if (sender) await sender.replaceTrack(this.stream?.getVideoTracks()[0] || null);
          if (!this.current(scope)) return;
          await this.applyMediaPolicy(scope);
          if (!this.current(scope)) return;
          await this.sendMediaState(scope);
        } finally { if (this.stoppingShare === task) this.stoppingShare = null; }
      })();
      this.stoppingShare = task;
      return task;
    }
    mute() { for (const track of this.stream?.getAudioTracks() || []) track.enabled = !track.enabled; }
    camera() { for (const track of this.stream?.getVideoTracks() || []) track.enabled = !track.enabled; }
    armLease(scope = this.scope()) {
      clearTimeout(this.leaseTimer);
      const remaining = Date.parse(this.call.expiresUtc) - Date.now();
      this.leaseTimer = setTimeout(() => this.fail(new Error('The call expired.'), scope), Number.isFinite(remaining) ? Math.max(0, Math.min(remaining, 2147483647)) : 0);
    }
    async end() {
      if (!this.call) return;
      const call = this.call, scope = this.scope();
      const action = this.caller ? (['preparing', 'ringing'].includes(call.status) ? 'cancel' : 'end') : (call.status === 'ringing' ? 'decline' : 'end');
      this.close();
      await this.command(action, { callId: call.id, conversationId: call.conversationId }, scope);
    }
    async fail(error, expected = this.scope()) {
      const scope = typeof expected === 'string' ? { id: expected, generation: this.generation } : expected;
      if (!this.current(scope)) return;
      const message = error?.message || 'The call could not continue.';
      const closedGeneration = this.generation + 1;
      try { await this.end(); } catch (_) { /* server lease bounds cleanup */ }
      if (this.generation === closedGeneration && !this.call) this.failure(message);
    }
    retire() {
      this.retired = true;
      this.close();
    }
    close() {
      if (this.call) { this.finished.add(this.call.id); if (this.finished.size > 256) this.finished.delete(this.finished.values().next().value); }
      ++this.generation;
      for (const timer of [this.leaseTimer, this.connectTimer, this.recovery, this.heartbeat, this.qualityTimer]) clearTimeout(timer);
      this.peer?.close(); this.peer = null;
      stop(this.stream); stop(this.display); stop(this.remoteStream);
      this.stream = this.display = this.remoteStream = this.call = this.policy = null;
      this.remoteScreenSharing = false;
      this.localReady = this.offered = this.negotiating = this.flushing = false;
      this.receipt = this.remoteEpoch = this.localEpoch = this.remoteDescriptionIdentity = this.recovery = this.peerSetup = this.accepting = this.sharing = this.stoppingShare = this.connectedReceipt = null;
      this.recoveries = 0; this.quality = 2; this.healthySamples = 0; this.availableBandwidth = null; this.policyApply = null;
      this.localCandidates = []; this.remoteCandidates = []; this.localIceUfrags = null;
      this.media(null, null); this.present(null, false);
    }
  }
  globalThis.LegendBrowserCalling = LegendBrowserCalling;
})();
