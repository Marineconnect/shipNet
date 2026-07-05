using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using StarlinkDeviceManager.Services;

namespace StarlinkDeviceManager.Filters;

public class ViewOnlyGuardFilter(
    ITempDataDictionaryFactory tempDataDictionaryFactory,
    ISqlAuthService authService) : IAsyncActionFilter
{
    private const string ViewOnlyClaimType = "IsViewOnly";
    private const string ViewOnlyMessage = "Tài khoản chỉ theo dõi chỉ được xem dữ liệu, không được thêm mới, chỉnh sửa, xóa hoặc tạo QR.";

    private static readonly HashSet<string> MutatingActionPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Create",
        "Edit",
        "Update",
        "Delete",
        "Save",
        "Reset",
        "Import",
        "Change",
        "LoginAsUser",
        "RefreshExpiredDevice",
        "RebootDeviceRouter"
    };

    private static readonly HashSet<string> MutatingPaymentActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "NinePayQrInfo",
        "NinePayBankTransferInfo",
        "NinePaySubscriptionQr"
    };

    private static readonly HashSet<string> SafeAuthenticatedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Logout"
    };

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (IsPublicEndpoint(context) || !IsBlockedAction(context) || !await IsViewOnlyUserAsync(context))
        {
            await next();
            return;
        }

        if (IsJsonRequest(context.HttpContext.Request))
        {
            context.Result = new ObjectResult(new
            {
                success = false,
                message = ViewOnlyMessage
            })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return;
        }

        var tempData = tempDataDictionaryFactory.GetTempData(context.HttpContext);
        tempData["ViewOnlyDenied"] = ViewOnlyMessage;

        var referer = context.HttpContext.Request.Headers.Referer.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(referer) &&
            Uri.TryCreate(referer, UriKind.Absolute, out var refererUri) &&
            string.Equals(refererUri.Host, context.HttpContext.Request.Host.Host, StringComparison.OrdinalIgnoreCase))
        {
            context.Result = new RedirectResult(referer);
            return;
        }

        context.Result = new RedirectToActionResult("Index", "Dashboard", null);
    }

    private async Task<bool> IsViewOnlyUserAsync(ActionExecutingContext context)
    {
        if (context.HttpContext.User.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (context.HttpContext.User.HasClaim(ViewOnlyClaimType, "true"))
        {
            return true;
        }

        var userIdValue = context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdValue, out var userId))
        {
            return false;
        }

        var user = await authService.GetUserByIdAsync(userId, context.HttpContext.RequestAborted);
        return user?.IsViewOnly == true;
    }

    private static bool IsBlockedAction(ActionExecutingContext context)
    {
        if (IsSafeAuthenticatedAction(context))
        {
            return false;
        }

        var actionName = context.ActionDescriptor.RouteValues.TryGetValue("action", out var action)
            ? action ?? string.Empty
            : string.Empty;

        if (IsUnsafeMethod(context.HttpContext.Request.Method))
        {
            return true;
        }

        if (MutatingPaymentActions.Contains(actionName))
        {
            return true;
        }

        return MutatingActionPrefixes.Any(prefix => actionName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSafeAuthenticatedAction(ActionExecutingContext context)
    {
        var actionName = context.ActionDescriptor.RouteValues.TryGetValue("action", out var action)
            ? action ?? string.Empty
            : string.Empty;

        return SafeAuthenticatedActions.Contains(actionName);
    }

    private static bool IsPublicEndpoint(ActionExecutingContext context)
    {
        return context.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any() &&
            !context.ActionDescriptor.EndpointMetadata.OfType<IAuthorizeData>().Any();
    }

    private static bool IsUnsafeMethod(string method)
    {
        return HttpMethods.IsPost(method) ||
            HttpMethods.IsPut(method) ||
            HttpMethods.IsPatch(method) ||
            HttpMethods.IsDelete(method);
    }

    private static bool IsJsonRequest(HttpRequest request)
    {
        return request.Headers.Accept.Any(value => value?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true) ||
            request.Headers.XRequestedWith == "XMLHttpRequest" ||
            request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true;
    }
}
