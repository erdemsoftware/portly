// Portly website: scroll effects, copy button and the direct download link. No tracking, no third-party code.
(function () {
  "use strict";
  var doc = document.documentElement;
  doc.classList.add("js");
  var reduce = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  // 3D app window: --p goes from 0 (tilted, as the page opens) to 1 (flat) while it scrolls toward the middle of the screen
  var stage = document.querySelector(".stage");
  if (stage && !reduce) {
    var ticking = false;
    var update = function () {
      ticking = false;
      var r = stage.getBoundingClientRect();
      var vh = window.innerHeight || 800;
      // start when the top of the window is near the bottom of the screen, finish when it reaches a third of the screen
      var p = (vh * 0.95 - r.top) / (vh * 0.62);
      p = Math.max(0, Math.min(1, p));
      stage.style.setProperty("--p", p.toFixed(3));
    };
    var onScroll = function () { if (!ticking) { ticking = true; requestAnimationFrame(update); } };
    window.addEventListener("scroll", onScroll, { passive: true });
    window.addEventListener("resize", onScroll);
    update();
  }

  // cards and rows fade in once
  var items = document.querySelectorAll(".reveal");
  if ("IntersectionObserver" in window && !reduce) {
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) {
        if (e.isIntersecting) { e.target.classList.add("in"); io.unobserve(e.target); }
      });
    }, { rootMargin: "0px 0px -8% 0px", threshold: 0.12 });
    items.forEach(function (el, i) { el.style.transitionDelay = (i % 3) * 70 + "ms"; io.observe(el); });
    // safety net: whatever is on screen shortly after the page opens is shown even if the observer has not fired
    setTimeout(function () {
      var vh = window.innerHeight || 800;
      items.forEach(function (el) { if (el.getBoundingClientRect().top < vh) el.classList.add("in"); });
    }, 1200);
  } else {
    items.forEach(function (el) { el.classList.add("in"); });
  }

  // copy the install command
  document.querySelectorAll("[data-copy]").forEach(function (btn) {
    btn.addEventListener("click", function () {
      var text = document.querySelector(btn.getAttribute("data-copy")).textContent.trim();
      var done = function () {
        var label = btn.textContent;
        btn.textContent = btn.getAttribute("data-done") || "Copied";
        btn.classList.add("done");
        setTimeout(function () { btn.textContent = label; btn.classList.remove("done"); }, 1800);
      };
      if (navigator.clipboard) navigator.clipboard.writeText(text).then(done, function () {});
    });
  });

  // the language switch remembers the choice, so the root page does not send the visitor back (see lang.js)
  document.querySelectorAll("[data-setlang]").forEach(function (a) {
    a.addEventListener("click", function () {
      try { localStorage.setItem("portly-lang", a.getAttribute("data-setlang")); } catch (e) { /* ignore */ }
    });
  });

  // Downloads. Every link already works without this: Windows uses .../releases/latest/download/<file> (always the newest
  // stable file), macOS / Linux point to the current beta. When GitHub answers, the version numbers are filled in and
  // macOS / Linux move to the newest release that has their files; once that is a stable release, the "beta" tags go.
  var REPO = "https://github.com/erdemsoftware/portly/releases/download/";
  var api = "https://api.github.com/repos/erdemsoftware/portly/releases";
  var get = function (url) {
    return fetch(url, { headers: { Accept: "application/vnd.github+json" } }).then(function (r) { return r.ok ? r.json() : null; });
  };
  var safeTag = function (t) { return /^v\d+\.\d+\.\d+(-[a-z]+\.\d+)?$/.test(t || ""); };
  if (!window.fetch) return;

  get(api + "/latest").then(function (rel) {
    if (!rel || !safeTag(rel.tag_name)) return;
    var v = rel.tag_name.slice(1);
    document.querySelectorAll("[data-version]").forEach(function (el) { el.textContent = v; });
  }).catch(function () {});

  get(api + "?per_page=15").then(function (list) {
    if (!list || !list.length) return;
    var rel = list.filter(function (r) {
      return !r.draft && safeTag(r.tag_name) && (r.assets || []).some(function (a) { return a.name === "Portly-macos-arm64.dmg"; });
    })[0];
    if (!rel) return;
    var names = {};
    rel.assets.forEach(function (a) { names[a.name] = true; });
    document.querySelectorAll("[data-file]").forEach(function (a) {
      var f = a.getAttribute("data-file");
      if (names[f]) a.href = REPO + rel.tag_name + "/" + f;
    });
    var v = rel.tag_name.slice(1);
    document.querySelectorAll("[data-beta-version]").forEach(function (el) { el.textContent = v; });
    if (!rel.prerelease) document.querySelectorAll(".tag.beta").forEach(function (el) { el.hidden = true; });
  }).catch(function () {});
})();
