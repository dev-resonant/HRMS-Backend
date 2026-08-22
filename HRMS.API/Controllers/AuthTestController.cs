using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HRMS.API.Controllers
{
    [ApiController]
    [Route("api/auth-test")]
    [Authorize]
    public class AuthTestController : ControllerBase
    {
        [HttpGet("me")]
        public IActionResult Me()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            var email = User.FindFirstValue(ClaimTypes.Email);

            var companyId = User.FindFirstValue("companyId");

            var role = User.FindFirstValue(ClaimTypes.Role);

            return Ok(new
            {
                userId,
                email,
                companyId,
                role
            });
        }
    }
}
