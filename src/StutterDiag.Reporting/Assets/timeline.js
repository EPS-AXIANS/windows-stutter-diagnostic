/* StutterDiag zoomable timeline — dependency-free canvas.
   Reads window.SD_TIMELINE (see HtmlReportGenerator). Mouse-wheel zooms about the
   cursor, drag pans, the buttons jump between stutters. Purely a visualization of
   temporal proximity; it asserts nothing about cause. */
(function () {
  "use strict";

  var DATA = window.SD_TIMELINE;
  var canvas = document.getElementById("sd-timeline");
  if (!DATA || !canvas || !DATA.sessions || DATA.sessions.length === 0) {
    if (canvas) { canvas.style.display = "none"; }
    return;
  }

  var freq = DATA.freq > 0 ? DATA.freq : 10000000;
  var readout = document.getElementById("sd-timeline-readout");
  var picker = document.getElementById("sd-timeline-session");
  var btnPrev = document.getElementById("sd-timeline-prev");
  var btnNext = document.getElementById("sd-timeline-next");
  var btnReset = document.getElementById("sd-timeline-reset");

  var LANES = [
    { kind: "Stutter", label: "Stutter", color: "#c0392b" },
    { kind: "UserMark", label: "User mark", color: "#8e44ad" },
    { kind: "Whea", label: "WHEA", color: "#c0392b" },
    { kind: "KernelPower", label: "Kernel-Power", color: "#2f6feb" },
    { kind: "DpcSpike", label: "DPC spike", color: "#d98e04" },
    { kind: "DiskLatencySpike", label: "Disk latency", color: "#d98e04" },
    { kind: "Event", label: "Event", color: "#6b7480" },
    { kind: "Normal", label: "Reference", color: "#9aa4b1" }
  ];
  var laneIndex = {};
  LANES.forEach(function (l, i) { laneIndex[l.kind] = i; });

  var session = DATA.sessions[0];
  var view = { from: 0, to: 1 };
  var dragging = false;
  var lastX = 0;

  function qpcToMs(q) { return (q / freq) * 1000.0; }

  function resetView() {
    var lo = session.t0, hi = session.t1;
    if (!(hi > lo)) { hi = lo + freq; }
    var pad = (hi - lo) * 0.02;
    view.from = lo - pad;
    view.to = hi + pad;
    draw();
  }

  function setSession(idx) {
    session = DATA.sessions[idx] || DATA.sessions[0];
    resetView();
  }

  var dpr = Math.max(1, window.devicePixelRatio || 1);
  function sizeCanvas() {
    var rect = canvas.getBoundingClientRect();
    canvas.width = Math.max(1, Math.round(rect.width * dpr));
    canvas.height = Math.max(1, Math.round(rect.height * dpr));
  }

  function xOf(qpc, w) {
    return ((qpc - view.from) / (view.to - view.from)) * w;
  }
  function qpcOf(x, w) {
    return view.from + (x / w) * (view.to - view.from);
  }

  function draw() {
    sizeCanvas();
    var ctx = canvas.getContext("2d");
    var w = canvas.width, h = canvas.height;
    ctx.clearRect(0, 0, w, h);
    ctx.save();
    ctx.scale(1, 1);

    var styles = getComputedStyle(document.body);
    var ink = styles.color || "#1c1f23";
    var lineColor = "rgba(128,128,128,0.35)";

    var padL = 108 * dpr, padR = 12 * dpr, padT = 10 * dpr, padB = 26 * dpr;
    var plotW = w - padL - padR;
    var plotH = h - padT - padB;
    var laneH = plotH / LANES.length;

    ctx.font = (11 * dpr) + "px system-ui, sans-serif";
    ctx.textBaseline = "middle";

    // lane rows + labels
    for (var i = 0; i < LANES.length; i++) {
      var y = padT + i * laneH;
      ctx.strokeStyle = lineColor;
      ctx.beginPath();
      ctx.moveTo(padL, y + laneH);
      ctx.lineTo(w - padR, y + laneH);
      ctx.stroke();
      ctx.fillStyle = ink;
      ctx.fillText(LANES[i].label, 6 * dpr, y + laneH / 2);
    }

    // time gridlines (approx 6)
    var span = view.to - view.from;
    var ticks = 6;
    ctx.fillStyle = ink;
    ctx.textAlign = "center";
    for (var t = 0; t <= ticks; t++) {
      var q = view.from + (span * t) / ticks;
      var gx = padL + xOf(q, plotW);
      ctx.strokeStyle = lineColor;
      ctx.beginPath();
      ctx.moveTo(gx, padT);
      ctx.lineTo(gx, padT + plotH);
      ctx.stroke();
      var relMs = qpcToMs(q - session.t0);
      ctx.fillText((relMs / 1000).toFixed(2) + "s", gx, h - padB / 2);
    }
    ctx.textAlign = "left";

    // entries
    var entries = session.entries;
    for (var e = 0; e < entries.length; e++) {
      var ent = entries[e];
      if (ent.qpc < view.from || ent.qpc > view.to) { continue; }
      var li = laneIndex[ent.kind];
      if (li === undefined) { li = laneIndex["Event"]; }
      var lane = LANES[li];
      var ex = padL + xOf(ent.qpc, plotW);
      var ey = padT + li * laneH + laneH / 2;
      ctx.fillStyle = lane.color;
      if ((ent.kind === "Stutter" || ent.kind === "UserMark") && ent.durationMs) {
        var wpx = Math.max(2 * dpr, (ent.durationMs / 1000 / (span / freq)) * plotW);
        ctx.globalAlpha = 0.85;
        ctx.fillRect(ex, padT + li * laneH + 3 * dpr, wpx, laneH - 6 * dpr);
        ctx.globalAlpha = 1;
      } else {
        ctx.beginPath();
        ctx.arc(ex, ey, 3 * dpr, 0, Math.PI * 2);
        ctx.fill();
      }
    }

    ctx.restore();
  }

  function nearestEntry(px, py) {
    var rect = canvas.getBoundingClientRect();
    var w = canvas.width;
    var padL = 108 * dpr, padR = 12 * dpr;
    var plotW = w - padL - padR;
    var cx = (px - rect.left) * dpr;
    var q = qpcOf(cx - padL, plotW);
    var best = null, bestD = Infinity;
    var entries = session.entries;
    for (var e = 0; e < entries.length; e++) {
      var d = Math.abs(entries[e].qpc - q);
      if (d < bestD) { bestD = d; best = entries[e]; }
    }
    return best;
  }

  canvas.addEventListener("wheel", function (ev) {
    ev.preventDefault();
    var rect = canvas.getBoundingClientRect();
    var w = canvas.width;
    var padL = 108 * dpr, padR = 12 * dpr;
    var plotW = w - padL - padR;
    var cx = (ev.clientX - rect.left) * dpr - padL;
    var pivot = qpcOf(cx, plotW);
    var factor = ev.deltaY < 0 ? 0.82 : 1.22;
    var newSpan = (view.to - view.from) * factor;
    var minSpan = freq / 1000; // 1 ms
    var maxSpan = (session.t1 - session.t0 + 1) * 40 + freq;
    if (newSpan < minSpan) { newSpan = minSpan; }
    if (newSpan > maxSpan) { newSpan = maxSpan; }
    var leftFrac = (pivot - view.from) / (view.to - view.from);
    view.from = pivot - leftFrac * newSpan;
    view.to = view.from + newSpan;
    draw();
  }, { passive: false });

  canvas.addEventListener("pointerdown", function (ev) {
    dragging = true;
    lastX = ev.clientX;
    canvas.setPointerCapture(ev.pointerId);
  });
  canvas.addEventListener("pointerup", function (ev) {
    dragging = false;
    try { canvas.releasePointerCapture(ev.pointerId); } catch (e) {}
  });
  canvas.addEventListener("pointermove", function (ev) {
    if (dragging) {
      var w = canvas.width;
      var padL = 108 * dpr, padR = 12 * dpr;
      var plotW = w - padL - padR;
      var dxPx = (ev.clientX - lastX) * dpr;
      lastX = ev.clientX;
      var dq = (dxPx / plotW) * (view.to - view.from);
      view.from -= dq;
      view.to -= dq;
      draw();
      return;
    }
    if (readout) {
      var ent = nearestEntry(ev.clientX, ev.clientY);
      if (ent) {
        var rel = (qpcToMs(ent.qpc - session.t0) / 1000).toFixed(3);
        readout.textContent = "+" + rel + "s  [" + ent.kind + "]  " + ent.label;
      }
    }
  });

  var stutterCursor = -1;
  function jump(dir) {
    var s = session.stutters || [];
    if (s.length === 0) { return; }
    stutterCursor = (stutterCursor + dir + s.length) % s.length;
    if (stutterCursor < 0) { stutterCursor = 0; }
    var target = s[stutterCursor];
    var span = Math.max(freq / 20, (view.to - view.from) * 0.25); // >= 50 ms
    view.from = target.qpc - span / 2;
    view.to = target.qpc + span / 2;
    draw();
    if (readout) { readout.textContent = "Stutter #" + target.index + " (id " + target.id + ")"; }
    var card = document.getElementById("stutter-" + target.id);
    if (card && card.scrollIntoView) { card.scrollIntoView({ behavior: "smooth", block: "center" }); }
  }

  if (btnPrev) { btnPrev.addEventListener("click", function () { jump(-1); }); }
  if (btnNext) { btnNext.addEventListener("click", function () { jump(1); }); }
  if (btnReset) { btnReset.addEventListener("click", function () { stutterCursor = -1; resetView(); }); }
  if (picker) {
    picker.addEventListener("change", function () { setSession(picker.selectedIndex); });
  }

  window.addEventListener("resize", draw);
  setSession(0);
})();
