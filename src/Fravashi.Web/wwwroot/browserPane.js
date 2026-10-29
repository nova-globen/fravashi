// Backlog 159: the session browser pane maps a click on the page's picture back onto the page's own viewport, which
// needs the size the picture is drawn at. Nothing else lives here.
window.fravashiBrowserPane = {
  size: (element) => {
    if (!element) { return [0, 0]; }
    const rect = element.getBoundingClientRect();
    return [rect.width, rect.height];
  },
};
