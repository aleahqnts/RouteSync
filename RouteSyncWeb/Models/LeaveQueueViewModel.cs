namespace FleetWise.Models
{
    /// <summary>The leave queue as the Requests page draws it.</summary>
    public class LeaveQueueViewModel
    {
        /// <summary>Which status is being shown. "All" shows every one.</summary>
        public string Status { get; set; } = "Pending";

        /// <summary>Waiting on a decision, counted whatever is being shown.</summary>
        public int PendingCount { get; set; }

        public List<LeaveRowViewModel> Rows { get; set; } = new();
    }

    /// <summary>One request, with the context needed to decide it.</summary>
    public class LeaveRowViewModel
    {
        public long RequestId { get; set; }
        public int DriverId { get; set; }
        public string DriverName { get; set; } = "";
        public string LeaveType { get; set; } = "";

        /// <summary>
        /// The dates covered as one phrase, for the places that name a request in a
        /// sentence: the decision dialog, the history panel, the audit trail.
        /// </summary>
        public string Span { get; set; } = "";

        /// <summary>First day off, in a column of its own.</summary>
        /// <remarks>
        /// The queue is read down the columns. A span written as one string reads well in
        /// a sentence and badly in a table, where the eye is comparing one request's dates
        /// against the next one's.
        /// </remarks>
        public string Start { get; set; } = "";

        /// <summary>Last day off.</summary>
        public string End { get; set; } = "";

        public int Days { get; set; }
        public string? Reason { get; set; }
        public string Status { get; set; } = "";
        public string Filed { get; set; } = "";

        /// <summary>
        /// Days of this allowance left once this request is counted, out of the year's
        /// entitlement.
        /// </summary>
        /// <remarks>
        /// Read down a driver's requests it is a ledger: each row shows what the
        /// allowance stood at after that request, rather than what it stands at now.
        /// The live figure was the same on every row of a driver and a type, which said
        /// nothing about any of them.
        ///
        /// A request still waiting counts against itself here. The question in front of
        /// whoever is reading is whether to grant it, and the number they want is what
        /// granting it would leave. One already refused or withdrawn takes nothing, so
        /// its row shows the allowance as that decision left it.
        /// </remarks>
        public int BalanceAfter { get; set; }

        public int EntitlementOfType { get; set; }

        /// <summary>
        /// Days of the same type on other requests still awaiting a decision. Approving
        /// this one and forgetting those is how an allowance gets overdrawn.
        /// </summary>
        public int OtherPendingDays { get; set; }

        /// <summary>Days of this leave already taken back, counted for the badge.</summary>
        public int RevokedCount { get; set; }

        /// <summary>
        /// Days still standing that could be taken back: inside the range, not already
        /// revoked, and not in the past.
        /// </summary>
        /// <remarks>
        /// A day off that has been taken cannot be handed back, so yesterday is never
        /// offered. An approved leave entirely in the past therefore offers nothing, and
        /// no Revoke is drawn for it.
        /// </remarks>
        public List<LeaveDayOption> RevokableDays { get; set; } = new();

        /// <summary>Set when the driver has asked for this granted leave back.</summary>
        public bool WithdrawAsked { get; set; }

        public string? WithdrawReason { get; set; }

        public string? WithdrawAskedWhen { get; set; }

        public string? DecisionNote { get; set; }

        /// <summary>What happened to this request, oldest first.</summary>
        public List<LeaveEventViewModel> History { get; set; } = new();
    }

    /// <summary>One thing that happened to a request, and who did it.</summary>
    /// <remarks>
    /// Who is named here and not in the driver's app. An operator answering for a decision
    /// needs to know whose it was; a driver reading the outcome does not, and naming the
    /// person turns a company decision into a personal one.
    /// </remarks>
    /// <summary>One day of a leave, as the revoke dialog lists it.</summary>
    public class LeaveDayOption
    {
        public string Iso { get; set; } = "";
        public string Label { get; set; } = "";
    }

    public class LeaveEventViewModel
    {
        public string Action { get; set; } = "";
        public string When { get; set; } = "";
        public string By { get; set; } = "";
        public string? Note { get; set; }
    }
}
