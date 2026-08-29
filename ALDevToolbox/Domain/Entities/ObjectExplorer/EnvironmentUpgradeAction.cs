namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// One thing the upgrade team did — or asked to have done later — to a customer's
/// Business Central environment: move its platform update out to the latest date
/// Microsoft allows, or start that update straight away. These rows <em>are</em> the
/// per-environment activity feed; there is no second table behind it, and no
/// <c>AuditInterceptor</c> entry for them either (this table is itself a log). See
/// <c>.design/saas-delivery.md</c> and issue #657.
///
/// <para>Two shapes share the row. An action the person asked for <em>immediately</em>
/// is performed on the request thread and lands here already
/// <see cref="UpgradeActionStatus.Sent"/> or <see cref="UpgradeActionStatus.Failed"/> —
/// there is nothing to cancel, because it already happened. An action scheduled for an
/// agreed slot ("tonight at 20:00") lands <see cref="UpgradeActionStatus.Pending"/> with
/// <see cref="ExecuteAfter"/> set, and <c>UpgradeActionWorker</c> fires it when it comes
/// due. Cancel works right up until the worker claims the row.</para>
/// </summary>
public class EnvironmentUpgradeAction
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>The customer. Denormalised from the environment so the worker resolves credentials without a join, and so the feed can name the customer.</summary>
    public int ProjectId { get; set; }
    public Project? Project { get; set; }

    /// <summary>The environment this acts on. The feed is keyed by it.</summary>
    public int EnvironmentId { get; set; }
    public ProjectEnvironment? Environment { get; set; }

    /// <summary>Which of the two moves this is.</summary>
    public UpgradeActionKind Kind { get; set; }

    /// <summary>Where the action has got to. See <see cref="UpgradeActionStatus"/>.</summary>
    public UpgradeActionStatus Status { get; set; } = UpgradeActionStatus.Pending;

    /// <summary>Who asked for it. Nullable (<c>ON DELETE SET NULL</c>) so the feed outlives the account.</summary>
    public int? RequestedByUserId { get; set; }
    public User? RequestedByUser { get; set; }

    /// <summary>
    /// The requester in the audit log's <c>"display name &lt;email&gt;"</c> form, copied
    /// in at request time. Denormalised on purpose: the feed still says who did this
    /// after the account is gone or renamed, which is the whole point of a history.
    /// </summary>
    public string RequestedBy { get; set; } = string.Empty;

    /// <summary>When it was asked for (UTC).</summary>
    public DateTime RequestedAt { get; set; }

    /// <summary>
    /// The UTC instant the action is due to fire — the slot the person picked, converted
    /// from the customer's own time zone. Equal to <see cref="RequestedAt"/> for an
    /// action performed immediately, so the feed can order and read both kinds alike.
    /// </summary>
    public DateTime ExecuteAfter { get; set; }

    /// <summary>When the change actually reached Business Central (or failed trying). Null while pending or cancelled.</summary>
    public DateTime? SentAt { get; set; }

    /// <summary>
    /// What happened, in the words the feed shows: the success detail, or the plain-words
    /// reason it did not work (a vanished update, a blocked environment, credentials the
    /// customer has since rotated). Never raw exception text.
    /// </summary>
    public string? Outcome { get; set; }

    public int? CancelledByUserId { get; set; }
    public User? CancelledByUser { get; set; }

    /// <summary>Who cancelled it, in the same denormalised form as <see cref="RequestedBy"/>.</summary>
    public string? CancelledBy { get; set; }

    public DateTime? CancelledAt { get; set; }

    // Concurrency: no version column, deliberately. The one race on this table is the
    // worker claiming a row while somebody cancels it, and both sides settle it with the
    // same conditional compare-and-set DeliveryService.RunDeliveryAsync uses — an
    // UPDATE ... WHERE status = 'Pending' AND sent_at IS NULL, where whoever the database
    // reports one affected row to has won. That is one statement, no retry loop, and it
    // beats a token for this shape: with xmin the loser would have to re-read and decide
    // what the new state means, which is exactly the question the WHERE clause answers.
    // Nothing else here is read-modify-write, so nothing else needs guarding.
}

/// <summary>
/// The two moves the upgrade team makes on a platform update's date. Stored as text
/// (<c>HasConversion&lt;string&gt;()</c>) like <see cref="ProjectVisibility"/>, so the
/// column reads plainly and a third kind never renumbers the existing rows.
/// </summary>
public enum UpgradeActionKind
{
    /// <summary>Move the update's date out to the latest Business Central still allows.</summary>
    PushDateToLatest,

    /// <summary>Start the update as soon as Business Central will take it, ignoring the environment's update window.</summary>
    RunNow,
}

/// <summary>
/// Where an upgrade action has got to: <c>Pending → Sent | Failed | Cancelled</c>. An
/// immediate action skips <c>Pending</c> and is written in its terminal state. Stored as
/// text, like <see cref="UpgradeActionKind"/>.
/// </summary>
public enum UpgradeActionStatus
{
    /// <summary>Scheduled for a future slot and not yet claimed by the worker. The only cancellable state.</summary>
    Pending,

    /// <summary>The change reached Business Central.</summary>
    Sent,

    /// <summary>Business Central refused it, or it could no longer be done. <see cref="EnvironmentUpgradeAction.Outcome"/> says why.</summary>
    Failed,

    /// <summary>Cancelled before it fired.</summary>
    Cancelled,
}
