using FleetWise.Models;

namespace FleetWise.Services
{
    /// <summary>
    /// What happened to a leave request, in the order it happened.
    /// </summary>
    /// <remarks>
    /// Read off the row rather than kept in a table of its own. A request is filed once,
    /// decided once, asked about and answered at most once, and revoked at most once as a
    /// whole, and each of those moments has columns of its own. None of them overwrites
    /// another, so granted leave that was later cancelled or taken back still reads as
    /// granted first.
    /// </remarks>
    public static class LeaveHistory
    {
        private const string Stamp = "MMM d, yyyy h:mm tt";

        /// <param name="names">Display names by user id, operators as well as drivers.</param>
        public static List<LeaveEventViewModel> Of(LeaveRequest r, IReadOnlyDictionary<int, string> names)
        {
            string Who(int? id) =>
                id is int i && names.TryGetValue(i, out var n) ? n : "Unknown";

            // Left blank rather than guessed where nobody was recorded, so an answer given
            // before its answerer was kept names nobody instead of the wrong person.
            string WhoIfKnown(int? id) => id is null ? "" : Who(id);

            var events = new List<LeaveEventViewModel>
            {
                Event("Filed", r.FiledAt, Who(r.UserId), r.Reason),
            };

            var accepted = LeaveEntitlement.AskAccepted(r);
            var acceptanceInDecision = LeaveEntitlement.DecisionIsAcceptance(r);

            if (r.DecidedAt is DateTime decided
                && !string.Equals(r.Status, "Pending", StringComparison.OrdinalIgnoreCase)
                && !acceptanceInDecision)
            {
                // What was decided, which is not always what the request says now. Revoking
                // and accepting a cancellation both leave the approval in the decision fields
                // and change only the status, so either way this was the approval; named by
                // the status, it would read as a second Revoked or Cancelled.
                var action = accepted || string.Equals(r.Status, "Revoked", StringComparison.OrdinalIgnoreCase)
                    ? "Approved"
                    : r.Status;

                // A withdrawal is the driver's own doing and carries no decider.
                events.Add(Event(action, decided,
                    r.DecidedBy is null ? Who(r.UserId) : Who(r.DecidedBy), r.DecisionNote));
            }

            if (r.WithdrawRequestedAt is DateTime asked)
            {
                events.Add(Event("Cancellation asked for", asked, Who(r.UserId), r.WithdrawReason));

                if (r.WithdrawAnsweredAt is DateTime answered)
                {
                    if (string.Equals(r.Status, "Approved", StringComparison.OrdinalIgnoreCase))
                    {
                        events.Add(Event("Cancellation declined", answered,
                            WhoIfKnown(r.WithdrawAnsweredBy), r.WithdrawAnswerNote));
                    }
                    else if (acceptanceInDecision)
                    {
                        events.Add(Event("Cancellation accepted", r.DecidedAt!.Value,
                            Who(r.DecidedBy), r.DecisionNote));
                    }
                    else if (accepted)
                    {
                        events.Add(Event("Cancellation accepted", answered,
                            WhoIfKnown(r.WithdrawAnsweredBy), r.WithdrawAnswerNote));
                    }
                }
            }

            // After the decision, not instead of it. Leave that was granted and then taken
            // back has two things that happened to it, and a history showing only the
            // second reads as though it was never granted.
            if (r.RevokedAt is DateTime revoked)
            {
                var which = r.RevokedDates is { Count: > 0 }
                            && !string.Equals(r.Status, "Revoked", StringComparison.OrdinalIgnoreCase)
                    ? $"{r.RevokedDates.Count} {(r.RevokedDates.Count == 1 ? "day" : "days")} taken back"
                    : "All days taken back";

                events.Add(Event("Revoked", revoked, Who(r.RevokedBy),
                    string.IsNullOrWhiteSpace(r.RevokeNote) ? which : $"{which}. {r.RevokeNote}"));
            }

            // In the order they happened, not the order they were assembled. OrderBy is
            // stable, so two stamped the same second keep the order they were added in.
            return events.OrderBy(e => e.At).ToList();
        }

        /// <summary>
        /// The note a request's status stands on: why it was cancelled for a cancellation
        /// that was accepted, and the decision's own note for everything else.
        /// </summary>
        public static string? StatusNote(LeaveRequest r) =>
            LeaveEntitlement.AskAccepted(r) && !LeaveEntitlement.DecisionIsAcceptance(r)
                ? r.WithdrawAnswerNote
                : r.DecisionNote;

        private static LeaveEventViewModel Event(string action, DateTime at, string by, string? note) => new()
        {
            Action = action,
            At = at,
            When = at.ToString(Stamp),
            By = by,
            Note = note,
        };
    }
}
