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
        /// Days of this allowance already granted this year, out of the entitlement.
        /// Approved days only: the request being decided is not counted against itself.
        /// </summary>
        public int RemainingOfType { get; set; }

        public int EntitlementOfType { get; set; }

        /// <summary>
        /// Days of the same type on other requests still awaiting a decision. Approving
        /// this one and forgetting those is how an allowance gets overdrawn.
        /// </summary>
        public int OtherPendingDays { get; set; }

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
    public class LeaveEventViewModel
    {
        public string Action { get; set; } = "";
        public string When { get; set; } = "";
        public string By { get; set; } = "";
        public string? Note { get; set; }
    }
}
