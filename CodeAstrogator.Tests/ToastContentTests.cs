using System.Linq;
using System.Xml.Linq;
using CodeAstrogator.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodeAstrogator.Tests
{
    public class ToastContentTests
    {
        [Fact]
        public void TurnEnd_UsesTheAnswerAsPlainText()
        {
            var t = ToastContent.ForTurnEnd("## Done\n\nI renamed **`OrderService`** in [Cart.cs](src/Cart.cs).\n- tests pass", null);
            Assert.Equal("Claude finished", t.Title);
            Assert.Equal("Done I renamed OrderService in Cart.cs. tests pass", t.Body);
        }

        [Fact]
        public void TurnEnd_WithoutText_SaysItIsTheUsersTurn()
        {
            Assert.Equal("Your turn.", ToastContent.ForTurnEnd("  ", null).Body);
        }

        [Fact]
        public void TurnEnd_ErrorWins()
        {
            var t = ToastContent.ForTurnEnd("partial answer", "API Error: overloaded");
            Assert.Equal("Claude stopped with an error", t.Title);
            Assert.Equal("API Error: overloaded", t.Body);
        }

        [Fact]
        public void Snippet_CutsWithEllipsis_AndNeverSplitsAnEmoji()
        {
            var cut = ToastContent.Snippet(new string('a', 300), 50);
            Assert.Equal(50, cut.Length);
            Assert.EndsWith("…", cut);

            // the surrogate pair straddles the cut position → it is dropped as a whole
            var emoji = ToastContent.Snippet(new string('a', 48) + "\U0001F680" + "bbbb", 50);
            Assert.Equal(new string('a', 48) + "…", emoji);
        }

        [Fact]
        public void Permission_ShowsCommandOrFileName()
        {
            var bash = ToastContent.ForPermission("Bash", new JObject { ["command"] = "dotnet test SampleShop.sln" });
            Assert.Equal("Claude needs your permission", bash.Title);
            Assert.Equal("Bash: dotnet test SampleShop.sln", bash.Body);

            var glob = ToastContent.ForPermission("PowerShell", new JObject { ["command"] = "Get-ChildItem src/**/*.cs `\n  -Recurse" });
            Assert.Equal("PowerShell: Get-ChildItem src/**/*.cs ` -Recurse", glob.Body); // no Markdown cleanup for commands

            var edit = ToastContent.ForPermission("Edit", new JObject { ["file_path"] = @"C:\src\SampleShop\Cart.cs" });
            Assert.Equal("Edit: Cart.cs", edit.Body);

            var mcp = ToastContent.ForPermission("mcp__github__create_issue", new JObject());
            Assert.Equal("github: create_issue", mcp.Body);
        }

        [Fact]
        public void Permission_ExitPlanMode_IsThePlan()
        {
            var t = ToastContent.ForPermission("ExitPlanMode", new JObject { ["plan"] = "# Plan\n1. Add the cart total" });
            Assert.Equal("Claude's plan is ready for review", t.Title);
            Assert.Equal("Plan Add the cart total", t.Body);
        }

        [Fact]
        public void Question_FirstQuestionPlusCount()
        {
            var input = JObject.Parse(@"{""questions"":[{""question"":""Which database?""},{""question"":""Add tests?""}]}");
            var t = ToastContent.ForQuestion(input);
            Assert.Equal("Claude has a question", t.Title);
            Assert.Equal("Which database? (+1 more)", t.Body);
        }

        [Fact]
        public void Xml_IsEscapedAndWellFormed()
        {
            var xml = ToastContent.BuildXml(
                new ToastText { Title = "Claude needs your permission", Body = "Bash: a < b && echo \"x\"\u0007" },
                "Fix <Cart> & total", "toolu_01");
            var doc = XDocument.Parse(xml); // throws if not well-formed
            var texts = doc.Root!.Element("visual")!.Element("binding")!.Elements("text").ToArray();
            Assert.Equal(3, texts.Length);
            Assert.Equal("Bash: a < b && echo \"x\"", texts[1].Value); // control character dropped
            Assert.Equal("attribution", (string?)texts[2].Attribute("placement"));
            Assert.Equal("Fix <Cart> & total", texts[2].Value);
            Assert.Equal("toolu_01", (string?)doc.Root.Attribute("launch"));
        }

        [Fact]
        public void ScopedTag_IsPerProcess_AndFitsWindowsLimit()
        {
            // two Visual Studio instances must not replace (or remove) each other's "turn" toast
            Assert.NotEqual(ToastContent.ScopedTag("turn", 1200), ToastContent.ScopedTag("turn", 3400));
            Assert.Equal("p1200-turn", ToastContent.ScopedTag("turn", 1200));

            var longId = "toolu_" + new string('x', 70) + "END";
            var tag = ToastContent.ScopedTag(longId, 123456);
            Assert.Equal(ToastContent.MaxTagLength, tag.Length);
            Assert.StartsWith("p123456-", tag); // the prefix survives the cut
            Assert.EndsWith("END", tag);        // the unique end of the id too
        }

        [Fact]
        public void Attribution_NamesWorkspaceAndChat()
        {
            Assert.Equal("SampleShop · Fix the cart total",
                ToastContent.Attribution(@"C:\src\SampleShop\", "Fix the cart total"));
            Assert.Equal("SampleShop", ToastContent.Attribution(@"C:\src\SampleShop", "Untitled"));
            Assert.Equal("Fix the cart total", ToastContent.Attribution(null, "Fix the cart total"));
            Assert.Null(ToastContent.Attribution("", "Untitled"));
        }

        [Fact]
        public void Xml_WithoutBodyOrAttribution_HasOnlyTheTitle()
        {
            var xml = ToastContent.BuildXml(new ToastText { Title = "Claude finished" }, null, "turn");
            Assert.Single(XDocument.Parse(xml).Root!.Element("visual")!.Element("binding")!.Elements("text"));
        }
    }
}
