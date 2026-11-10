using System;
using System.IO;
using System.Linq;
using CodeAstrogator.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodeAstrogator.Tests
{
    public class SkillCatalogTests : IDisposable
    {
        private readonly string _root;
        private readonly string _userClaude;
        private readonly string _repo;
        private readonly string _workspace;

        public SkillCatalogTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ca-skills-" + Guid.NewGuid().ToString("n").Substring(0, 8));
            _userClaude = Path.Combine(_root, "home", ".claude");
            _repo = Path.Combine(_root, "SampleShop");
            _workspace = Path.Combine(_repo, "src", "Shop.Web");
            Directory.CreateDirectory(Path.Combine(_repo, ".git"));
            Directory.CreateDirectory(_workspace);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private static void Skill(string skillsDir, string folder, string frontMatter)
        {
            Directory.CreateDirectory(Path.Combine(skillsDir, folder));
            File.WriteAllText(Path.Combine(skillsDir, folder, "SKILL.md"), "---\n" + frontMatter + "\n---\n\nBody\n");
        }

        [Fact]
        public void FrontMatter_PlainQuotedAndBlockValues()
        {
            var fm = SkillCatalog.ParseFrontMatter("---\nname: release-notes\ndescription: >\n  Writes release notes\n  from the changelog.\nuser-invocable: no\n---\n# x");
            Assert.Equal("release-notes", fm.Name);
            Assert.Equal("Writes release notes from the changelog.", fm.Description);
            Assert.False(fm.UserInvocable);

            var quoted = SkillCatalog.ParseFrontMatter("---\ndescription: \"Use when: the cart total \\\"breaks\\\"\"\n---");
            Assert.Equal("Use when: the cart total \"breaks\"", quoted.Description);

            Assert.Null(SkillCatalog.ParseFrontMatter("no front matter\nname: x").Name);
        }

        [Fact]
        public void Scan_FindsPersonalProjectParentsPluginsAndBundled()
        {
            Skill(Path.Combine(_userClaude, "skills"), "deploy", "name: deploy\ndescription: Deploys SampleShop");
            Skill(Path.Combine(_userClaude, "skills", "synced"), "from-web", "description: Synced one");
            Skill(Path.Combine(_repo, ".claude", "skills"), "repo-wide", "description: Repo level");
            Skill(Path.Combine(_workspace, ".claude", "skills"), "web-only", "description: Workspace level");
            Skill(Path.Combine(_workspace, ".claude", "skills"), "deploy", "description: Shadowed by the personal one");
            var pluginDir = Path.Combine(_root, "plugin");
            Skill(Path.Combine(pluginDir, "skills"), "commit", "description: Commit helper");

            var skills = SkillCatalog.Scan(new SkillScanContext
            {
                UserClaudeDir = _userClaude,
                Workspace = _workspace,
                Plugins = new[]
                {
                    new InstalledPlugin { Id = "commit-commands@official", Enabled = true, InstallPath = pluginDir },
                    new InstalledPlugin { Id = "off-plugin@official", Enabled = false, InstallPath = pluginDir },
                },
                LoadedNames = new[] { "deploy", "commit-commands:commit", "doctor" },
            });

            var byName = skills.ToDictionary(s => s.Name);
            Assert.Equal(SkillSource.Personal, byName["deploy"].Source);           // personal beats project
            Assert.Equal("Deploys SampleShop", byName["deploy"].Description);
            Assert.Equal(SkillSource.Synced, byName["from-web"].Source);
            Assert.Equal(SkillSource.Project, byName["repo-wide"].Source);         // parent up to the repo root
            Assert.Equal(SkillSource.Project, byName["web-only"].Source);
            Assert.Equal(SkillSource.Plugin, byName["commit-commands:commit"].Source);
            Assert.False(byName["commit-commands:commit"].CanOverride);
            Assert.DoesNotContain("off-plugin:commit", byName.Keys);               // disabled plugin contributes nothing
            Assert.Equal(SkillSource.Bundled, byName["doctor"].Source);            // loaded but not on disk
            Assert.True(byName["deploy"].Loaded);
            Assert.False(byName["web-only"].Loaded);
        }

        [Fact]
        public void ProjectSkillDirs_OnlyTheWorkspaceOutsideARepo()
        {
            var lone = Path.Combine(_root, "lone");
            Directory.CreateDirectory(lone);
            Assert.Single(SkillCatalog.ProjectSkillDirs(lone));
            Assert.Equal(3, SkillCatalog.ProjectSkillDirs(_workspace).Count); // Shop.Web, src, SampleShop
        }

        [Fact]
        public void Overrides_LocalBeatsProjectBeatsUser_AndWritingKeepsOtherKeys()
        {
            var userFile = ClaudeSettingsFiles.PathOf("user", null, _userClaude)!;
            Directory.CreateDirectory(Path.GetDirectoryName(userFile)!);
            File.WriteAllText(userFile, "{ \"model\": \"opus\", \"skillOverrides\": { \"deploy\": \"off\", \"lint\": \"name-only\" } }");

            Assert.True(SkillCatalog.WriteOverride("local", _workspace, "deploy", "user-invocable-only", _userClaude));
            var o = SkillCatalog.ReadOverrides(_workspace, _userClaude);
            Assert.Equal(("user-invocable-only", "local"), o["deploy"]);
            Assert.Equal(("name-only", "user"), o["lint"]);

            // removing the local entry falls back to the user one; the user file's other keys stay
            Assert.True(SkillCatalog.WriteOverride("local", _workspace, "deploy", null, _userClaude));
            Assert.Equal(("off", "user"), SkillCatalog.ReadOverrides(_workspace, _userClaude)["deploy"]);
            Assert.True(SkillCatalog.WriteOverride("user", _workspace, "deploy", "on", _userClaude));
            var user = JObject.Parse(File.ReadAllText(userFile));
            Assert.Equal("opus", user.Value<string>("model"));
            Assert.Equal("on", user["skillOverrides"]!.Value<string>("deploy"));
            Assert.Equal("name-only", user["skillOverrides"]!.Value<string>("lint"));
        }

        [Fact]
        public void WriteOverride_RefusesUnreadableFilesAndUnknownValues()
        {
            var local = ClaudeSettingsFiles.PathOf("local", _workspace)!;
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            File.WriteAllText(local, "{ broken json");
            Assert.False(SkillCatalog.WriteOverride("local", _workspace, "deploy", "off"));
            Assert.Equal("{ broken json", File.ReadAllText(local)); // untouched
            Assert.Throws<ArgumentException>(() => SkillCatalog.WriteOverride("local", _workspace, "deploy", "hidden"));
            Assert.False(SkillCatalog.WriteOverride("project", null, "deploy", "off")); // no workspace → no file
        }

        [Fact]
        public void SetState_On_RemovesOrWritesExplicitly()
        {
            var userFile = ClaudeSettingsFiles.PathOf("user", null, _userClaude)!;
            Directory.CreateDirectory(Path.GetDirectoryName(userFile)!);
            File.WriteAllText(userFile, "{ \"skillOverrides\": { \"deploy\": \"off\" } }");

            // local "off" then local "on": the user file still hides it → "on" must be written explicitly
            Assert.True(SkillCatalog.SetState("local", _workspace, "deploy", "off", _userClaude));
            Assert.True(SkillCatalog.SetState("local", _workspace, "deploy", "on", _userClaude));
            Assert.Equal(("on", "local"), SkillCatalog.ReadOverrides(_workspace, _userClaude)["deploy"]);

            // nothing else hides "lint" → "on" just removes the entry
            Assert.True(SkillCatalog.SetState("local", _workspace, "lint", "name-only", _userClaude));
            Assert.True(SkillCatalog.SetState("local", _workspace, "lint", "on", _userClaude));
            Assert.False(SkillCatalog.ReadOverrides(_workspace, _userClaude).ContainsKey("lint"));
        }

        [Fact]
        public void BundledSwitch_ReadsAndWrites()
        {
            Assert.Equal((false, (string?)null), SkillCatalog.ReadBundledDisabled(_workspace, _userClaude));
            Assert.True(SkillCatalog.WriteBundledDisabled("local", _workspace, true, _userClaude));
            Assert.Equal((true, "local"), SkillCatalog.ReadBundledDisabled(_workspace, _userClaude));
        }

        [Fact]
        public void CreateAndDelete_PersonalSkill()
        {
            var dir = Path.Combine(_userClaude, "skills");
            var file = SkillCatalog.CreateSkill(dir, "release-notes", "Use when: writing notes");
            var fm = SkillCatalog.ParseFrontMatter(File.ReadAllText(file));
            Assert.Equal("release-notes", fm.Name);
            Assert.Equal("Use when: writing notes", fm.Description); // the colon forced quoting — still round-trips
            Assert.Throws<IOException>(() => SkillCatalog.CreateSkill(dir, "release-notes", "again"));
            Assert.Throws<ArgumentException>(() => SkillCatalog.CreateSkill(dir, "Release Notes", "x"));

            var skill = SkillCatalog.Scan(new SkillScanContext { UserClaudeDir = _userClaude }).Single();
            SkillCatalog.DeleteSkill(skill);
            Assert.False(Directory.Exists(Path.GetDirectoryName(file)));
            Assert.Throws<InvalidOperationException>(() => SkillCatalog.DeleteSkill(new SkillInfo { Source = SkillSource.Plugin, SkillFile = file }));
        }
    }
}
