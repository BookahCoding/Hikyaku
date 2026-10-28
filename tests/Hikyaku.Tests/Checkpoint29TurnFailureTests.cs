using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 29 — failed-turn recovery cards: plain words, honest delivery, and never an invitation to a duplicate send.</summary>
public class Checkpoint29TurnFailureTests
{
    [Theory]
    [InlineData(PromptDeliveryState.Pending, true)]
    [InlineData(PromptDeliveryState.NotReceived, true)]
    [InlineData(PromptDeliveryState.Acknowledged, false)]
    [InlineData(PromptDeliveryState.Uncertain, false)]
    [InlineData(PromptDeliveryState.Partial, false)]
    [InlineData(PromptDeliveryState.Settled, false)]
    public void After_a_dropped_connection_the_prompt_may_be_put_back_only_when_it_is_known_not_to_have_arrived(PromptDeliveryState state, bool offered)
    {
        var card = TurnFailures.Describe(TurnFailureKind.ConnectionDropped, null, state, false);
        Assert.Equal(offered, card.CanRestorePrompt);
        // The two safe states can never be reached with an auto-resend: the policy has none.
        Assert.False(PromptResendPolicy.MayAutoResend(state));
    }

    [Fact]
    public void Uncertain_delivery_says_to_check_the_history_first_and_that_nothing_is_resent_automatically()
    {
        var card = TurnFailures.Describe(TurnFailureKind.ConnectionDropped, null, PromptDeliveryState.Uncertain, false);
        Assert.Contains("may already have your message", card.DeliveryNote);
        Assert.Contains("Check the chat history", card.DeliveryNote);
        Assert.Contains("nothing is resent automatically", card.DeliveryNote);
        Assert.False(card.CanRestorePrompt);
    }

    [Fact]
    public void A_partial_reply_is_said_to_be_kept()
    {
        var card = TurnFailures.Describe(TurnFailureKind.ConnectionDropped, null, PromptDeliveryState.Partial, false);
        Assert.Contains("part of the reply was kept", card.DeliveryNote);
        Assert.False(card.CanRestorePrompt);
    }

    [Fact]
    public void A_gateway_error_means_the_message_arrived_and_the_user_may_put_it_back_to_ask_again()
    {
        var card = TurnFailures.Describe(TurnFailureKind.GatewayError, "model overloaded", PromptDeliveryState.Settled, false);
        Assert.True(card.CanRestorePrompt);
        Assert.Equal("The gateway reported an error", card.Title);
        Assert.Contains("reached the gateway", card.DeliveryNote);
        Assert.Equal("model overloaded", card.Detail);
    }

    [Fact]
    public void A_failed_attachment_means_nothing_was_sent_whatever_the_delivery_state_says()
    {
        var card = TurnFailures.Describe(TurnFailureKind.AttachFailed, "too big", PromptDeliveryState.Settled, true);
        Assert.True(card.CanRestorePrompt);
        Assert.Contains("Nothing was sent", card.DeliveryNote);
        Assert.Contains("Attachments and shared terminal text are not restored", card.DeliveryNote);
    }

    [Fact]
    public void Attachments_or_terminal_text_are_only_mentioned_when_the_prompt_can_be_put_back()
    {
        Assert.DoesNotContain("not restored", TurnFailures.Describe(TurnFailureKind.ConnectionDropped, null, PromptDeliveryState.Uncertain, true).DeliveryNote);
        Assert.Contains("not restored", TurnFailures.Describe(TurnFailureKind.ClientError, null, PromptDeliveryState.Pending, true).DeliveryNote);
        Assert.DoesNotContain("not restored", TurnFailures.Describe(TurnFailureKind.ClientError, null, PromptDeliveryState.Pending, false).DeliveryNote);
    }

    [Fact]
    public void A_client_error_before_the_message_left_says_nothing_was_sent()
    {
        var card = TurnFailures.Describe(TurnFailureKind.ClientError, "NullReferenceException", PromptDeliveryState.Pending, false);
        Assert.Equal("Something went wrong in Hikyaku", card.Title);
        Assert.True(card.CanRestorePrompt);
        Assert.Contains("did not reach the gateway", card.DeliveryNote);
    }

    [Fact]
    public void Detail_is_cleaned_and_bounded()
    {
        Assert.Null(TurnFailures.Clean(null));
        Assert.Null(TurnFailures.Clean("  \u0007 "));
        Assert.Equal("bad thing", TurnFailures.Clean("bad\u0007 thing"));
        var longDetail = TurnFailures.Clean(new string('x', 1000))!;
        Assert.Equal(TurnFailures.MaxDetailChars + 1, longDetail.Length);
        Assert.EndsWith("…", longDetail);
    }
}
