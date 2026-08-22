using HRMS.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace HRMS.API.Controllers
{

    [ApiController]
    [Route("api/tenant-test")]
    public class TenantTestController : ControllerBase
    {
        private readonly ITenantContext _tenantContext;

        public TenantTestController(ITenantContext tenantContext)
        {
            _tenantContext = tenantContext;
        }

        [HttpGet]
        public IActionResult Get()
        {
            return Ok(new
            {
                isResolved = _tenantContext.IsResolved,
                companyId = _tenantContext.CompanyId,
                domain = _tenantContext.Domain
            });
        }

    }
}
