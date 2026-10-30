namespace FleetWise.Models
{
    // The leave queue as the Requests page draws it.
    public class LeaveQueueViewModel
    {
        // Which status is being shown. "All" shows every one.
        public string Status { get; set; } = "Pending";

        // Waiting on a decision, counted whatever is being shown.
        public int PendingCount { get; set; }

        public List<LeaveRowViewModel> Rows { get; set; } = new();
    }

    // One request, with the context needed to decide it.
    public class LeaveRowViewModel
    {
        public long RequestId { get; set; }
        public int DriverId { get; set; }
        public string DriverName { get; set; } = "";
        public string LeaveType { get; set; } = "";

        // The dates covered as one phrase, for the places that name a request in a
        // sentence: the decision dialog, the history panel, the audit trail.
        public string Span { get; set; } = "";

        // First day off, in a column of its own.
        //
        // The queue is read down the columns. A span written as one string reads well in
        // a sentence and badly in a table, where the eye is comparing one request's dates
        // against the next one's.
        public string Start { get; set; } = "";

        // Last day off.
        public string End { get; set; } = "";

        public int Days { get; set; }
        public string? Reason { get; set; }
        public string Status { get; set; } = "";
        public string Filed { get; set; } = "";

        // The day it was filed, and the time on the line under it.
        public string FiledDay { get; set; } = "";
        public string FiledTime { get; set; } = "";

        // Filed by a member of staff from their profile rather than by a driver.
        public bool IsStaff { get; set; }

        // The signed-in person's own request, which somebody else has to decide.
        public bool IsOwn { get; set; }

        // Days of this allowance left once this request is counted, out of the year's
        // entitlement.
        //
        // Read down a driver's requests it is a ledger: each row shows what the
        // allowance stood at after that request, rather than what it stands at now.
        // The live figure was the same on every row of a driver and a type, which said
        // nothing about any of them.
        //
        // A request still waiting counts against itself here. The question in front of
        // whoever is reading is whether to grant it, and the number they want is what
        // granting it would leave. One already refused or withdrawn takes nothing, so
        // its row shows the allowance as that decision left it.
        public int BalanceAfter { get; set; }

        public int EntitlementOfType { get; set; }

        // Days of the same type on other requests still awaiting a decision. Approving
        // this one and forgetting those is how an allowance gets overdrawn.
        public int OtherPendingDays { get; set; }

        // Days of this leave already taken back, counted for the badge.
        public int RevokedCount { get; set; }

        // Days still standing that could be taken back: inside the range, not already
        // revoked, and not in the past.
        //
        // A day off that has been taken cannot be handed back, so yesterday is never
        // offered. An approved leave entirely in the past therefore offers nothing, and
        // no Revoke is drawn for it.
        public List<LeaveDayOption> RevokableDays { get; set; } = new();

        // Set when the driver has asked for this granted leave back.
        public bool WithdrawAsked { get; set; }

        public string? WithdrawReason { get; set; }

        public string? WithdrawAskedWhen { get; set; }

        public string? DecisionNote { get; set; }

        // What happened to this request, oldest first.
        public List<LeaveEventViewModel> History { get; set; } = new();
    }

    // One day of a leave, as the revoke dialog lists it.
    public class LeaveDayOption
    {
        public string Iso { get; set; } = "";
        public string Label { get; set; } = "";
    }

    // One thing that happened to a request, and who did it.
    //
    // Who is named here and not in the driver's app. An operator answering for a decision
    // needs to know whose it was; a driver reading the outcome does not, and naming the
    // person turns a company decision into a personal one.
    public class LeaveEventViewModel
    {
        public string Action { get; set; } = "";

        // When it happened, for ordering. The formatted form is in When.
        //
        // The events are not written in the order they occur: asking to cancel is read
        // off the same row as the decision, and a leave approved at noon and asked about
        // that evening listed the asking first because that is the order the code builds
        // them in. A history out of order is not a history.
        public DateTime At { get; set; }

        public string When { get; set; } = "";
        public string By { get; set; } = "";
        public string? Note { get; set; }
    }
}
