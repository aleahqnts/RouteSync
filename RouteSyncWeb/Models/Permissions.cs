namespace FleetWise.Models
{
    /// <summary>
    /// Every permission a role can hold, named once.
    /// </summary>
    /// <remarks>
    /// This list was written out in three places: the modal that draws the switches, the
    /// script that fills them in when the role changes, and the controller that saves
    /// them. A permission added to one and missed in another failed quietly and in a way
    /// that read as something else. Missed in the script, one switch appeared to be on for
    /// every role, because it was never written when the role changed. Missed in the
    /// controller, saving reported success and dropped the value on the way past, so the
    /// switch came back off.
    ///
    /// The modal renders from here and the controller saves against it, so a permission
    /// exists once. The script reads the switches the modal rendered, so it follows on its
    /// own.
    /// </remarks>
    public static class Permissions
    {
        /// <summary>Dashboard permissions, in the order the modal lists them.</summary>
        public static readonly (string Key, string Label)[] Web =
        {
            ("dashboard", "Dashboard"),
            ("routes",    "Routes"),
            ("vehicles",  "Vehicles"),
            ("reports",   "Reports"),
            ("users",     "Users"),
            ("requests",  "Requests"),
            ("audit",     "Audit Log"),
        };

        /// <summary>Driver app permissions.</summary>
        public static readonly (string Key, string Label)[] Mobile =
        {
            ("tracking",  "Tracking"),
            ("messages",  "Messages"),
            ("checklist", "Checklist"),
        };

        public static string[] WebKeys => Web.Select(p => p.Key).ToArray();

        public static string[] MobileKeys => Mobile.Select(p => p.Key).ToArray();
    }
}
