using CodeAstrogator.Bridge;
using Xunit;

namespace CodeAstrogator.Tests
{
    /// <summary>The CLI's total_cost_usd is the running session total (measured 2.1.287: 0.00289 after the
    /// first run, 0.00562 after a --resume'd second) — the footer shows the difference.</summary>
    public class TurnCostTests
    {
        [Fact]
        public void NewConversation_FirstTurn_IsTheWholeTotal() =>
            Assert.Equal(0.00289006, WebViewBridge.TurnCost(null, resumedSession: false, total: 0.00289006)!.Value, 8);

        [Fact]
        public void LaterTurn_IsTheDifferenceToThePreviousTotal() =>
            Assert.Equal(0.00272648, WebViewBridge.TurnCost(0.00289006, resumedSession: true, total: 0.00561654)!.Value, 8);

        [Fact]
        public void ResumedSessionWithoutBaseline_IsUnknown() =>
            Assert.Null(WebViewBridge.TurnCost(null, resumedSession: true, total: 141.06));

        [Fact]
        public void SmallerTotal_MeansAFreshConversation() =>
            Assert.Equal(0.004, WebViewBridge.TurnCost(141.06, resumedSession: true, total: 0.004)!.Value, 8);
    }
}
