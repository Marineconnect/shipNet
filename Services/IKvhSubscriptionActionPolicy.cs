using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public interface IKvhSubscriptionActionPolicy
{
    KvhSubscriptionActionDecision EvaluatePause(KvhSubscriptionActionContext context);
    KvhSubscriptionActionDecision EvaluateResume(KvhSubscriptionActionContext context);
}

public sealed class KvhSubscriptionActionPolicy : IKvhSubscriptionActionPolicy
{
    public KvhSubscriptionActionDecision EvaluatePause(KvhSubscriptionActionContext context)
    {
        if (string.IsNullOrWhiteSpace(context.TrafficId)) return Deny("missing_traffic_id", "Missing Traffic ID.");
        if (string.IsNullOrWhiteSpace(context.Region)) return Deny("missing_region", "Missing Region.");
        if (!context.KvhSubscriptionId.HasValue) return Deny("missing_subscription", "Subscription unknown.");
        if (context.HasPendingCommand) return Deny("pending_command", "A KVH command is already pending.");
        if (context.CooldownUntilUtc.HasValue && context.CooldownUntilUtc.Value > DateTime.UtcNow) return Deny("cooldown", "Command cooldown is active.", context.CooldownUntilUtc);

        var status = Normalize(context.Status);
        var scheduled = KvhJsonHelpers.NormalizeScheduledAction(context.ScheduledAction);
        if (scheduled == "SUSPEND") return Deny("scheduled_suspend", "Khong the Pause vi subscription dang cho SUSPEND co hieu luc.");
        if (scheduled == "RESUME") return Deny("scheduled_resume", "Subscription dang co yeu cau Resume cho xu ly.");
        if (IsSuspended(status)) return Deny("already_suspended", "Subscription da o trang thai SUSPEND.");
        if (status != "ACTIVE") return Deny("invalid_state", "Pause chi hop le khi subscription ACTIVE.");

        return Allow();
    }

    public KvhSubscriptionActionDecision EvaluateResume(KvhSubscriptionActionContext context)
    {
        if (string.IsNullOrWhiteSpace(context.TrafficId)) return Deny("missing_traffic_id", "Missing Traffic ID.");
        if (string.IsNullOrWhiteSpace(context.Region)) return Deny("missing_region", "Missing Region.");
        if (!context.KvhSubscriptionId.HasValue) return Deny("missing_subscription", "Subscription unknown.");
        if (context.HasPendingCommand) return Deny("pending_command", "A KVH command is already pending.");
        if (context.CooldownUntilUtc.HasValue && context.CooldownUntilUtc.Value > DateTime.UtcNow) return Deny("cooldown", "Command cooldown is active.", context.CooldownUntilUtc);

        var status = Normalize(context.Status);
        var scheduled = KvhJsonHelpers.NormalizeScheduledAction(context.ScheduledAction);
        if (scheduled == "RESUME") return Deny("scheduled_resume", "Subscription dang cho Resume co hieu luc.");
        if (scheduled == "SUSPEND") return Deny("scheduled_suspend", "Subscription dang co yeu cau Suspend cho xu ly.");
        if (status == "ACTIVE") return Deny("already_active", "Subscription da ACTIVE.");
        if (!IsSuspended(status)) return Deny("invalid_state", "Resume chi hop le khi subscription SUSPEND/PAUSED.");

        return Allow();
    }

    private static KvhSubscriptionActionDecision Allow() => new() { Allowed = true };
    private static KvhSubscriptionActionDecision Deny(string reasonCode, string message, DateTime? nextAllowedAtUtc = null) => new() { Allowed = false, ReasonCode = reasonCode, Message = message, NextAllowedAtUtc = nextAllowedAtUtc };
    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant().Replace(" ", "_");
    private static bool IsSuspended(string status) => status.Contains("SUSPEND", StringComparison.OrdinalIgnoreCase) || status.Contains("PAUSE", StringComparison.OrdinalIgnoreCase);
}
