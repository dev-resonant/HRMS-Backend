using HRMS.API.Authorization;
using HRMS.Application.Interfaces;
using HRMS.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HRMS.Tests.Platform;

// Test-only controllers that exercise the platform gate. They live in the test assembly and are added to the host only by tests that use
// ProbeHost.AddProbeController; the production API has no platform endpoints yet. Several of them contain the mistake they are named for.
// ASP0026 is the compiler analyzer for exactly the AllowAnonymous mistake; it is reproduced on purpose.
#pragma warning disable ASP0026

/// <summary>The correct shape: a platform controller with the gate at class level and a permission per action.</summary>
[ApiController]
[Route("api/platform/probe")]
[PlatformOnly]
public class PlatformProbeController : ControllerBase
{
    private readonly ICurrentUser _currentUser;

    public PlatformProbeController(ICurrentUser currentUser) => _currentUser = currentUser;

    [HttpGet("manage")]
    [RequirePermission(PermissionNames.Roles.Module, PermissionNames.Roles.ManagePermissions)]
    public IActionResult Manage() => Ok(new { _currentUser.IsPlatform, _currentUser.CompanyId });

    [HttpGet("view")]
    [RequirePermission(PermissionNames.Roles.Module, PermissionNames.Roles.View)]
    public IActionResult View() => Ok(new { _currentUser.IsPlatform });

    /// <summary>MISTAKE: action-level [AllowAnonymous] under a [PlatformOnly] controller. Must be denied for everyone.</summary>
    [HttpGet("anonymous")]
    [AllowAnonymous]
    public IActionResult Anonymous() => Ok("reached");

    /// <summary>MISTAKE: [SelfService] under a [PlatformOnly] controller (it would skip the permission). Must be denied for everyone.</summary>
    [HttpGet("self-service")]
    [SelfService]
    public IActionResult SelfService() => Ok("reached");
}

/// <summary>MISTAKE: [PlatformOnly] without any permission. The gate never grants on its own, so this is denied for everyone.</summary>
[ApiController]
[Route("api/platform/probe-nopermission")]
[PlatformOnly]
public class PlatformNoPermissionProbeController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok("reached");
}

/// <summary>MISTAKE: an action in the reserved platform area that forgot [PlatformOnly]. The runtime backstop must refuse it.</summary>
[ApiController]
[Route("api/platform/probe-forgotten")]
public class PlatformForgottenAttributeProbeController : ControllerBase
{
    [HttpGet]
    [RequirePermission(PermissionNames.Roles.Module, PermissionNames.Roles.View)]
    public IActionResult Get() => Ok("reached");
}

/// <summary>[PlatformOnly] at ACTION level on an ordinary route, next to an ordinary action of the same controller.</summary>
[ApiController]
[Route("api/probe-platform-action")]
public class PlatformActionLevelProbeController : ControllerBase
{
    [HttpGet("gated")]
    [PlatformOnly]
    [RequirePermission(PermissionNames.Roles.Module, PermissionNames.Roles.ManagePermissions)]
    public IActionResult Gated() => Ok("reached");

    [HttpGet("ordinary")]
    [RequirePermission(PermissionNames.Roles.Module, PermissionNames.Roles.View)]
    public IActionResult Ordinary() => Ok("reached");
}

#pragma warning restore ASP0026
