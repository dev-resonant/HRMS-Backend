using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HRMS.API.Controllers
{
    [ApiController]
    [Route("healthz")]
    public class HealthController : Controller
    {
        [HttpGet]
        [AllowAnonymous]

        public IActionResult Get()
        {
            return Ok(new
            {
                status = "healthy"
            });
        }
    }
}
