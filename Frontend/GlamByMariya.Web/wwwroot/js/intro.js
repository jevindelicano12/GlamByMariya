// Homepage intro: soft circles bloom, the name is signed, the tagline appears, then it fades away.
(function () {
  'use strict';
  var root = document.documentElement;
  var host = document.getElementById('gbm-intro');
  if (!host) return;
  if (!root.classList.contains('gbm-intro-on')) { host.remove(); return; }
  try { sessionStorage.setItem('gbm-intro-seen', '1'); } catch { }

  var timers = [], finished = false;
  var at = function (ms, f) { timers.push(setTimeout(f, ms)); };

  function finish() {
    if (finished) return;
    finished = true;
    timers.forEach(clearTimeout);
    root.classList.remove('gbm-intro-on');
    host.remove();
    document.removeEventListener('keydown', onKey);
  }
  function fadeAway() {
    if (finished) return;
    timers.forEach(clearTimeout); timers = [];
    host.classList.add('is-fading');
    setTimeout(finish, 900);
  }
  function onKey(e) { if (e.key === 'Escape') fadeAway(); }
  document.getElementById('gbm-intro-skip').addEventListener('click', fadeAway);
  document.addEventListener('keydown', onKey);

  if (window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
    host.classList.add('is-calm');
    at(1300, fadeAway);
    return;
  }
  // Force a style pass so the circles start at scale(0) before they bloom.
  void host.offsetWidth;
  at(50, function () { host.classList.add('is-bloom'); });
  at(1000, function () { host.classList.add('is-writing'); });
  at(2700, function () { host.classList.add('is-tag'); });
  at(4400, fadeAway);
})();
