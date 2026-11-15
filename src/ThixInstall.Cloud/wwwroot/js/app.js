/* ThixInstall.Cloud: the few behaviours only this app has (the toolkit, matui.js, does the rest).
   - The search of the start page (data-tile-filter="#list"): hides the tiles whose text does not match, then the categories without a tile left.
     A hidden tile keeps its tick (a bundle may hold what the search hides) and "select all" of the toolkit leaves hidden tiles alone. */
(function () {
  'use strict';

  function normalise(text) {
    return (text || '').toLowerCase().normalize('NFD').replace(/[̀-ͯ]/g, '');
  }

  function filter(input) {
    var scope = document.querySelector(input.getAttribute('data-tile-filter'));
    if (!scope) { return; }
    var words = normalise(input.value).split(/\s+/).filter(Boolean);
    var shown = 0;
    Array.prototype.forEach.call(scope.querySelectorAll('[data-tile-group]'), function (group) {
      var inGroup = 0;
      Array.prototype.forEach.call(group.querySelectorAll('[data-tile-text]'), function (tile) {
        var text = normalise(tile.getAttribute('data-tile-text'));
        var match = words.every(function (w) { return text.indexOf(w) !== -1; });
        tile.hidden = !match;
        if (match) { inGroup++; }
      });
      group.hidden = inGroup === 0;
      shown += inGroup;
    });
    var empty = scope.querySelector('[data-tile-empty]');
    if (empty) { empty.hidden = shown !== 0; }
  }

  Array.prototype.forEach.call(document.querySelectorAll('[data-tile-filter]'), function (input) {
    input.addEventListener('input', function () { filter(input); });
    // Escape clears the search first
    input.addEventListener('keydown', function (e) {
      if (e.key === 'Escape' && input.value !== '') { e.preventDefault(); input.value = ''; filter(input); }
    });
    if (input.value) { filter(input); }
  });
})();
