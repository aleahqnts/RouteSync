namespace FleetWise.Models
{
    /// <summary>One leave allowance as the profile shows it: left of the year's, and pending.</summary>
    public sealed record ProfileLeaveBalance(string Type, int Left, int OfYear, int Pending);

    /// <summary>The signed-in person's own page.</summary>
    public class ProfilePageViewModel
    {
        public string FullName { get; set; } = "";
        public string Initials { get; set; } = "";
        public string FirstName { get; set; } = "";
        public string MiddleName { get; set; } = "";
        public string LastName { get; set; } = "";
        public string Email { get; set; } = "";
        public string Role { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTime MemberSince { get; set; }
        public DateTime? LastSignIn { get; set; }

        public string ContactNumber { get; set; } = "";
        public string Address { get; set; } = "";
        public string EmergencyContactName { get; set; } = "";
        public string EmergencyContactNumber { get; set; } = "";

        /// <summary>Entries in the audit trail this month, or null when it could not be read.</summary>
        public int? ActionsThisMonth { get; set; }

        /// <summary>Leave decisions made this month, or null when the trail could not be read.</summary>
        public int? DecisionsThisMonth { get; set; }

        /// <summary>Approved leave days taken this year, across every type.</summary>
        public int LeaveDaysTaken { get; set; }

        /// <summary>The latest entries in the audit trail made by this person.</summary>
        public List<AuditEntryViewModel> Recent { get; set; } = new();

        public int Year { get; set; }
        public List<ProfileLeaveBalance> Balances { get; set; } = new();

        /// <summary>Every request filed, newest first.</summary>
        public List<LeaveRequest> Requests { get; set; } = new();

        /// <summary>The operational day, against which leave is judged.</summary>
        public DateTime Today { get; set; }

        /// <summary>The earliest day any leave may be filed for, sick and emergency only.</summary>
        public DateTime Earliest { get; set; }
    }
}
