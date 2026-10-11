// Peer-to-peer transport for Netcode for GameObjects in WebGL builds.
// Uses PeerJS (WebRTC data channels); the public PeerJS broker is only used to exchange the
// connection offer, after which all game traffic flows directly between the two browsers.
//
// Event types returned by WebRtc_Poll: 1 connect, 2 data, 3 disconnect, 4 host ready, 5 fatal error.

var WebRtcTransportLib = {
  $WRTC: {
    peer: null,
    isHost: false,
    opened: false,
    conns: {},
    nextId: 1,
    queue: [],
    current: null,
    lastError: "",
    connectTimer: 0,

    push: function (type, id, data) {
      WRTC.queue.push({ type: type, id: id, data: data || null });
    },

    fail: function (message) {
      console.warn("[WebRtcTransport] " + message);
      WRTC.lastError = message;
      WRTC.push(5, 0, null);
    },

    reset: function () {
      for (var id in WRTC.conns) WRTC.closeEntry(Number(id), false);
      if (WRTC.peer) {
        try { WRTC.peer.destroy(); } catch (e) {}
      }
      clearTimeout(WRTC.connectTimer);
      WRTC.peer = null;
      WRTC.opened = false;
      WRTC.conns = {};
      WRTC.nextId = 1;
      WRTC.queue = [];
      WRTC.current = null;
      WRTC.lastError = "";
    },

    loadLibrary: function (url, onReady) {
      if (typeof Peer !== "undefined") { onReady(); return; }
      var script = document.createElement("script");
      script.src = url;
      script.async = true;
      script.onload = function () {
        if (typeof Peer === "undefined") WRTC.fail("library-load-failed");
        else onReady();
      };
      script.onerror = function () { WRTC.fail("library-load-failed"); };
      document.head.appendChild(script);
    },

    onData: function (id, data) {
      var bytes;
      if (data instanceof ArrayBuffer) bytes = new Uint8Array(data);
      else if (ArrayBuffer.isView(data)) bytes = new Uint8Array(data.buffer, data.byteOffset, data.byteLength);
      else return;
      WRTC.push(2, id, bytes);
    },

    setupConnection: function (conn, id) {
      var entry = { conn: conn, id: id, open: false, unreliable: null, rtt: 0, statsTimer: 0, iceTimer: 0 };
      WRTC.conns[id] = entry;

      conn.on("open", function () {
        if (!WRTC.conns[id]) return;
        entry.open = true;
        clearTimeout(WRTC.connectTimer);
        try { conn.dataChannel.binaryType = "arraybuffer"; } catch (e) {}
        // A second, negotiated channel without retransmits for NGO's unreliable traffic.
        try {
          var channel = conn.peerConnection.createDataChannel("unreliable", { negotiated: true, id: 77, ordered: false, maxRetransmits: 0 });
          channel.binaryType = "arraybuffer";
          channel.onmessage = function (e) { WRTC.onData(id, e.data); };
          entry.unreliable = channel;
        } catch (e) {
          console.warn("[WebRtcTransport] unreliable channel unavailable, using the reliable one", e);
        }
        entry.statsTimer = setInterval(function () { WRTC.updateRtt(entry); }, 1000);
        WRTC.updateRtt(entry);
        WRTC.push(1, id, null);
      });

      conn.on("data", function (data) { WRTC.onData(id, data); });
      conn.on("close", function () { WRTC.closeEntry(id, true); });
      conn.on("error", function (err) { console.warn("[WebRtcTransport] connection error", err); });
      conn.on("iceStateChanged", function (state) {
        clearTimeout(entry.iceTimer);
        if (state === "failed" || state === "closed") {
          WRTC.closeEntry(id, true);
        } else if (state === "disconnected") {
          // Give ICE a few seconds to recover before treating the peer as gone.
          entry.iceTimer = setTimeout(function () { WRTC.closeEntry(id, true); }, 6000);
        }
      });
    },

    closeEntry: function (id, notify) {
      var entry = WRTC.conns[id];
      if (!entry) return;
      delete WRTC.conns[id];
      clearInterval(entry.statsTimer);
      clearTimeout(entry.iceTimer);
      try { if (entry.unreliable) entry.unreliable.close(); } catch (e) {}
      try { entry.conn.close(); } catch (e) {}
      if (!notify) return;
      if (entry.open) WRTC.push(3, id, null);
      else if (!WRTC.isHost) WRTC.fail("connection-closed");
    },

    updateRtt: function (entry) {
      var pc = entry.conn.peerConnection;
      if (!pc || !pc.getStats) return;
      pc.getStats().then(function (stats) {
        stats.forEach(function (report) {
          if (report.type === "candidate-pair" && report.nominated && report.state === "succeeded" &&
              typeof report.currentRoundTripTime === "number") {
            entry.rtt = Math.round(report.currentRoundTripTime * 1000);
          }
        });
      }).catch(function () {});
    }
  },

  WebRtc_StartHost: function (peerIdPtr, libraryUrlPtr) {
    var peerId = UTF8ToString(peerIdPtr);
    var libraryUrl = UTF8ToString(libraryUrlPtr);
    WRTC.reset();
    WRTC.isHost = true;
    WRTC.loadLibrary(libraryUrl, function () {
      var peer = new Peer(peerId, { debug: 1 });
      WRTC.peer = peer;
      peer.on("open", function () {
        WRTC.opened = true;
        WRTC.push(4, 0, null);
      });
      peer.on("connection", function (conn) {
        WRTC.setupConnection(conn, WRTC.nextId++);
      });
      peer.on("disconnected", function () {
        // Lost the broker only; established peer connections keep working. Reconnect so new players can join.
        if (!peer.destroyed) {
          try { peer.reconnect(); } catch (e) {}
        }
      });
      peer.on("error", function (err) {
        var type = (err && err.type) ? err.type : String(err);
        if (!WRTC.opened || type === "unavailable-id") WRTC.fail(type);
        else console.warn("[WebRtcTransport] broker error", type);
      });
    });
  },

  WebRtc_StartClient: function (hostPeerIdPtr, libraryUrlPtr) {
    var hostPeerId = UTF8ToString(hostPeerIdPtr);
    var libraryUrl = UTF8ToString(libraryUrlPtr);
    WRTC.reset();
    WRTC.isHost = false;
    WRTC.loadLibrary(libraryUrl, function () {
      var peer = new Peer({ debug: 1 });
      WRTC.peer = peer;
      WRTC.connectTimer = setTimeout(function () {
        var entry = WRTC.conns[1];
        if (!entry || !entry.open) WRTC.fail("timeout");
      }, 20000);
      peer.on("open", function () {
        WRTC.opened = true;
        var conn = peer.connect(hostPeerId, { reliable: true, serialization: "raw" });
        WRTC.setupConnection(conn, 1);
      });
      peer.on("error", function (err) {
        var type = (err && err.type) ? err.type : String(err);
        var entry = WRTC.conns[1];
        if (!entry || !entry.open) WRTC.fail(type);
        else console.warn("[WebRtcTransport] broker error", type);
      });
    });
  },

  WebRtc_Send: function (connectionId, bufferPtr, offset, length, reliable) {
    var entry = WRTC.conns[connectionId];
    if (!entry || !entry.open) return;
    var data = HEAPU8.slice(bufferPtr + offset, bufferPtr + offset + length);
    if (!reliable && entry.unreliable && entry.unreliable.readyState === "open") {
      try { entry.unreliable.send(data); return; } catch (e) {}
    }
    entry.conn.send(data);
  },

  WebRtc_Poll: function (infoPtr) {
    if (WRTC.queue.length === 0) { WRTC.current = null; return 0; }
    var ev = WRTC.queue.shift();
    WRTC.current = ev;
    HEAP32[infoPtr >> 2] = ev.id;
    HEAP32[(infoPtr >> 2) + 1] = ev.data ? ev.data.length : 0;
    return ev.type;
  },

  WebRtc_ReadData: function (bufferPtr, maxLength) {
    var ev = WRTC.current;
    if (!ev || !ev.data) return 0;
    var n = Math.min(maxLength, ev.data.length);
    HEAPU8.set(ev.data.subarray(0, n), bufferPtr);
    return n;
  },

  WebRtc_Disconnect: function (connectionId) {
    WRTC.closeEntry(connectionId, false);
  },

  WebRtc_Shutdown: function () {
    WRTC.reset();
  },

  WebRtc_GetRtt: function (connectionId) {
    var entry = WRTC.conns[connectionId];
    return entry ? entry.rtt : 0;
  },

  WebRtc_GetLastError: function () {
    return stringToNewUTF8(WRTC.lastError || "");
  },

  WebRtc_CopyToClipboard: function (textPtr) {
    var text = UTF8ToString(textPtr);
    try {
      if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(text).catch(function (e) { console.warn("[WebRtcTransport] clipboard", e); });
      }
    } catch (e) {
      console.warn("[WebRtcTransport] clipboard", e);
    }
  }
};

autoAddDeps(WebRtcTransportLib, "$WRTC");
mergeInto(LibraryManager.library, WebRtcTransportLib);
