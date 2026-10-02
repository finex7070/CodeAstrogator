using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodeAstrogator.Core
{
    /// <summary>The newest published GitHub release (<c>/releases/latest</c>).</summary>
    public sealed class ReleaseInfo
    {
        /// <summary>Version without a leading "v" ("0.8.4").</summary>
        public string Version { get; set; } = "";
        /// <summary>Download URL of the attached <c>.vsix</c>; null when the release carries none.</summary>
        public string? VsixUrl { get; set; }
        public string? HtmlUrl { get; set; }
    }

    /// <summary>A downloaded, verified update waiting to be installed when Visual Studio closes.</summary>
    public sealed class PendingUpdate
    {
        public string Version { get; set; } = "";
        public string VsixPath { get; set; } = "";
        public string? HtmlUrl { get; set; }
        public DateTimeOffset StagedAt { get; set; }
        /// <summary>Start Visual Studio again after installing ("Restart now").</summary>
        public bool Relaunch { get; set; }
    }

    /// <summary>Outcome of <see cref="ExtensionUpdater.CheckAndStageAsync"/>.</summary>
    public sealed class StageResult
    {
        public bool Checked { get; set; }          // false = throttled, nothing fetched
        public ReleaseInfo? Latest { get; set; }
        public PendingUpdate? Staged { get; set; } // non-null = an update is ready to install
        public string? Error { get; set; }
    }

    /// <summary>
    /// Self-update for installs that do not want to wait for Visual Studio's own extension updater
    /// (which in practice only fetches updates once the Extensions dialog has been opened).
    /// <para>
    /// Flow: the newest GitHub release is checked (throttled); when it is newer than the installed
    /// version and carries a <c>.vsix</c>, that file is downloaded to
    /// <c>%LocalAppData%\CodeAstrogator\updates\</c> and <b>verified</b> — its manifest must carry the
    /// installed extension's identity id and exactly the release's version — and recorded as
    /// <c>pending.json</c>. A loaded extension cannot be replaced while Visual Studio runs, so the
    /// install itself happens when VS closes: <see cref="StartInstallHelper"/> launches a hidden
    /// PowerShell that waits for this <c>devenv.exe</c> to exit and then runs Visual Studio's own
    /// <c>VSIXInstaller.exe /quiet /instanceIds:&lt;this instance&gt;</c> (switch syntax verified against
    /// VSIXInstaller 18.10). Because the identity id is the same, this also updates a copy that was
    /// installed from the Marketplace.
    /// </para>
    /// <para>No Anthropic communication is involved — this only talks to the project's GitHub.</para>
    /// </summary>
    public static class ExtensionUpdater
    {
        /// <summary>Don't ask GitHub more often than this (the releases API allows 60 requests/h).</summary>
        public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(3);

        public const string PendingFileName = "pending.json";
        public const string StateFileName = "state.json";
        public const string LogFileName = "install.log";

        /// <summary><c>%LocalAppData%\CodeAstrogator\updates</c>.</summary>
        public static string DefaultUpdatesDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeAstrogator", "updates");

        // ── GitHub release ───────────────────────────────────────────────────

        /// <summary>Parses the <c>/releases/latest</c> payload; null when there is no usable tag.</summary>
        public static ReleaseInfo? ParseLatestRelease(string json)
        {
            try
            {
                var root = JObject.Parse(json);
                var tag = (root.Value<string>("tag_name") ?? "").Trim();
                if (tag.Length == 0)
                    return null;

                string? vsix = null;
                foreach (var asset in (root["assets"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    var name = asset.Value<string>("name") ?? "";
                    var url = asset.Value<string>("browser_download_url");
                    if (name.EndsWith(".vsix", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(url))
                    {
                        vsix = url;
                        break;
                    }
                }

                return new ReleaseInfo
                {
                    Version = NormalizeVersion(tag),
                    VsixUrl = vsix,
                    HtmlUrl = root.Value<string>("html_url"),
                };
            }
            catch
            {
                return null;
            }
        }

        public static string NormalizeVersion(string version) =>
            (version ?? "").Trim().TrimStart('v', 'V');

        /// <summary>True when <paramref name="remote"/> is strictly newer (numeric, dot-separated).</summary>
        public static bool IsNewer(string remote, string installed)
        {
            var a = Parts(remote);
            var b = Parts(installed);
            for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                var x = i < a.Length ? a[i] : 0;
                var y = i < b.Length ? b[i] : 0;
                if (x != y)
                    return x > y;
            }
            return false;
        }

        private static int[] Parts(string v) =>
            NormalizeVersion(v).Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();

        // ── VSIX verification ────────────────────────────────────────────────

        /// <summary>Reads <c>Identity/@Id</c> and <c>@Version</c> from a VSIX's <c>extension.vsixmanifest</c>.</summary>
        public static (string? Id, string? Version) ReadVsixIdentity(string vsixPath)
        {
            try
            {
                using var zip = ZipFile.OpenRead(vsixPath);
                var entry = zip.Entries.FirstOrDefault(e =>
                    string.Equals(e.FullName, "extension.vsixmanifest", StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                    return (null, null);
                using var stream = entry.Open();
                return ReadManifestIdentity(XDocument.Load(stream));
            }
            catch
            {
                return (null, null);
            }
        }

        /// <summary>Identity of an installed extension from its deployed <c>extension.vsixmanifest</c>.</summary>
        public static (string? Id, string? Version) ReadManifestIdentity(string manifestPath)
        {
            try
            {
                return File.Exists(manifestPath) ? ReadManifestIdentity(XDocument.Load(manifestPath)) : (null, null);
            }
            catch
            {
                return (null, null);
            }
        }

        private static (string? Id, string? Version) ReadManifestIdentity(XDocument doc)
        {
            var identity = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Identity");
            return (identity?.Attribute("Id")?.Value, identity?.Attribute("Version")?.Value);
        }

        // ── check + download ─────────────────────────────────────────────────

        /// <summary>
        /// Checks the newest release (at most every <see cref="CheckInterval"/> unless
        /// <paramref name="force"/>) and, when it is newer, downloads + verifies its VSIX and records it as
        /// pending. Never throws; problems end up in <see cref="StageResult.Error"/>.
        /// </summary>
        public static async Task<StageResult> CheckAndStageAsync(
            string gitHubRepo, string extensionId, string installedVersion,
            bool force = false, string? updatesDir = null, Func<string, Task<string?>>? fetch = null,
            Func<string, string, Task<bool>>? download = null, CancellationToken ct = default)
        {
            var dir = updatesDir ?? DefaultUpdatesDir;
            var result = new StageResult();
            try
            {
                Directory.CreateDirectory(dir);

                // Something already staged for a newer version than installed → keep it.
                var pending = ReadPending(dir);
                if (pending != null && !IsNewer(pending.Version, installedVersion))
                {
                    ClearPending(dir); // installed meanwhile (or outdated) — clean up
                    pending = null;
                }

                var statePath = Path.Combine(dir, StateFileName);
                if (!force && LastCheck(statePath) is DateTimeOffset last && DateTimeOffset.Now - last < CheckInterval)
                {
                    result.Staged = pending;
                    return result;
                }
                WriteLastCheck(statePath, DateTimeOffset.Now); // throttle failures too

                var url = "https://api.github.com/repos/" + gitHubRepo.Trim('/') + "/releases/latest";
                var json = await (fetch ?? FetchStringAsync)(url).ConfigureAwait(false);
                result.Checked = true;
                var latest = json == null ? null : ParseLatestRelease(json);
                result.Latest = latest;
                if (latest == null)
                {
                    result.Error = "Could not read the latest release from GitHub.";
                    result.Staged = pending;
                    return result;
                }

                if (!IsNewer(latest.Version, installedVersion))
                    return result; // up to date

                if (pending != null && pending.Version == latest.Version && File.Exists(pending.VsixPath))
                {
                    result.Staged = pending; // already downloaded
                    return result;
                }

                if (string.IsNullOrEmpty(latest.VsixUrl))
                {
                    result.Error = "Release " + latest.Version + " has no .vsix attached.";
                    return result;
                }

                var target = Path.Combine(dir, "CodeAstrogator-" + latest.Version + ".vsix");
                var temp = target + ".download";
                if (!await (download ?? DownloadAsync)(latest.VsixUrl!, temp).ConfigureAwait(false))
                {
                    result.Error = "Downloading " + latest.Version + " failed.";
                    TryDelete(temp);
                    return result;
                }

                // Never install something that is not exactly this extension in exactly this version.
                var (id, version) = ReadVsixIdentity(temp);
                if (!string.Equals(id, extensionId, StringComparison.OrdinalIgnoreCase) ||
                    NormalizeVersion(version ?? "") != latest.Version)
                {
                    result.Error = "The downloaded package is not Code Astrogator " + latest.Version + " (id "
                                   + (id ?? "?") + ", version " + (version ?? "?") + ") — not installed.";
                    TryDelete(temp);
                    return result;
                }

                TryDelete(target);
                File.Move(temp, target);
                foreach (var old in Directory.GetFiles(dir, "CodeAstrogator-*.vsix"))
                {
                    if (!string.Equals(old, target, StringComparison.OrdinalIgnoreCase))
                        TryDelete(old); // older downloads
                }

                var staged = new PendingUpdate
                {
                    Version = latest.Version,
                    VsixPath = target,
                    HtmlUrl = latest.HtmlUrl,
                    StagedAt = DateTimeOffset.Now,
                };
                WritePending(dir, staged);
                result.Staged = staged;
                return result;
            }
            catch (Exception ex)
            {
                result.Error = "Update check failed: " + ex.Message;
                return result;
            }
        }

        private static async Task<string?> FetchStringAsync(string url)
        {
            try
            {
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                client.DefaultRequestHeaders.Add("User-Agent", "CodeAstrogator-updater");
                client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
                using var response = await client.GetAsync(url).ConfigureAwait(false);
                return response.IsSuccessStatusCode
                    ? await response.Content.ReadAsStringAsync().ConfigureAwait(false)
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static async Task<bool> DownloadAsync(string url, string targetPath)
        {
            try
            {
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                client.DefaultRequestHeaders.Add("User-Agent", "CodeAstrogator-updater");
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return false;
                using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var file = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await source.CopyToAsync(file).ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ── pending / state files ────────────────────────────────────────────

        public static PendingUpdate? ReadPending(string? updatesDir = null)
        {
            try
            {
                var path = Path.Combine(updatesDir ?? DefaultUpdatesDir, PendingFileName);
                if (!File.Exists(path))
                    return null;
                var pending = JsonConvert.DeserializeObject<PendingUpdate>(File.ReadAllText(path));
                return pending == null || string.IsNullOrEmpty(pending.Version) || string.IsNullOrEmpty(pending.VsixPath)
                    ? null
                    : pending;
            }
            catch
            {
                return null;
            }
        }

        public static void WritePending(string updatesDir, PendingUpdate pending)
        {
            Directory.CreateDirectory(updatesDir);
            File.WriteAllText(Path.Combine(updatesDir, PendingFileName),
                JsonConvert.SerializeObject(pending, Formatting.Indented), new UTF8Encoding(false));
        }

        /// <summary>
        /// Called once per start: when the staged update is no longer newer than what is installed, it
        /// was installed (or superseded) — clean it up. Returns the version when it matches the
        /// installed one exactly, i.e. the update was just applied (for an "Updated to X" note).
        /// </summary>
        public static string? TakeAppliedUpdate(string installedVersion, string? updatesDir = null)
        {
            var pending = ReadPending(updatesDir);
            if (pending == null || IsNewer(pending.Version, installedVersion))
                return null;
            ClearPending(updatesDir);
            return NormalizeVersion(pending.Version) == NormalizeVersion(installedVersion) ? pending.Version : null;
        }

        public static void ClearPending(string? updatesDir = null)
        {
            var dir = updatesDir ?? DefaultUpdatesDir;
            var pending = ReadPending(dir);
            TryDelete(Path.Combine(dir, PendingFileName));
            if (pending != null)
                TryDelete(pending.VsixPath);
        }

        private static DateTimeOffset? LastCheck(string statePath)
        {
            try
            {
                if (!File.Exists(statePath))
                    return null;
                var text = JObject.Parse(File.ReadAllText(statePath)).Value<string>("lastCheck");
                return DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at : (DateTimeOffset?)null;
            }
            catch
            {
                return null;
            }
        }

        private static void WriteLastCheck(string statePath, DateTimeOffset at)
        {
            try
            {
                File.WriteAllText(statePath, new JObject { ["lastCheck"] = at.ToString("o") }.ToString(), new UTF8Encoding(false));
            }
            catch
            {
                // read-only profile — the next check simply runs again
            }
        }

        private static void TryDelete(string? path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
        }

        // ── Visual Studio instance + installer ───────────────────────────────

        /// <summary>Instance id (e.g. <c>d0daee8a</c>) of the installation that contains
        /// <paramref name="devenvPath"/>, from <c>vswhere -all -prerelease -format json</c>.</summary>
        public static string? FindInstanceId(string vswhereJson, string devenvPath)
        {
            try
            {
                foreach (var inst in JArray.Parse(vswhereJson).OfType<JObject>())
                {
                    var installPath = inst.Value<string>("installationPath");
                    if (!string.IsNullOrEmpty(installPath) &&
                        devenvPath.StartsWith(installPath!.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                        return inst.Value<string>("instanceId");
                }
            }
            catch
            {
                // malformed output
            }
            return null;
        }

        /// <summary>Runs vswhere (hidden) and resolves the instance of <paramref name="devenvPath"/>.</summary>
        public static string? ResolveInstanceId(string devenvPath)
        {
            try
            {
                var vswhere = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Microsoft Visual Studio", "Installer", "vswhere.exe");
                if (!File.Exists(vswhere))
                    return null;
                var psi = new ProcessStartInfo
                {
                    FileName = vswhere,
                    Arguments = "-all -prerelease -format json -utf8",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                using var p = Process.Start(psi);
                var json = p!.StandardOutput.ReadToEnd();
                p.WaitForExit(15000);
                return FindInstanceId(json, devenvPath);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The helper: waits until Visual Studio (<c>devenvPid</c>) has exited, then runs VSIXInstaller
        /// quietly against this instance while showing a small <b>progress window</b> ("Installing the
        /// Code Astrogator update to version X…" → "… is installed. You can start Visual Studio again." /
        /// "Starting Visual Studio…", closing itself after 8 s; on failure the exit code + log path stay
        /// up until OK). The console itself stays hidden; without WinForms it installs silently. Exit
        /// codes go to <c>install.log</c> (0 = installed). For "Restart now" it starts Visual Studio
        /// again once the installer is done. It leaves <c>pending.json</c> alone: the next start compares
        /// it with the installed version (see <see cref="TakeAppliedUpdate"/>).
        /// </summary>
        internal const string HelperScript = @"param(
  [int]$DevenvPid, [string]$Installer, [string]$InstanceId, [string]$Vsix,
  [string]$UpdatesDir, [string]$Devenv, [string]$Relaunch, [string]$Version
)
$log = Join-Path $UpdatesDir 'install.log'
function Log($m) { Add-Content -Path $log -Value ((Get-Date).ToString('s') + '  ' + $m) -Encoding UTF8 }
function Restart-VS { if ($Relaunch -eq '1' -and $Devenv) { Log 'restarting Visual Studio'; Start-Process -FilePath $Devenv } }

Log ('waiting for devenv ' + $DevenvPid + ' to exit')
Wait-Process -Id $DevenvPid -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

$a = @('/quiet')
if ($InstanceId) { $a += ('/instanceIds:' + $InstanceId) }
$a += ('/logFile:""' + (Join-Path $UpdatesDir 'vsixinstaller.log') + '""')
$a += ('""' + $Vsix + '""')

$ui = $true
try {
  Add-Type -AssemblyName System.Windows.Forms
  Add-Type -AssemblyName System.Drawing
  [System.Windows.Forms.Application]::EnableVisualStyles()
} catch { $ui = $false }

try {
  Log ('running VSIXInstaller ' + ($a -join ' '))
  $p = Start-Process -FilePath $Installer -ArgumentList $a -PassThru -WindowStyle Hidden
  $null = $p.Handle # keep the handle, otherwise ExitCode is empty after the process ended
} catch { Log ('error: ' + $_); Restart-VS; exit 1 }

# pending.json stays: on the next start the extension sees installed == pending, announces the
# update and cleans up; after a failure it is simply retried at the next close.
if (-not $ui) { $p.WaitForExit(); Log ('VSIXInstaller exit code ' + $p.ExitCode); Restart-VS; exit 0 }

$form = New-Object System.Windows.Forms.Form
$form.Text = 'Code Astrogator update'
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox = $false
$form.MinimizeBox = $false
$form.StartPosition = 'CenterScreen'
$form.TopMost = $true
$form.Font = New-Object System.Drawing.Font('Segoe UI', 9.5)
$form.ClientSize = New-Object System.Drawing.Size(440, 160)

$label = New-Object System.Windows.Forms.Label
$label.Location = New-Object System.Drawing.Point(18, 16)
$label.Size = New-Object System.Drawing.Size(404, 72)
$label.Text = 'Installing the Code Astrogator update to version ' + $Version + ' ...'

$bar = New-Object System.Windows.Forms.ProgressBar
$bar.Location = New-Object System.Drawing.Point(18, 92)
$bar.Size = New-Object System.Drawing.Size(404, 16)
$bar.Style = 'Marquee'
$bar.MarqueeAnimationSpeed = 30

$ok = New-Object System.Windows.Forms.Button
$ok.Text = 'OK'
$ok.Location = New-Object System.Drawing.Point(342, 122)
$ok.Size = New-Object System.Drawing.Size(80, 26)
$ok.Enabled = $false
$ok.Add_Click({ $form.Close() })
$form.AcceptButton = $ok
$form.Controls.AddRange(@($label, $bar, $ok))

$closeTimer = New-Object System.Windows.Forms.Timer
$closeTimer.Interval = 8000
$closeTimer.Add_Tick({ $closeTimer.Stop(); $form.Close() })

$poll = New-Object System.Windows.Forms.Timer
$poll.Interval = 400
$poll.Add_Tick({
  if (-not $p.HasExited) { return }
  $poll.Stop()
  $code = $p.ExitCode
  Log ('VSIXInstaller exit code ' + $code)
  $bar.Visible = $false
  $ok.Enabled = $true
  if ($code -eq 0) {
    if ($Relaunch -eq '1') { $label.Text = 'Code Astrogator ' + $Version + ' is installed. Starting Visual Studio ...' }
    else { $label.Text = 'Code Astrogator ' + $Version + ' is installed. You can start Visual Studio again.' }
    $closeTimer.Start()
  } else {
    $label.Text = 'The update to ' + $Version + ' could not be installed (VSIXInstaller exit code ' + $code + '). It is tried again the next time you close Visual Studio. Details: ' + (Join-Path $UpdatesDir 'vsixinstaller.log')
  }
  Restart-VS
})
$form.Add_Shown({ $form.Activate(); $poll.Start() })
[void]$form.ShowDialog()
";

        /// <summary>Writes the helper script next to the download and builds its (hidden) start info.</summary>
        public static ProcessStartInfo BuildInstallHelper(
            PendingUpdate pending, int devenvPid, string devenvPath, string? instanceId, string updatesDir)
        {
            Directory.CreateDirectory(updatesDir);
            var script = Path.Combine(updatesDir, "install-update.ps1");
            File.WriteAllText(script, HelperScript, new UTF8Encoding(true)); // BOM: Windows PowerShell reads UTF-8 then

            var installer = Path.Combine(Path.GetDirectoryName(devenvPath)!, "VSIXInstaller.exe");
            string Q(string s) => "\"" + s.Replace("\"", "") + "\"";
            var args = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File " + Q(script)
                       + " -DevenvPid " + devenvPid
                       + " -Installer " + Q(installer)
                       + " -InstanceId " + Q(instanceId ?? "")
                       + " -Vsix " + Q(pending.VsixPath)
                       + " -UpdatesDir " + Q(updatesDir)
                       + " -Devenv " + Q(devenvPath)
                       + " -Relaunch " + (pending.Relaunch ? "1" : "0")
                       + " -Version " + Q(pending.Version);

            return new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
        }

        /// <summary>Starts the helper for a pending update of THIS Visual Studio process. Returns false
        /// when there is nothing to install or the helper could not be started.</summary>
        public static bool StartInstallHelper(string installedVersion, string? updatesDir = null)
        {
            try
            {
                var dir = updatesDir ?? DefaultUpdatesDir;
                var pending = ReadPending(dir);
                if (pending == null || !IsNewer(pending.Version, installedVersion) || !File.Exists(pending.VsixPath))
                    return false;

                using var self = Process.GetCurrentProcess();
                var devenv = self.MainModule?.FileName ?? "";
                if (!File.Exists(devenv))
                    return false;

                var psi = BuildInstallHelper(pending, self.Id, devenv, ResolveInstanceId(devenv), dir);
                using var helper = Process.Start(psi);
                return helper != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
