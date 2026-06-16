/* ===========================================================================
   Build-time configuration for Code Astrogator.
   Edit this file BEFORE building the VSIX, then rebuild + reinstall — the values
   are baked into the shipped WebUI (there is no runtime/settings override).

   Used by app.js to point the announcement + update banners at a GitHub repo:
     - announcement: https://raw.githubusercontent.com/<githubRepo>/<noticeBranch>/notice.json
     - update check: https://api.github.com/repos/<githubRepo>/releases/latest
   =========================================================================== */
window.CPFC_CONFIG = {
  // GitHub "owner/repo" the banners read from. Change this to point at your fork.
  githubRepo: "finex7070/CodeAstrogator",

  // Branch the raw announcement file (notice.json) is fetched from.
  noticeBranch: "master",

  // "Working" indicator one-liners — a random one shows next to the rocket while a turn runs,
  // and swaps as new content streams in. Add/edit freely (plain text).
  workingPhrases: [
    "Plotting a course through the codebase…",
    "Engaging the hyperdrive…",
    "Dodging asteroid-field bugs…",
    "Consulting the star charts…",
    "Refueling the thrusters…",
    "Boosting past the stack-trace nebula…",
    "Negotiating with the compiler aliens…",
    "Navigating the semicolon belt…",
    "Charging the photon linter…",
    "Triangulating the bug's coordinates…",
    "Aligning the warp coils…",
    "Decoding transmissions from main()…",
    "Scanning distant repositories…",
    "Houston, we're shipping a feature…",
    "Warp speed engaged, captain…",
    "Threading through the merge-conflict belt…",
    "Polishing the cosmic edge cases…",
    "Recalibrating the flux capacitor…",
    "Venting the technical debt out the airlock…",
    "Spinning up the quantum debugger…",
    "Calibrating the deflector array against null pointers…",
    "Plotting a hyperjump past the legacy code…",
    "Scanning for life in the comment sections…",
    "Rerouting power to the refactor drive…",
    "Outrunning the deadline meteor shower…",
    "Syncing with the mothership repo…",
    "Compiling stardust into shippable code…",
    "Tractor-beaming the missing dependency aboard…",
  ],
};
