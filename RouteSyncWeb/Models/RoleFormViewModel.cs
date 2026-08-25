using System.ComponentModel.DataAnnotations;

namespace FleetWise.Models
{
    public class RoleFormViewModel
    {
        // null = Add Role (CreateRole), non-null = Edit Role (UpdateRole)
        public int? RoleId { get; set; }

        [Required, StringLength(50)]
        public string RoleName { get; set; } = string.Empty;

        /// <summary>
        /// Legacy column. The permission switches decide what a role may do; this is
        /// no longer filled in on the form and keeps whatever the row already held.
        /// </summary>
        public string AccessLevel { get; set; } = string.Empty;

        // Bound from hidden+checkbox pairs: WebPermissions[Dashboard], WebPermissions[FleetMap], ...
        public Dictionary<string, bool> WebPermissions { get; set; } = new();

        // Bound from MobilePermissions[FullAccess]
        public Dictionary<string, bool> MobilePermissions { get; set; } = new();
    }
}
