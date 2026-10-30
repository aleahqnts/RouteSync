namespace FleetWise.Models
{
    public class UserListItemViewModel
    {
        public int UserId { get; set; }

        // Formatted as "Last, First M."
        public string FullName { get; set; } = string.Empty;

        public string Email { get; set; } = string.Empty;

        public string RoleName { get; set; } = string.Empty;

        public string AccountStatus { get; set; } = string.Empty;

        public string FirstName { get; set; } = string.Empty;

        public string? MiddleName { get; set; }

        public string LastName { get; set; } = string.Empty;

        public int RoleId { get; set; }
    }
}
