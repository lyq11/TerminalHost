(() => {
  const term = new Terminal({
    cursorBlink: true,
    convertEol: false,
    scrollback: 10000,
    fontFamily: 'Cascadia Mono, Consolas, monospace',
    fontSize: 14,
    lineHeight: 1.08,
    allowProposedApi: false,
    theme: {
      background: '#0d0f13',
      foreground: '#d9dde6',
      cursor: '#d9dde6',
      selectionBackground: '#3b4252aa'
    }
  });
  const fitAddon = new FitAddon.FitAddon();
  term.loadAddon(fitAddon);
  term.open(document.getElementById('terminal'));

  const post = value => window.chrome.webview.postMessage(value);
  term.onData(data => post({ type: 'input', data }));

  let resizeTimer = 0;
  const resize = () => {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(() => {
      try {
        fitAddon.fit();
        post({ type: 'resize', cols: term.cols, rows: term.rows });
      } catch (_) {}
    }, 40);
  };
  new ResizeObserver(resize).observe(document.getElementById('terminal'));

  window.chrome.webview.addEventListener('message', event => {
    const message = event.data || {};
    switch (message.type) {
      case 'output': term.write(message.data || ''); break;
      case 'clear': term.clear(); break;
      case 'reset': term.reset(); break;
      case 'focus': term.focus(); break;
      case 'configure':
        term.options.fontFamily = message.fontFamily || 'Cascadia Mono, Consolas, monospace';
        term.options.fontSize = Number(message.fontSize) || 14;
        term.options.theme = message.theme === 'light'
          ? { background: '#f7f8fa', foreground: '#20242c', cursor: '#20242c', selectionBackground: '#9bbcf066' }
          : { background: '#0d0f13', foreground: '#d9dde6', cursor: '#d9dde6', selectionBackground: '#3b4252aa' };
        resize();
        break;
    }
  });

  window.addEventListener('keydown', event => {
    if (event.ctrlKey && event.shiftKey && event.code === 'KeyC' && term.hasSelection()) {
      navigator.clipboard.writeText(term.getSelection());
      event.preventDefault();
    }
  });

  requestAnimationFrame(() => {
    fitAddon.fit();
    post({ type: 'ready', cols: term.cols, rows: term.rows });
    resize();
    term.focus();
  });
})();
