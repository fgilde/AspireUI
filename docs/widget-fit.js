window.fitGildeWidgets = function (p) {
  var css =
    ':host { font-family: inherit !important; }' +
    '.surface, .surface[data-theme] {' +
    ' --bg: ' + p.bg + ' !important; --raised: ' + p.raised + ' !important; --input: ' + p.input + ' !important;' +
    ' --line: ' + p.line + ' !important; --text: ' + p.text + ' !important; --muted: ' + p.muted + ' !important;' +
    (p.ink ? ' --accent-ink: ' + p.ink + ' !important;' : '') + ' }' +
    '.card { border-radius: ' + p.radius + '; box-shadow: none; }' +
    '.bar { display: none; }' +
    'input, textarea, select, .notice, .status, .error, .contact-again, .challenge-success, .support-link, .star-support { border-radius: ' + p.control + '; }' +
    'button.primary { border-radius: ' + p.control + '; min-height: 42px; padding: 0 18px; font-weight: 600; }' +
    'button.primary:hover { filter: brightness(.92); transform: none; }';
  var sheet = new CSSStyleSheet();
  sheet.replaceSync(css);
  function fit(el) {
    if (!el.shadowRoot || el.shadowRoot.adoptedStyleSheets.indexOf(sheet) >= 0) return;
    el.shadowRoot.adoptedStyleSheets = el.shadowRoot.adoptedStyleSheets.concat(sheet);
  }
  function scan() { document.querySelectorAll('gilde-contact, gilde-support').forEach(fit); }
  Promise.all([customElements.whenDefined('gilde-contact'), customElements.whenDefined('gilde-support')]).then(function () {
    scan();
    new MutationObserver(scan).observe(document.body, { childList: true, subtree: true });
  });
};
