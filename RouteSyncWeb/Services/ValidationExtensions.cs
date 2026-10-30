using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace FleetWise.Services
{
    public static class ValidationExtensions
    {
        // First validation failure, as a plain sentence for the existing BadRequest(string)
        // responses the dispatch modals already display.
        //
        // Needed because these controllers are plain MVC, not [ApiController]: MVC binds the
        // body and fills ModelState, then runs the action regardless. Without an explicit
        // check the attributes are decoration. (Adding [ApiController] would automate this,
        // but it also rewrites every response shape in the controller, which these views
        // parse by hand.)
        public static string FirstError(this ModelStateDictionary modelState) =>
            modelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => e.ErrorMessage)
                .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m))
            ?? "Some of those details are not valid.";
    }
}
