using System.ComponentModel.DataAnnotations;

namespace FleetWise.Models
{
    public class EditUserViewModel
    {
        [Required]
        public int UserId { get; set; }

        [Required(ErrorMessage = "Enter a first name."), StringLength(50, ErrorMessage = "First name can be up to 50 characters.")]
        public string FirstName { get; set; } = string.Empty;

        [StringLength(50, ErrorMessage = "Middle name can be up to 50 characters.")]
        public string? MiddleName { get; set; }

        [Required(ErrorMessage = "Enter a last name."), StringLength(50, ErrorMessage = "Last name can be up to 50 characters.")]
        public string LastName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Enter an email address."), EmailAddress(ErrorMessage = "Enter a valid email address.")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Select a role."), Range(1, int.MaxValue, ErrorMessage = "Select a role.")]
        public int RoleId { get; set; }

        [Required, RegularExpression("^(Activated|Deactivated)$", ErrorMessage = "Choose Activated or Deactivated.")]
        public string AccountStatus { get; set; } = "Activated";
    }
}
