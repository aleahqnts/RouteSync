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

        /// <summary>The dates covered, written the way they are read.</summary>
        public string Span { get; set; } = "";

        public int Days { get; set; }
        public string? Reason { get; set; }
        public string Status { get; set; } = "";
        public string Filed { get; set; } = "";

        /// <summary>
        /// Days between filing and the first day off. Stated, not judged: BGC asks for
        /// three and does not enforce it, so this is context for the person deciding.
        /// </summary>
        public int NoticeDays { get; set; }

        /// <summary>What the driver has left of this allowance, this request included.</summary>
        public int RemainingOfType { get; set; }

        public int EntitlementOfType { get; set; }

        public string? DecisionNote { get; set; }
    }
}
