using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace CodeAstrogator.Core
{
    /// <summary>Input for one catalog refresh (everything the orchestrator may not read itself,
    /// because it runs off the UI thread).</summary>
    public sealed class ModelCatalogContext
    {
        /// <summary>Resolved <c>claude</c> executable; null ⇒ nothing can be probed.</summary>
        public string? ExePath { get; set; }

        /// <summary>Working directory for the probe processes (solution dir).</summary>
        public string? WorkingDirectory { get; set; }

        /// <summary>Directory the extension was deployed to — holds the bundled <c>models.json</c>.</summary>
        public string? ExtensionDirectory { get; set; }

        /// <summary>User opted into repo fetches (shared with the announcement banner). False ⇒
        /// bundled defaults only, no network call.</summary>
        public bool AllowRemoteDefaults { get; set; }

        /// <summary>GitHub <c>owner/repo</c> and branch the default catalog is fetched from
        /// (from <c>WebUI/config.js</c>, same source as notice.json).</summary>
        public string GitHubRepo { get; set; } = "";
        public string Branch { get; set; } = "";
    }

    /// <summary>
    /// Builds the model list for the Model · Mode picker out of three layers:
    /// <list type="number">
    /// <item><b>Default catalog</b> — <c>models.json</c>, fetched from the repo (GitHub raw, like
    /// notice.json, opt-in + throttled) and otherwise read from the copy bundled in the VSIX. It
    /// defines which models exist, their labels, their family and the order.</item>
    /// <item><b>Local cache</b> — <c>%LocalAppData%\CodeAstrogator\models.json</c>: the merged
    /// catalog plus, per model, whether the installed CLI accepts it. Users may add their own
    /// entries here; they are never overwritten by the defaults.</item>
    /// <item><b>CLI probe</b> — Anthropic ships a model before the CLI accepts it (Claude Opus 5.5,
    /// released 2026-09-22, is rejected by CLI 2.1.263 with <c>[claude-code:unrecognized_model]</c>),
    /// so every ID is verified against the installed binary before it is offered.</item>
    /// </list>
    /// <para>
    /// <b>The probe costs nothing and talks to nobody.</b> There is no <c>claude models</c> command
    /// and no local model catalog file, but <c>claude -p /model --model &lt;id&gt;</c> renders the
    /// model name the CLI resolved — a known ID becomes its display label (<c>Opus 5.5</c>), an
    /// unknown ID is echoed verbatim (<c>claude-opus-5-5</c>). Verified against CLI 2.1.263
    /// (2026-09-23): <c>num_turns: 0</c>, <c>total_cost_usd: 0</c>, <c>duration_api_ms: 0</c>, and
    /// the call still answers with <c>ANTHROPIC_BASE_URL</c> pointed at a dead port — so no API
    /// request and no OAuth token are involved (same rule as <see cref="ClaudeUsageClient"/>:
    /// everything goes through the official binary). Results are cached per CLI version, so a full
    /// sweep runs once per CLI build.
    /// </para>
    /// <para>Re-verify the label/echo distinction when the CLI updates.</para>
    /// </summary>
    public static class ClaudeModelCatalog
    {
        /// <summary>File name of both the default catalog and the local cache.</summary>
        public const string CatalogFileName = "models.json";

        /// <summary>Don't re-fetch the defaults more often than this (a release-cadence file).</summary>
        public static readonly TimeSpan RemoteRefreshInterval = TimeSpan.FromHours(12);

        private static readonly SemaphoreSlim RefreshGate = new SemaphoreSlim(1, 1);
        private static readonly object CacheLock = new object();
        private static IReadOnlyList<PickerModel> _current = Array.Empty<PickerModel>();
        private static string? _probedForExe;

        /// <summary>Last resolved picker list (empty until the first refresh finished).</summary>
        public static IReadOnlyList<PickerModel> Current
        {
            get { lock (CacheLock) return _current; }
        }

        /// <summary>Forces the next <see cref="RefreshAsync"/> to re-read and re-probe everything
        /// (used when the configured executable path changes).</summary>
        public static void Invalidate()
        {
            lock (CacheLock)
            {
                _probedForExe = null;
                _current = Array.Empty<PickerModel>();
            }
        }

        /// <summary>Local cache path (<c>%LocalAppData%\CodeAstrogator\models.json</c>).</summary>
        public static string CachePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodeAstrogator",
            CatalogFileName);

        /// <summary>
        /// Refreshes the catalog: merge defaults into the cache, probe what the installed CLI has not
        /// been asked about yet, persist, and return the picker rows. <paramref name="onProgress"/> is
        /// invoked after every probe so the UI can fill in while a first-run sweep is still running.
        /// Never throws; on any failure it returns whatever could be resolved (possibly empty, which
        /// leaves the WebUI on its built-in list).
        /// </summary>
        public static async Task<IReadOnlyList<PickerModel>> RefreshAsync(
            ModelCatalogContext context,
            Action<IReadOnlyList<PickerModel>>? onProgress = null,
            CancellationToken ct = default)
        {
            await RefreshGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                lock (CacheLock)
                {
                    // Already swept for this executable in this VS session → serve the cached result.
                    if (_probedForExe != null &&
                        string.Equals(_probedForExe, context.ExePath ?? "", StringComparison.OrdinalIgnoreCase))
                        return _current;
                }

                var data = ModelCatalog.ParseCache(ReadFileOrNull(CachePath) ?? "");
                var defaults = await LoadDefaultsAsync(context, data, ct).ConfigureAwait(false);
                if (defaults.Entries.Count > 0)
                {
                    data = ModelCatalog.MergeDefaults(data, defaults.Entries);
                    data.DefaultsSource = defaults.Source;
                    if (defaults.FetchedAt != null)
                        data.DefaultsFetchedAt = defaults.FetchedAt;
                }
                if (data == null || data.Models.Count == 0)
                    return Publish(context.ExePath, Array.Empty<PickerModel>(), probed: false);

                if (string.IsNullOrEmpty(context.ExePath))
                {
                    // No CLI at all: nothing can be verified. Persist the merged catalog, report
                    // nothing, and let the UI keep its built-in fallback list.
                    WriteCache(data);
                    return Publish(context.ExePath, Array.Empty<PickerModel>(), probed: false);
                }

                var cliVersion = await ReadCliVersionAsync(context.ExePath!, ct).ConfigureAwait(false);
                var pending = data.Models.Where(m => ModelCatalog.NeedsProbe(m, cliVersion)).ToList();

                if (pending.Count > 0 && onProgress != null)
                    onProgress(ModelCatalog.BuildPicker(data.Models)); // whatever is already known

                foreach (var entry in pending)
                {
                    if (ct.IsCancellationRequested)
                        break;
                    var known = await ProbeModelAsync(context.ExePath!, entry.Id, context.WorkingDirectory, ct)
                        .ConfigureAwait(false);
                    if (known == null)
                        continue; // probe failed — leave the entry unknown and retry next time

                    entry.Available = known.Value;
                    entry.CheckedCliVersion = cliVersion;
                    entry.CheckedAt = DateTimeOffset.Now;
                    onProgress?.Invoke(ModelCatalog.BuildPicker(data.Models));
                }

                WriteCache(data);
                return Publish(
                    context.ExePath,
                    ModelCatalog.BuildPicker(data.Models),
                    probed: !ct.IsCancellationRequested && data.Models.All(m => !ModelCatalog.NeedsProbe(m, cliVersion)));
            }
            catch
            {
                return Current; // best-effort: the picker keeps whatever it had
            }
            finally
            {
                RefreshGate.Release();
            }
        }

        private static IReadOnlyList<PickerModel> Publish(string? exePath, IReadOnlyList<PickerModel> rows, bool probed)
        {
            lock (CacheLock)
            {
                _current = rows;
                _probedForExe = probed ? exePath ?? "" : null; // incomplete sweep → try again next time
                return _current;
            }
        }

        // ── defaults (repo / bundled) ─────────────────────────────────────────

        /// <summary>
        /// Default catalog: from the repo when the user allowed repo fetches and the throttle window
        /// has passed, otherwise from the copy bundled in the VSIX. A failed fetch silently falls back
        /// to the bundled file; an empty result leaves the cached catalog as-is.
        /// </summary>
        private static async Task<DefaultCatalog> LoadDefaultsAsync(
            ModelCatalogContext context, ModelCatalogData? cache, CancellationToken ct)
        {
            var due = cache?.DefaultsFetchedAt == null ||
                      DateTimeOffset.Now - cache.DefaultsFetchedAt.Value >= RemoteRefreshInterval;

            if (context.AllowRemoteDefaults && due && !string.IsNullOrEmpty(context.GitHubRepo))
            {
                var url = "https://raw.githubusercontent.com/" + context.GitHubRepo.Trim('/') + "/" +
                          (string.IsNullOrEmpty(context.Branch) ? "master" : context.Branch) + "/" + CatalogFileName;
                var remote = ModelCatalog.ParseDefaults(await FetchAsync(url, ct).ConfigureAwait(false) ?? "");
                if (remote.Count > 0)
                    return new DefaultCatalog(remote, "remote", DateTimeOffset.Now);
            }

            var bundled = string.IsNullOrEmpty(context.ExtensionDirectory)
                ? null
                : ReadFileOrNull(Path.Combine(context.ExtensionDirectory!, CatalogFileName));
            // The bundled file is not a fetch: leave the throttle stamp alone so the next start
            // (or the next options change) tries the repo again.
            return new DefaultCatalog(ModelCatalog.ParseDefaults(bundled ?? ""), "bundled", null);
        }

        private sealed class DefaultCatalog
        {
            public DefaultCatalog(IReadOnlyList<ModelEntry> entries, string source, DateTimeOffset? fetchedAt)
            {
                Entries = entries;
                Source = source;
                FetchedAt = fetchedAt;
            }

            public IReadOnlyList<ModelEntry> Entries { get; }
            public string Source { get; }
            public DateTimeOffset? FetchedAt { get; }
        }

        private static async Task<string?> FetchAsync(string url, CancellationToken ct)
        {
            try
            {
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                client.DefaultRequestHeaders.Add("User-Agent", "CodeAstrogator");
                using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return null;
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch
            {
                return null; // offline / blocked / 404 → bundled defaults
            }
        }

        /// <summary>Reads the GitHub repo + branch out of the shipped <c>WebUI/config.js</c> (the same
        /// build-time config the banners use), falling back to the upstream repo.</summary>
        public static (string Repo, string Branch) ReadRepoConfig(string? extensionDirectory)
        {
            var repo = "finex7070/CodeAstrogator";
            var branch = "master";
            try
            {
                if (string.IsNullOrEmpty(extensionDirectory))
                    return (repo, branch);
                var text = ReadFileOrNull(Path.Combine(extensionDirectory!, "WebUI", "config.js"));
                if (text == null)
                    return (repo, branch);
                var r = Regex.Match(text, @"githubRepo\s*:\s*""([^""]+)""");
                if (r.Success)
                    repo = r.Groups[1].Value;
                var b = Regex.Match(text, @"noticeBranch\s*:\s*""([^""]+)""");
                if (b.Success)
                    branch = b.Groups[1].Value;
            }
            catch
            {
                // keep the defaults
            }
            return (repo, branch);
        }

        // ── cache file ────────────────────────────────────────────────────────

        private static string? ReadFileOrNull(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch { return null; }
        }

        private static void WriteCache(ModelCatalogData data)
        {
            try
            {
                var path = CachePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, ModelCatalog.Serialize(data), new UTF8Encoding(false));
            }
            catch
            {
                // read-only profile / locked file — the probe just runs again next time
            }
        }

        // ── CLI probes ────────────────────────────────────────────────────────

        /// <summary>Version string of the installed CLI ("2.1.263"), or "" when it cannot be read.
        /// Availability is cached against it, so a CLI update re-probes every model.</summary>
        internal static async Task<string> ReadCliVersionAsync(string exePath, CancellationToken ct)
        {
            var output = await RunAsync(exePath, "--version", null, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(output))
                return "";
            var m = Regex.Match(output!, @"\d+(?:\.\d+)+");
            return m.Success ? m.Value : "";
        }

        /// <summary>Asks the CLI to resolve <paramref name="modelId"/>; true = the CLI knows it,
        /// false = it does not, null = the probe itself failed.</summary>
        private static async Task<bool?> ProbeModelAsync(
            string exePath, string modelId, string? workingDirectory, CancellationToken ct)
        {
            var noHooksSettings = ClaudeUsageClient.EnsureNoHooksSettingsFile();
            var arguments = "-p /model --model " + modelId + " --output-format json";
            if (!string.IsNullOrEmpty(noHooksSettings))
                arguments += " --settings \"" + noHooksSettings + "\"";

            var output = await RunAsync(exePath, arguments, workingDirectory, ct).ConfigureAwait(false);
            return output == null ? null : ParseModelKnown(output, modelId);
        }

        /// <summary>
        /// Reads the <c>/model</c> report out of the CLI's JSON envelope and decides whether the CLI
        /// knows <paramref name="modelId"/>. The report reads
        /// <c>Current model: `Opus 5.5` (effort: high)</c> for a known ID and
        /// <c>Current model: `claude-opus-5-5` (effort: high)</c> for an unknown one — i.e. the
        /// backticked name is the raw ID exactly when the CLI could not resolve it.
        /// Returns null when nothing could be parsed (the entry then stays unknown and is retried).
        /// </summary>
        internal static bool? ParseModelKnown(string output, string modelId)
        {
            var text = UnwrapResult(output);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var m = Regex.Match(text, @"Current model:\s*`([^`]+)`");
            if (!m.Success)
                return null;

            var rendered = m.Groups[1].Value.Trim();
            if (rendered.Length == 0)
                return null;

            return !string.Equals(rendered, modelId, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Unwraps <c>{"result": "…"}</c> (tolerating hook preamble lines); falls back to
        /// the raw output. Same shape as <see cref="ClaudeUsageClient.ParseUsageResult"/>.</summary>
        private static string UnwrapResult(string output)
        {
            string? text = null;
            using (var reader = new StringReader(output))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var trimmed = line.TrimStart();
                    if (trimmed.Length == 0 || trimmed[0] != '{')
                        continue;
                    try
                    {
                        var result = JObject.Parse(trimmed).Value<string>("result");
                        if (!string.IsNullOrEmpty(result))
                            text = result; // keep the last result line
                    }
                    catch
                    {
                        // not the JSON envelope — ignore this line
                    }
                }
            }
            return text ?? output;
        }

        /// <summary>
        /// Runs the CLI hidden and returns its stdout (null on failure/timeout). stdin is closed
        /// immediately (neither <c>--version</c> nor the slash command takes a prompt), the process
        /// window is suppressed, and the hook-disabling <c>--settings</c> file is added by the caller —
        /// so nothing here can flash a console window.
        /// </summary>
        private static async Task<string?> RunAsync(
            string exePath, string arguments, string? workingDirectory, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
                    psi.WorkingDirectory = workingDirectory;

                using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                var stdout = new StringBuilder();
                var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.Exited += (_, __) =>
                {
                    try { exited.TrySetResult(process.ExitCode); }
                    catch { exited.TrySetResult(-1); }
                };
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                        lock (stdout) stdout.AppendLine(e.Data);
                };
                process.ErrorDataReceived += (_, __) => { /* ignored — best-effort */ };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                try { process.StandardInput.Close(); } catch { }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
                using (timeoutCts.Token.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(); }
                    catch { /* already gone */ }
                }))
                {
                    await exited.Task.ConfigureAwait(false);
                    try { process.WaitForExit(); } catch { }
                    if (timeoutCts.IsCancellationRequested)
                        return null; // killed by timeout/caller — output is unreliable
                    lock (stdout) return stdout.ToString();
                }
            }
            catch
            {
                return null; // not launchable / no permission
            }
        }
    }
}
