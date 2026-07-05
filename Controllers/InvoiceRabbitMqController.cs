using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StarlinkDeviceManager.Models;
using StarlinkDeviceManager.Services;

namespace StarlinkDeviceManager.Controllers;

[Authorize]
public class InvoiceRabbitMqController(
    IInvoiceRabbitMqPublisher publisher,
    ISqlAuthService authService,
    ILogger<InvoiceRabbitMqController> logger) : Controller
{
    private const string IndexViewPath = "~/Views/InvoiceRabbitMq/Index.cshtml";

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        if (!await IsSystemAdminAsync())
        {
            return Forbid();
        }

        return View(IndexViewPath, BuildModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Publish(InvoiceRabbitMqTestViewModel model)
    {
        if (!await IsSystemAdminAsync())
        {
            return Forbid();
        }

        model.ConfigSummary = publisher.GetConfigurationSummary();

        if (!ModelState.IsValid)
        {
            model.Message = "Please enter invoice JSON before publishing.";
            return View(IndexViewPath, model);
        }

        var (userId, username) = GetCurrentAuditContext();
        logger.LogInformation("User {Username} requested RabbitMQ invoice test publish.", username);

        var result = await publisher.PublishInvoiceAsync(new InvoiceRabbitMqPublishRequest
        {
            InvoiceJson = model.InvoiceJson,
            RoutingKeyOverride = model.RoutingKeyOverride,
            UserId = userId,
            Username = username
        }, HttpContext.RequestAborted);

        model.Published = result.Success;
        model.Message = result.Message;
        model.Logs = result.Logs;

        return View(IndexViewPath, model);
    }

    private InvoiceRabbitMqTestViewModel BuildModel()
    {
        return new InvoiceRabbitMqTestViewModel
        {
            ConfigSummary = publisher.GetConfigurationSummary()
        };
    }

    private async Task<bool> IsSystemAdminAsync()
    {
        var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdValue, out var userId))
        {
            return false;
        }

        var currentUser = await authService.GetUserByIdAsync(userId, HttpContext.RequestAborted);
        return currentUser is not null &&
            !currentUser.IsViewOnly &&
            !currentUser.IsTenantUser &&
            !currentUser.IsShipAdmin &&
            !currentUser.IsCrew;
    }

    private (int? UserId, string Username) GetCurrentAuditContext()
    {
        var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        int? userId = int.TryParse(userIdValue, out var parsedUserId) ? parsedUserId : null;
        var username = User.Identity?.Name;

        return (userId, string.IsNullOrWhiteSpace(username) ? "system" : username);
    }
}
