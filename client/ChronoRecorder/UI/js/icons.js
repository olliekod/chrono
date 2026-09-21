// Icons: simple 24px outlines drawn for Chrono, inlined so there is nothing to load.
(function (root) {
  const Chrono = root.Chrono || (root.Chrono = {});

  const NS = 'http://www.w3.org/2000/svg';

  // Each icon is a list of [tag, attributes]. Stroke icons unless marked fill.
  const ICONS = {
    library: [['rect', { x: 3.5, y: 3.5, width: 7, height: 7, rx: 1.5 }], ['rect', { x: 13.5, y: 3.5, width: 7, height: 7, rx: 1.5 }],
              ['rect', { x: 3.5, y: 13.5, width: 7, height: 7, rx: 1.5 }], ['rect', { x: 13.5, y: 13.5, width: 7, height: 7, rx: 1.5 }]],
    record: [['circle', { cx: 12, cy: 12, r: 8.5 }], ['circle', { cx: 12, cy: 12, r: 3.5, fill: 'currentColor' }]],
    sliders: [['path', { d: 'M4 6h9M17 6h3M4 12h3M11 12h9M4 18h11M19 18h1' }], ['circle', { cx: 15, cy: 6, r: 2 }],
              ['circle', { cx: 9, cy: 12, r: 2 }], ['circle', { cx: 17, cy: 18, r: 2 }]],
    link: [['path', { d: 'M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1' }], ['path', { d: 'M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1' }]],
    upload: [['path', { d: 'M7 18a4 4 0 0 1-.6-7.95A5.5 5.5 0 0 1 17 8.6 4.7 4.7 0 0 1 17.5 18' }], ['path', { d: 'M12 20v-8M9 14.5l3-3 3 3' }]],
    cloud: [['path', { d: 'M7 18a4 4 0 0 1-.6-7.95A5.5 5.5 0 0 1 17 8.6 4.7 4.7 0 0 1 17.5 18z' }]],
    check: [['path', { d: 'M5 12.5l4.5 4.5L19 7.5' }]],
    trash: [['path', { d: 'M4.5 7h15M9 7V4.5h6V7M6.5 7l1 12.5h9l1-12.5M10 11v5M14 11v5' }]],
    folder: [['path', { d: 'M3 7.5A2 2 0 0 1 5 5.5h4l2 2h8a2 2 0 0 1 2 2V17a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z' }]],
    play: [['path', { d: 'M8 5.5v13l11-6.5z', fill: 'currentColor' }]],
    pause: [['path', { d: 'M7.5 5h3.5v14H7.5zM13 5h3.5v14H13z', fill: 'currentColor' }]],
    scissors: [['circle', { cx: 6, cy: 6.5, r: 2.8 }], ['circle', { cx: 6, cy: 17.5, r: 2.8 }], ['path', { d: 'M8.4 8l11 8.5M8.4 16l11-8.5' }]],
    x: [['path', { d: 'M6 6l12 12M18 6L6 18' }]],
    expand: [['path', { d: 'M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5' }]],
    search: [['circle', { cx: 11, cy: 11, r: 6 }], ['path', { d: 'M15.5 15.5l5 5' }]],
    volume: [['path', { d: 'M4 10v4h4l5 4V6L8 10z', fill: 'currentColor' }], ['path', { d: 'M16.5 9a4.5 4.5 0 0 1 0 6' }]],
    mute: [['path', { d: 'M4 10v4h4l5 4V6L8 10z', fill: 'currentColor' }], ['path', { d: 'M16 9.5l5 5M21 9.5l-5 5' }]],
    copy: [['rect', { x: 8.5, y: 8.5, width: 11.5, height: 11.5, rx: 2 }], ['path', { d: 'M15.5 8.5V6.5a2 2 0 0 0-2-2h-7a2 2 0 0 0-2 2v7a2 2 0 0 0 2 2h2' }]],
    film: [['rect', { x: 3, y: 4.5, width: 18, height: 15, rx: 2 }], ['path', { d: 'M8 4.5v15M16 4.5v15M3 9.5h5M3 14.5h5M16 9.5h5M16 14.5h5' }]],
    keyboard: [['rect', { x: 2.5, y: 6, width: 19, height: 12, rx: 2 }], ['path', { d: 'M6.5 10h.01M10 10h.01M14 10h.01M17.5 10h.01M7.5 14h9' }]],
    monitor: [['rect', { x: 3, y: 4, width: 18, height: 12, rx: 2 }], ['path', { d: 'M8.5 20h7M12 16v4' }]],
    mic: [['rect', { x: 9, y: 3, width: 6, height: 11, rx: 3 }], ['path', { d: 'M5.5 11a6.5 6.5 0 0 0 13 0M12 17.5V21' }]],
    clock: [['circle', { cx: 12, cy: 12, r: 8.5 }], ['path', { d: 'M12 7.5V12l3 2' }]],
    gamepad: [['path', { d: 'M7 8h10a4 4 0 0 1 4 4.2l-.5 3.3a2.4 2.4 0 0 1-4 1.4L14.6 15h-5.2l-1.9 1.9a2.4 2.4 0 0 1-4-1.4L3 12.200A4 4 0 0 1 7 8z' }],
              ['path', { d: 'M8 10.5v3M6.5 12h3' }]],
    film2: [['rect', { x: 3, y: 5, width: 18, height: 14, rx: 2 }], ['path', { d: 'M10 9.5v5l4.5-2.500z', fill: 'currentColor' }]],
    alert: [['path', { d: 'M12 4l9 15.500H3z' }], ['path', { d: 'M12 10v4M12 17h.01' }]],
    pulse: [['path', { d: 'M3 12h4l2.5-6 4 12 2.500-6H21' }]],
  };

  /** An <svg class="icon"> for the named icon; 1.25em square by default (set the size with CSS). */
  function icon(name) {
    const svg = root.document.createElementNS(NS, 'svg');
    svg.setAttribute('class', 'icon');
    svg.setAttribute('viewBox', '0 0 24 24');
    svg.setAttribute('fill', 'none');
    svg.setAttribute('stroke', 'currentColor');
    svg.setAttribute('stroke-width', '2');
    svg.setAttribute('stroke-linecap', 'round');
    svg.setAttribute('stroke-linejoin', 'round');
    svg.setAttribute('aria-hidden', 'true');
    for (const [tag, attrs] of ICONS[name] || []) {
      const part = root.document.createElementNS(NS, tag);
      for (const [key, value] of Object.entries(attrs)) part.setAttribute(key, value);
      svg.append(part);
    }
    return svg;
  }

  Chrono.icon = icon;
})(window);
