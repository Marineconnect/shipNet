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
        if (context.CooldownUntilUtc.HasValue && context.CooldownUntilUtc.Value > DateTime.UtcNow) return Deny("cooldown", "Command cooldown is active.", nextAllowedAtUtc: context.CooldownUntilUtc);

        var status = Normalize(context.Status);
        var scheduled = KvhJsonHelpers.NormalizeScheduledAction(context.ScheduledAction);
        if (scheduled == "SUSPEND" && (!string.IsNullOrWhiteSpace(context.ScheduleId) || context.ScheduledEffectiveDateUtc.HasValue))
        {
            var effective = ShipNetTimeZone.FormatVietnam(context.ScheduledEffectiveDateUtc, includeSuffix: true);
            return Deny(
                "kvh_pause_already_scheduled",
                $"Subscription da co yeu cau Pause truoc do va dang cho SUSPEND co hieu luc vao {effective}.",
                $"A previous Pause request already exists and is waiting to become effective on {effective}.",
                scheduledEffectiveDateUtc: context.ScheduledEffectiveDateUtc);
        }

        if (scheduled == "RESUME") return Deny("kvh_conflicting_resume_schedule", "Subscription dang co yeu cau Resume cho xu ly.", "The subscription has a pending Resume schedule.");
        if (IsSuspended(status)) return Deny("kvh_subscription_already_suspended", "Subscription da o trang thai SUSPEND.", "The subscription is already suspended.");
        if (status != "ACTIVE") return Deny("invalid_state", "Pause chi hop le khi subscription ACTIVE.");

        return Allow();
    }

    public KvhSubscriptionActionDecision EvaluateResume(KvhSubscriptionActionContext context)
    {
        if (string.IsNullOrWhiteSpace(context.TrafficId)) return Deny("missing_traffic_id", "Missing Traffic ID.");
        if (string.IsNullOrWhiteSpace(context.Region)) return Deny("missing_region", "Missing Region.");
        if (!context.KvhSubscriptionId.HasValue) return Deny("missing_subscription", "Subscription unknown.");
        if (context.HasPendingCommand) return Deny("pending_command", "A KVH command is already pending.");
        if (context.CooldownUntilUtc.HasValue && context.CooldownUntilUtc.Value > DateTime.UtcNow) return Deny("cooldown", "Command cooldown is active.", nextAllowedAtUtc: context.CooldownUntilUtc);

        var status = Normalize(context.Status);
        var scheduled = KvhJsonHelpers.NormalizeScheduledAction(context.ScheduledAction);
        if (scheduled == "RESUME") return Deny("scheduled_resume", "Subscription dang cho Resume co hieu luc.");
        if (scheduled == "SUSPEND") return Deny("scheduled_suspend", "Subscription dang co yeu cau Suspend cho xu ly.");
        if (status == "ACTIVE") return Deny("already_active", "Subscription da ACTIVE.");
        if (!IsSuspended(status)) return Deny("invalid_state", "Resume chi hop le khi subscription SUSPEND/PAUSED.");

        return Allow();
    }

    private static KvhSubscriptionActionDecision Allow() => new() { Allowed = true };
    private static KvhSubscriptionActionDecision Deny(string reasonCode, string message, string? messageEn = null, DateTime? nextAllowedAtUtc = null, DateTime? scheduledEffectiveDateUtc = null) => new()
    {
        Allowed = false,
        ReasonCode = reasonCode,
        Message = message,
        MessageEn = messageEn ?? message,
        NextAllowedAtUtc = nextAllowedAtUtc,
        ScheduledEffectiveDateUtc = scheduledEffectiveDateUtc
    };
    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant().Replace(" ", "_");
    private static bool IsSuspended(string status) => status.Contains("SUSPEND", StringComparison.OrdinalIgnoreCase) || status.Contains("PAUSE", StringComparison.OrdinalIgnoreCase);
}
