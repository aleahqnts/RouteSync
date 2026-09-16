using FleetWise.Models;
using FleetWise.Services;

namespace RouteSyncWeb.Tests;

/// <summary>A leave request's history, read off its row.</summary>
public class LeaveHistoryTests
{
    private const int Driver = 1020;
    private const int Approver = 900;
    private const int Answerer = 901;

    private static readonly IReadOnlyDictionary<int, string> Names = new Dictionary<int, string>
    {
        [Driver] = "Chester Alcanzarin",
        [Approver] = "Admin User",
        [Answerer] = "Dispatch Desk",
    };

    private static readonly DateTime Filed = new(2026, 9, 4, 14, 51, 0);
    private static readonly DateTime Approved = new(2026, 9, 4, 14, 52, 0);
    private static readonly DateTime Asked = new(2026, 9, 5, 8, 0, 0);
    private static readonly DateTime Answered = new(2026, 9, 5, 9, 30, 0);

    private static LeaveRequest Granted() => new()
    {
        RequestId = 1,
        UserId = Driver,
        LeaveType = "Vacation",
        StartDate = new DateTime(2026, 9, 21),
        EndDate = new DateTime(2026, 9, 22),
        Reason = "Province",
        Status = "Approved",
        FiledAt = Filed,
        DecidedAt = Approved,
        DecidedBy = Approver,
        DecisionNote = "Enjoy",
    };

    private static LeaveRequest AskedToCancel()
    {
        var r = Granted();
        r.WithdrawRequestedAt = Asked;
        r.WithdrawReason = "Plans changed";
        return r;
    }

    private static string[] Actions(LeaveRequest r) =>
        LeaveHistory.Of(r, Names).Select(e => e.Action).ToArray();

    [Fact]
    public void An_accepted_cancellation_keeps_the_approval_before_it()
    {
        var r = AskedToCancel();
        r.Status = "Cancelled";
        r.WithdrawAnsweredAt = Answered;
        r.WithdrawAnsweredBy = Answerer;
        r.WithdrawAnswerNote = "Covered by Ana";

        var events = LeaveHistory.Of(r, Names);

        Assert.Equal(new[] { "Filed", "Approved", "Cancellation asked for", "Cancellation accepted" },
            events.Select(e => e.Action));

        var approval = events[1];
        Assert.Equal(Approved, approval.At);
        Assert.Equal("Admin User", approval.By);
        Assert.Equal("Enjoy", approval.Note);

        var accepted = events[3];
        Assert.Equal(Answered, accepted.At);
        Assert.Equal("Dispatch Desk", accepted.By);
        Assert.Equal("Covered by Ana", accepted.Note);

        Assert.True(LeaveEntitlement.AskAccepted(r));
        Assert.False(LeaveEntitlement.DecisionIsAcceptance(r));
        Assert.Equal("Covered by Ana", LeaveHistory.StatusNote(r));
    }

    [Fact]
    public void An_acceptance_written_over_the_approval_reads_as_the_acceptance_alone()
    {
        // The shape of a row accepted before the answer had columns of its own: the
        // decision fields hold the acceptance and nobody is named as the answerer.
        var r = AskedToCancel();
        r.Status = "Cancelled";
        r.WithdrawAnsweredAt = Answered;
        r.DecidedAt = Answered;
        r.DecidedBy = Answerer;
        r.DecisionNote = "Cancelled at the driver's request.";

        var events = LeaveHistory.Of(r, Names);

        Assert.Equal(new[] { "Filed", "Cancellation asked for", "Cancellation accepted" },
            events.Select(e => e.Action));
        Assert.Equal("Dispatch Desk", events[2].By);
        Assert.Equal("Cancelled at the driver's request.", events[2].Note);

        Assert.True(LeaveEntitlement.DecisionIsAcceptance(r));
        Assert.Equal("Cancelled at the driver's request.", LeaveHistory.StatusNote(r));
    }

    [Fact]
    public void An_acceptance_with_no_answerer_still_keeps_an_approval_made_before_the_asking()
    {
        var r = AskedToCancel();
        r.Status = "Cancelled";
        r.WithdrawAnsweredAt = Answered;

        Assert.False(LeaveEntitlement.DecisionIsAcceptance(r));
        Assert.Equal(new[] { "Filed", "Approved", "Cancellation asked for", "Cancellation accepted" }, Actions(r));
        Assert.Equal("", LeaveHistory.Of(r, Names)[3].By);
    }

    [Fact]
    public void A_declined_cancellation_names_who_answered_rather_than_who_approved()
    {
        var r = AskedToCancel();
        r.WithdrawAnsweredAt = Answered;
        r.WithdrawAnsweredBy = Answerer;
        r.WithdrawAnswerNote = "Nobody to cover";

        var events = LeaveHistory.Of(r, Names);

        Assert.Equal(new[] { "Filed", "Approved", "Cancellation asked for", "Cancellation declined" },
            events.Select(e => e.Action));
        Assert.Equal("Dispatch Desk", events[3].By);
        Assert.Equal("Nobody to cover", events[3].Note);
        Assert.Equal("Enjoy", LeaveHistory.StatusNote(r));
    }

    [Fact]
    public void A_decline_with_no_answerer_names_nobody()
    {
        var r = AskedToCancel();
        r.WithdrawAnsweredAt = Answered;

        Assert.Equal("", LeaveHistory.Of(r, Names)[3].By);
    }

    [Fact]
    public void An_outstanding_asking_has_no_answer_yet()
    {
        Assert.Equal(new[] { "Filed", "Approved", "Cancellation asked for" }, Actions(AskedToCancel()));
    }

    [Fact]
    public void A_request_the_driver_withdrew_while_waiting_reads_as_cancelled_by_them()
    {
        var r = Granted();
        r.Status = "Cancelled";
        r.DecidedBy = null;
        r.DecisionNote = null;

        var events = LeaveHistory.Of(r, Names);

        Assert.False(LeaveEntitlement.AskAccepted(r));
        Assert.Equal(new[] { "Filed", "Cancelled" }, events.Select(e => e.Action));
        Assert.Equal("Chester Alcanzarin", events[1].By);
    }

    [Fact]
    public void Leave_revoked_while_an_asking_was_open_reads_approved_asked_revoked()
    {
        var r = AskedToCancel();
        r.Status = "Revoked";
        r.WithdrawAnsweredAt = Answered;
        r.WithdrawAnsweredBy = Approver;
        r.RevokedAt = Answered;
        r.RevokedBy = Approver;
        r.RevokeNote = "Needed on the road";

        var events = LeaveHistory.Of(r, Names);

        Assert.Equal(new[] { "Filed", "Approved", "Cancellation asked for", "Revoked" },
            events.Select(e => e.Action));
        Assert.Equal("All days taken back. Needed on the road", events[3].Note);
    }
}
