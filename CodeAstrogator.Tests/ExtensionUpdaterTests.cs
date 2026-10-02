using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using CodeAstrogator.Core;
using Xunit;

namespace CodeAstrogator.Tests
{
    public class ExtensionUpdaterTests : IDisposable
    {
        private const string Id = "CodeAstrogator.9049dcc4-f4c0-4748-b8cd-57e9a981c404";
        private readonly string _dir;

        public ExtensionUpdaterTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ca-updater-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        // Real /releases/latest shape (trimmed), v0.8.2 as published 2026-09-24.
        private static string ReleaseJson(string tag, bool withVsix = true) =>
            "{\"tag_name\":\"" + tag + "\",\"html_url\":\"https://github.com/finex7070/CodeAstrogator/releases/tag/" + tag + "\"," +
            "\"assets\":[" + (withVsix
                ? "{\"name\":\"CodeAstrogator.vsix\",\"browser_download_url\":\"https://example.invalid/" + tag + "/CodeAstrogator.vsix\"}"
                : "{\"name\":\"notes.txt\",\"browser_download_url\":\"https://example.invalid/notes.txt\"}") + "]}";

        /// <summary>A minimal VSIX: a zip carrying extension.vsixmanifest with the given identity.</summary>
        private static void WriteVsix(string path, string id, string version)
        {
            var manifest =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<PackageManifest Version=\"2.0.0\" xmlns=\"http://schemas.microsoft.com/developer/vsx-schema/2011\">" +
                "<Metadata><Identity Id=\"" + id + "\" Version=\"" + version + "\" Language=\"en-US\" Publisher=\"Code Astrogator\" />" +
                "</Metadata></PackageManifest>";
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            var entry = zip.CreateEntry("extension.vsixmanifest");
            using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            w.Write(manifest);
        }

        private Func<string, string, Task<bool>> DownloadAs(string id, string version) =>
            (url, target) => { WriteVsix(target, id, version); return Task.FromResult(true); };

        // ── parsing ─────────────────────────────────────────────────────────

        [Fact]
        public void ParsesTagAndVsixAsset()
        {
            var r = ExtensionUpdater.ParseLatestRelease(ReleaseJson("v0.8.2"));
            Assert.NotNull(r);
            Assert.Equal("0.8.2", r!.Version);
            Assert.EndsWith("/CodeAstrogator.vsix", r.VsixUrl);
            Assert.Contains("/releases/tag/v0.8.2", r.HtmlUrl);
        }

        [Fact]
        public void ReleaseWithoutVsix_HasNoDownload()
        {
            Assert.Null(ExtensionUpdater.ParseLatestRelease(ReleaseJson("v0.8.2", withVsix: false))!.VsixUrl);
            Assert.Null(ExtensionUpdater.ParseLatestRelease("{}"));
            Assert.Null(ExtensionUpdater.ParseLatestRelease("not json"));
        }

        [Theory]
        [InlineData("0.8.3", "0.8.2", true)]
        [InlineData("v0.9.0", "0.8.12", true)]
        [InlineData("0.8.2", "0.8.2", false)]
        [InlineData("0.8.1", "0.8.2", false)]
        [InlineData("1.0", "0.99.99", true)]
        public void VersionComparison_IsNumeric(string remote, string installed, bool newer)
        {
            Assert.Equal(newer, ExtensionUpdater.IsNewer(remote, installed));
        }

        [Fact]
        public void ReadsTheIdentityOutOfAVsix()
        {
            var path = Path.Combine(_dir, "x.vsix");
            WriteVsix(path, Id, "0.8.4");
            var (id, version) = ExtensionUpdater.ReadVsixIdentity(path);
            Assert.Equal(Id, id);
            Assert.Equal("0.8.4", version);
            Assert.Equal((null, null), ExtensionUpdater.ReadVsixIdentity(Path.Combine(_dir, "missing.vsix")));
        }

        [Fact]
        public void FindsTheInstanceOfTheRunningDevenv()
        {
            // Measured vswhere output (both instances on the dev machine).
            const string vswhere = "[" +
                "{\"instanceId\":\"c71b4c8f\",\"installationPath\":\"C:\\\\Program Files\\\\Microsoft Visual Studio\\\\2022\\\\Community\"}," +
                "{\"instanceId\":\"d0daee8a\",\"installationPath\":\"C:\\\\Program Files\\\\Microsoft Visual Studio\\\\18\\\\Community\"}]";
            Assert.Equal("d0daee8a", ExtensionUpdater.FindInstanceId(vswhere,
                @"C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe"));
            Assert.Equal("c71b4c8f", ExtensionUpdater.FindInstanceId(vswhere,
                @"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe"));
            Assert.Null(ExtensionUpdater.FindInstanceId(vswhere, @"D:\Other\devenv.exe"));
        }

        // ── check + stage ───────────────────────────────────────────────────

        [Fact]
        public async Task NewerRelease_IsDownloadedVerifiedAndStaged()
        {
            var result = await ExtensionUpdater.CheckAndStageAsync("o/r", Id, "0.8.2", force: true, updatesDir: _dir,
                fetch: _ => Task.FromResult<string?>(ReleaseJson("v0.8.3")), download: DownloadAs(Id, "0.8.3"));

            Assert.Null(result.Error);
            Assert.NotNull(result.Staged);
            Assert.Equal("0.8.3", result.Staged!.Version);
            Assert.True(File.Exists(result.Staged.VsixPath));
            Assert.Equal("0.8.3", ExtensionUpdater.ReadPending(_dir)!.Version);
        }

        [Fact]
        public async Task PackageOfAnotherExtension_IsRejected()
        {
            var result = await ExtensionUpdater.CheckAndStageAsync("o/r", Id, "0.8.2", force: true, updatesDir: _dir,
                fetch: _ => Task.FromResult<string?>(ReleaseJson("v0.8.3")), download: DownloadAs("Someone.Else", "0.8.3"));

            Assert.Null(result.Staged);
            Assert.Contains("not Code Astrogator", result.Error);
            Assert.Null(ExtensionUpdater.ReadPending(_dir));
            Assert.Empty(Directory.GetFiles(_dir, "*.vsix*"));
        }

        [Fact]
        public async Task PackageWithAnotherVersionThanTheTag_IsRejected()
        {
            var result = await ExtensionUpdater.CheckAndStageAsync("o/r", Id, "0.8.2", force: true, updatesDir: _dir,
                fetch: _ => Task.FromResult<string?>(ReleaseJson("v0.8.3")), download: DownloadAs(Id, "0.8.2"));

            Assert.Null(result.Staged);
            Assert.NotNull(result.Error);
        }

        [Fact]
        public async Task UpToDate_StagesNothing()
        {
            var downloaded = false;
            var result = await ExtensionUpdater.CheckAndStageAsync("o/r", Id, "0.8.2", force: true, updatesDir: _dir,
                fetch: _ => Task.FromResult<string?>(ReleaseJson("v0.8.2")),
                download: (u, t) => { downloaded = true; return Task.FromResult(true); });

            Assert.True(result.Checked);
            Assert.Null(result.Staged);
            Assert.Null(result.Error);
            Assert.False(downloaded);
        }

        [Fact]
        public async Task SecondCheckWithinTheInterval_DoesNotAskGitHub()
        {
            var calls = 0;
            Func<string, Task<string?>> fetch = _ => { calls++; return Task.FromResult<string?>(ReleaseJson("v0.8.2")); };

            await ExtensionUpdater.CheckAndStageAsync("o/r", Id, "0.8.2", updatesDir: _dir, fetch: fetch);
            var second = await ExtensionUpdater.CheckAndStageAsync("o/r", Id, "0.8.2", updatesDir: _dir, fetch: fetch);
            await ExtensionUpdater.CheckAndStageAsync("o/r", Id, "0.8.2", force: true, updatesDir: _dir, fetch: fetch);

            Assert.False(second.Checked);
            Assert.Equal(2, calls); // first + forced
        }

        [Fact]
        public async Task AlreadyStagedVersion_IsNotDownloadedAgain()
        {
            Func<string, Task<string?>> fetch = _ => Task.FromResult<string?>(ReleaseJson("v0.8.3"));
            await ExtensionUpdater.CheckAndStageAsync("o/r", Id, "0.8.2", force: true, updatesDir: _dir, fetch: fetch, download: DownloadAs(Id, "0.8.3"));

            var downloads = 0;
            var again = await ExtensionUpdater.CheckAndStageAsync("o/r", Id, "0.8.2", force: true, updatesDir: _dir, fetch: fetch,
                download: (u, t) => { downloads++; return Task.FromResult(false); });

            Assert.Equal(0, downloads);
            Assert.Equal("0.8.3", again.Staged!.Version);
        }

        [Fact]
        public async Task AppliedUpdate_IsAnnouncedOnceAndCleanedUp()
        {
            await ExtensionUpdater.CheckAndStageAsync("o/r", Id, "0.8.2", force: true, updatesDir: _dir,
                fetch: _ => Task.FromResult<string?>(ReleaseJson("v0.8.3")), download: DownloadAs(Id, "0.8.3"));

            Assert.Null(ExtensionUpdater.TakeAppliedUpdate("0.8.2", _dir));      // not installed yet → keep
            Assert.NotNull(ExtensionUpdater.ReadPending(_dir));
            Assert.Equal("0.8.3", ExtensionUpdater.TakeAppliedUpdate("0.8.3", _dir)); // installed → announce
            Assert.Null(ExtensionUpdater.ReadPending(_dir));
            Assert.Empty(Directory.GetFiles(_dir, "*.vsix"));
            Assert.Null(ExtensionUpdater.TakeAppliedUpdate("0.8.3", _dir));      // only once
        }

        // ── installer helper ────────────────────────────────────────────────

        [Fact]
        public void Helper_WaitsForThisDevenvAndTargetsThisInstance()
        {
            var pending = new PendingUpdate { Version = "0.8.3", VsixPath = Path.Combine(_dir, "CodeAstrogator-0.8.3.vsix"), Relaunch = true };
            var psi = ExtensionUpdater.BuildInstallHelper(pending, 4242,
                @"C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe", "d0daee8a", _dir);

            Assert.Equal("powershell.exe", psi.FileName);
            Assert.True(psi.CreateNoWindow);
            Assert.Contains("-DevenvPid 4242", psi.Arguments);
            Assert.Contains("-Installer \"C:\\Program Files\\Microsoft Visual Studio\\18\\Community\\Common7\\IDE\\VSIXInstaller.exe\"", psi.Arguments);
            Assert.Contains("-InstanceId \"d0daee8a\"", psi.Arguments);
            Assert.Contains("-Relaunch 1", psi.Arguments);
            Assert.Contains("-Version \"0.8.3\"", psi.Arguments); // shown in the progress window
            Assert.True(File.Exists(Path.Combine(_dir, "install-update.ps1")));
        }
    }
}
