"use strict";
// Bridge between xterm.js and Mira: output comes from TorLink's pseudo console, keys and the terminal size go back.
(() => {
  const host = window.chrome && window.chrome.webview;
  const element = document.getElementById("terminal");
  if (!host || !element || typeof Terminal !== "function" || typeof FitAddon !== "object") return;
  const build = Number(new URLSearchParams(location.search).get("build")) || 19041;
  const term = new Terminal({
    fontFamily: '"Cascadia Mono", "Cascadia Code", Consolas, "Segoe UI Symbol", monospace',
    fontSize: 14,
    lineHeight: 1.15,
    cursorBlink: false,
    cursorStyle: "block",
    cursorInactiveStyle: "none",
    scrollback: 0,
    customGlyphs: true,
    rescaleOverlappingGlyphs: true,
    drawBoldTextInBrightColors: false,
    windowsPty: { backend: "conpty", buildNumber: build },
    theme: {
      background: "#0e0e10", foreground: "#e8e8ec", cursor: "#f5f5f7", cursorAccent: "#0e0e10", selectionBackground: "rgba(255,255,255,0.24)",
      black: "#1c1c20", red: "#ee7d92", green: "#86d6a2", yellow: "#f0c560", blue: "#8fb3ff", magenta: "#c9a7f5", cyan: "#79d2d6", white: "#d6d6db",
      brightBlack: "#6b6b74", brightRed: "#f4a0b0", brightGreen: "#a8e6bd", brightYellow: "#f6d88a", brightBlue: "#b3cbff", brightMagenta: "#dcc4fa", brightCyan: "#a3e3e6", brightWhite: "#f5f5f7"
    }
  });
  const fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  term.open(element);
  const send = (message) => host.postMessage(message);
  term.onData((data) => send({ type: "input", data }));

  // TorLink turns wheel notches into arrow keys; sending the arrows directly works whatever the console does with mouse reports.
  let wheel = 0;
  term.attachCustomWheelEventHandler((event) => {
    wheel += event.deltaMode === 1 ? event.deltaY * 33 : event.deltaMode === 2 ? event.deltaY * 300 : event.deltaY;
    while (Math.abs(wheel) >= 100) {
      send({ type: "input", data: wheel < 0 ? "\u001b[A" : "\u001b[B" });
      wheel -= Math.sign(wheel) * 100;
    }
    event.preventDefault();
    return false;
  });
  // Alt+← and the keyboard's Back key return to Mira, as everywhere else in the app (when WebView2 leaves them to the page).
  // Ctrl+C copies a selection instead of quitting TorLink; Ctrl+V is left to the browser, which pastes the text.
  term.attachCustomKeyEventHandler((event) => {
    const back = event.key === "BrowserBack" || (event.key === "ArrowLeft" && event.altKey && !event.ctrlKey && !event.shiftKey && !event.metaKey);
    if (back) {
      if (event.type === "keydown" && !event.repeat) send({ type: "back" });
      return false;
    }
    if (event.type !== "keydown" || !event.ctrlKey || event.altKey) return true;
    const key = event.key.toLowerCase();
    if (key === "c" && (event.shiftKey || term.hasSelection())) {
      const text = term.getSelection();
      if (text && navigator.clipboard) navigator.clipboard.writeText(text).catch(() => {});
      term.clearSelection();
      return false;
    }
    return key !== "v";
  });

  let frame = 0, reported = "";
  const measure = () => {
    const size = fit.proposeDimensions();
    if (!size || !Number.isFinite(size.cols) || !Number.isFinite(size.rows)) return null;
    return { cols: Math.max(20, Math.min(500, size.cols)), rows: Math.max(5, Math.min(300, size.rows)) };
  };
  const report = () => {
    frame = 0;
    const size = measure();
    if (!size) return;
    if (size.cols !== term.cols || size.rows !== term.rows) term.resize(size.cols, size.rows);
    const key = size.cols + "x" + size.rows;
    if (key === reported) return;
    reported = key;
    send({ type: "resize", cols: size.cols, rows: size.rows });
  };
  new ResizeObserver(() => { if (!frame) frame = requestAnimationFrame(report); }).observe(element);

  host.addEventListener("message", (event) => {
    const message = event.data;
    if (!message || typeof message.type !== "string") return;
    if (message.type === "output" && typeof message.data === "string") term.write(message.data);
    else if (message.type === "reset") { term.reset(); term.options.disableStdin = false; element.classList.remove("closed"); reported = ""; report(); }
    else if (message.type === "exit") { term.options.disableStdin = true; element.classList.add("closed"); term.blur(); }
    else if (message.type === "focus") term.focus();
  });
  window.addEventListener("focus", () => term.focus());

  const size = measure() || { cols: 100, rows: 30 };
  term.resize(size.cols, size.rows);
  reported = size.cols + "x" + size.rows;
  send({ type: "ready", cols: size.cols, rows: size.rows });
  term.focus();
})();
