// Runs before the page is drawn. Root page only: a visitor whose browser speaks Turkish goes to /tr/, unless they picked a
// language on this site before (the switch in the corner remembers it). The OS is noted for the download buttons.
(function () {
  "use strict";
  var doc = document.documentElement;
  var ua = navigator.userAgent || "";
  var platform = (navigator.userAgentData && navigator.userAgentData.platform) || navigator.platform || "";
  var os = /Android|iPhone|iPad|iPod/i.test(ua) ? "mobile"
    : /Mac/i.test(platform) || /Mac OS X/.test(ua) ? "mac"
    : /Linux|X11|CrOS/i.test(platform + " " + ua) ? "linux"
    : "windows";
  doc.setAttribute("data-os", os);

  if (doc.getAttribute("data-root") !== "1") return;
  try {
    var saved = localStorage.getItem("portly-lang");
    var langs = navigator.languages && navigator.languages.length ? navigator.languages : [navigator.language || ""];
    var wantsTr = saved ? saved === "tr" : String(langs[0] || "").toLowerCase().indexOf("tr") === 0;
    if (wantsTr) location.replace("tr/" + location.hash);
  } catch (e) { /* storage blocked: stay on the English page */ }
})();
