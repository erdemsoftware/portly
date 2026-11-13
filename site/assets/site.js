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

  // "Download": the links point to the latest release page; when GitHub answers, they go straight to the setup file
  // and the version is shown. (GitHub's "latest" never lists a pre-release, so this is always the stable version.)
  var links = document.querySelectorAll("[data-download]");
  var label = document.querySelectorAll("[data-version]");
  if (links.length && window.fetch) {
    fetch("https://api.github.com/repos/erdemsoftware/portly/releases/latest", { headers: { Accept: "application/vnd.github+json" } })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (rel) {
        if (!rel || !rel.assets) return;
        var setup = rel.assets.filter(function (a) { return /-win10-11-setup\.exe$/.test(a.name); })[0];
        if (setup && /^https:\/\/github\.com\/erdemsoftware\/portly\/releases\/download\//.test(setup.browser_download_url))
          links.forEach(function (a) { a.href = setup.browser_download_url; });
        var v = String(rel.tag_name || "").replace(/^v/, "");
        if (/^\d+\.\d+\.\d+$/.test(v)) label.forEach(function (el) { el.textContent = v; });
      })
      .catch(function () {});
  }
})();
