using System.Globalization;

namespace Fravashi.Domain.WorkItems;

/// <summary>Which of the two rule tiers a rule belongs to (plan section 1.8, decision D26).</summary>
public enum RuleTier
{
    /// <summary>A model invariant: binds every stored item whatever wrote it, a provider included.</summary>
    Invariant,

    /// <summary>An authoring rule: binds only a change Fravashi itself authors through the local board.</summary>
    Authoring,
}

/// <summary>
/// The rules of the work item model, enforced here and never by a UI (plan sections 1.1 to 1.4 and 4.3).
/// <para>
/// <b>Two tiers.</b> <see cref="CheckInvariants"/>, <see cref="CheckLinkInvariants"/> and
/// <see cref="ApplyMirroredTransition"/> hold a change to the MODEL INVARIANTS only; they are what a
/// provider-sourced write is held to (item 47's mirroring path). <see cref="ValidateNew"/>,
/// <see cref="ValidateParent"/>, <see cref="ValidateUpdate"/>, <see cref="ApplyTransition"/>,
/// <see cref="ValidateFlags"/>, <see cref="ValidateLink"/> and <see cref="ValidateUnlink"/> are the AUTHORING
/// tier for a change Fravashi makes; each also checks the invariants, and each judges only what the change
/// touches, never the fields it leaves alone. Every refusal carries a code registered in <see cref="Tiers"/>.
/// </para>
/// </summary>
public static class WorkItemRules
{
    /// <summary>The refusal codes, one per rule.</summary>
    public static class Codes
    {
        /// <summary>[I] A level, kind, status, resolution or priority that is not a declared value.</summary>
        public const string ValueUndefined = "value_undefined";

        /// <summary>[I] A blank title.</summary>
        public const string TitleRequired = "title_required";

        /// <summary>[I] A resolution on an open item.</summary>
        public const string ResolutionOnOpenItem = "resolution_on_open_item";

        /// <summary>[I] A parent that is not at a strictly higher level (the item itself included).</summary>
        public const string ParentLevelNotHigher = "parent_level_not_higher";

        /// <summary>[I] A parent in another project.</summary>
        public const string ParentOtherProject = "parent_other_project";

        /// <summary>[I] A named parent that was not supplied, or a supplied parent that is not the named one.</summary>
        public const string ParentNotFound = "parent_not_found";

        /// <summary>[I] A level change that leaves an existing child at the same or a higher level.</summary>
        public const string ChildLevelNotLower = "child_level_not_lower";

        /// <summary>[I] A link from an item to itself.</summary>
        public const string LinkSelf = "link_self";

        /// <summary>[I] A link between items in different projects.</summary>
        public const string LinkOtherProject = "link_other_project";

        /// <summary>[I] A stored name that no enum value has.</summary>
        public const string UnknownStorageName = "unknown_storage_name";

        /// <summary>[A] An empty or whitespace description on a create or on an update that sets it.</summary>
        public const string DescriptionRequired = "description_required";

        /// <summary>[A] An authored parent that is not exactly one level up.</summary>
        public const string ParentLevelNotAdjacent = "parent_level_not_adjacent";

        /// <summary>[A] A level change that leaves an existing child not exactly one level down.</summary>
        public const string ChildLevelNotAdjacent = "child_level_not_adjacent";

        /// <summary>[A] A Spike or a Bug authored above Increment level.</summary>
        public const string KindLevelNotAllowed = "kind_level_not_allowed";

        /// <summary>[A] A timebox on an item that is not a Spike.</summary>
        public const string TimeboxRequiresSpike = "timebox_requires_spike";

        /// <summary>[A] A timebox of zero or less.</summary>
        public const string TimeboxNotPositive = "timebox_not_positive";

        /// <summary>[A] A Blocked flag set without a reason.</summary>
        public const string BlockedReasonRequired = "blocked_reason_required";

        /// <summary>[A] A flag set on a terminal item.</summary>
        public const string FlagOnTerminalItem = "flag_on_terminal_item";

        /// <summary>[A] A resolution other than Completed on an authored Done.</summary>
        public const string DoneResolutionNotCompleted = "done_resolution_not_completed";

        /// <summary>[A] Cancelled without a resolution.</summary>
        public const string CancelledResolutionRequired = "cancelled_resolution_required";

        /// <summary>[A] Cancelled with resolution Completed.</summary>
        public const string CancelledResolutionInvalid = "cancelled_resolution_invalid";

        /// <summary>[A] Resolution Duplicate without a duplicates link, or unlinking the last one.</summary>
        public const string DuplicateLinkRequired = "duplicate_link_required";

        /// <summary>[A] A blocks edge that would close a cycle.</summary>
        public const string BlockedByCycle = "blocked_by_cycle";

        /// <summary>[A] A supersedes edge that would close a cycle.</summary>
        public const string SupersedesCycle = "supersedes_cycle";

        /// <summary>[A] A duplicates edge whose target is itself resolved Duplicate.</summary>
        public const string DuplicateChained = "duplicate_chained";

        /// <summary>[A] A second ref in a role that holds at most one (plan, mockup, prototype, approved design).</summary>
        public const string RefRoleNotRepeatable = "ref_role_not_repeatable";

        /// <summary>[A] A ref with a blank target, which points at nothing.</summary>
        public const string RefTargetRequired = "ref_target_required";

        /// <summary>[A] An ambiguity score outside 0 to 100.</summary>
        public const string AmbiguityOutOfRange = "ambiguity_out_of_range";

        /// <summary>[A] A scope pattern that is blank, rooted, or climbs out of the workspace root.</summary>
        public const string ScopePatternNotRelative = "scope_pattern_not_relative";

        /// <summary>[A] A blank acceptance criterion, readiness line, scenario title or step.</summary>
        public const string AgentLayerTextRequired = "agent_layer_text_required";

        /// <summary>[A] A change budget of zero or fewer files, which no attempt could satisfy.</summary>
        public const string ChangeBudgetNotPositive = "change_budget_not_positive";

        /// <summary>[A] A person blocker with no person or no reason.</summary>
        public const string PersonBlockerIncomplete = "person_blocker_incomplete";

        /// <summary>[A] Marking an item for autopilot (ADR 0031 decision 1, backlog 298) while it is not Ready.</summary>
        public const string AutopilotMarkRequiresReady = "autopilot_mark_requires_ready";
    }

    /// <summary>Every rule code with its tier, as marked in plan sections 1.1 to 1.4 and 4.3.</summary>
    public static IReadOnlyDictionary<string, RuleTier> Tiers { get; } = new Dictionary<string, RuleTier>(StringComparer.Ordinal)
    {
        [Codes.ValueUndefined] = RuleTier.Invariant,
        [Codes.TitleRequired] = RuleTier.Invariant,
        [Codes.ResolutionOnOpenItem] = RuleTier.Invariant,
        [Codes.ParentLevelNotHigher] = RuleTier.Invariant,
        [Codes.ParentOtherProject] = RuleTier.Invariant,
        [Codes.ParentNotFound] = RuleTier.Invariant,
        [Codes.ChildLevelNotLower] = RuleTier.Invariant,
        [Codes.LinkSelf] = RuleTier.Invariant,
        [Codes.LinkOtherProject] = RuleTier.Invariant,
        [Codes.UnknownStorageName] = RuleTier.Invariant,
        [Codes.DescriptionRequired] = RuleTier.Authoring,
        [Codes.ParentLevelNotAdjacent] = RuleTier.Authoring,
        [Codes.ChildLevelNotAdjacent] = RuleTier.Authoring,
        [Codes.KindLevelNotAllowed] = RuleTier.Authoring,
        [Codes.TimeboxRequiresSpike] = RuleTier.Authoring,
        [Codes.TimeboxNotPositive] = RuleTier.Authoring,
        [Codes.BlockedReasonRequired] = RuleTier.Authoring,
        [Codes.FlagOnTerminalItem] = RuleTier.Authoring,
        [Codes.DoneResolutionNotCompleted] = RuleTier.Authoring,
        [Codes.CancelledResolutionRequired] = RuleTier.Authoring,
        [Codes.CancelledResolutionInvalid] = RuleTier.Authoring,
        [Codes.DuplicateLinkRequired] = RuleTier.Authoring,
        [Codes.BlockedByCycle] = RuleTier.Authoring,
        [Codes.SupersedesCycle] = RuleTier.Authoring,
        [Codes.DuplicateChained] = RuleTier.Authoring,
        [Codes.RefRoleNotRepeatable] = RuleTier.Authoring,
        [Codes.RefTargetRequired] = RuleTier.Authoring,
        [Codes.AmbiguityOutOfRange] = RuleTier.Authoring,
        [Codes.ScopePatternNotRelative] = RuleTier.Authoring,
        [Codes.AgentLayerTextRequired] = RuleTier.Authoring,
        [Codes.ChangeBudgetNotPositive] = RuleTier.Authoring,
        [Codes.PersonBlockerIncomplete] = RuleTier.Authoring,
        [Codes.AutopilotMarkRequiresReady] = RuleTier.Authoring,
    };

    /// <summary>The tier of a rule code. An unregistered code is a programming fault and throws.</summary>
    public static RuleTier TierOf(string code) =>
        Tiers.TryGetValue(code, out var tier)
            ? tier
            : throw new ArgumentException($"'{code}' is not a registered work item rule.", nameof(code));

    // ------------------------------------------------------------------------------------------------
    // Invariant tier
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Holds a state to every MODEL INVARIANT and builds the aggregate: the item-local ones of
    /// <see cref="WorkItem.Restore"/>, plus a parent at a strictly higher level in the same project, plus every
    /// supplied child at a strictly lower level. When the state names a parent, that parent must be supplied
    /// (<c>parent_not_found</c> otherwise). This is the whole check a provider-sourced write gets.
    /// </summary>
    public static DomainResult<WorkItem> CheckInvariants(
        WorkItemState state,
        WorkItem? parent = null,
        IEnumerable<WorkItem>? children = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var errors = LocalInvariantErrors(state);
        errors.AddRange(ParentInvariantErrors(state, parent));
        errors.AddRange(ChildInvariantErrors(state.Level, children));
        return Build(state, errors);
    }

    /// <summary>
    /// A status change as a PROVIDER said it: the status as given, the resolution as given on a terminal
    /// status, and the resolution cleared on an open one (plan section 1.4 rule 5, an invariant). Flags are kept
    /// as they are. No authoring rule applies.
    /// </summary>
    public static DomainResult<WorkItem> ApplyMirroredTransition(
        WorkItem item,
        WorkItemStatus to,
        WorkItemResolution? resolution)
    {
        ArgumentNullException.ThrowIfNull(item);
        return WorkItem.Restore(item.ToState() with
        {
            Status = to,
            Resolution = Enum.IsDefined(to) && to.IsOpen() ? null : resolution,
        });
    }

    /// <summary>
    /// The link invariants: no link from an item to itself and no link across projects. Returns the link in
    /// normal form. A cycle, a chained duplicate and the other authoring refusals are not checked here.
    /// </summary>
    public static DomainResult<WorkItemLink> CheckLinkInvariants(WorkItem from, WorkItemLinkType type, WorkItem to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var normalised = WorkItemLink.Normalise(from.Id, type, to.Id);
        if (!normalised.IsValid)
        {
            return normalised;
        }

        if (from.Project != to.Project)
        {
            return DomainResult.Refused<WorkItemLink>(
            [
                new ValidationError(
                    Codes.LinkOtherProject,
                    "link",
                    $"A link cannot cross projects: item {from.Id.Value} is in project {from.Project.Value} and item {to.Id.Value} is in project {to.Project.Value}."),
            ]);
        }

        return normalised;
    }

    // ------------------------------------------------------------------------------------------------
    // Authoring tier
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// A create Fravashi authors. On top of the invariants: a non-blank description, a parent exactly one level
    /// up, a kind authorable at the level, a timebox only on a Spike and positive, a Blocked flag with a reason,
    /// no flag on a terminal item, and the terminal rules of plan section 1.4 for an initial terminal status (a
    /// Done gets resolution Completed; a Cancelled needs WontDo or CannotReproduce, since a new item has no
    /// <c>duplicates</c> link to justify Duplicate).
    /// </summary>
    public static DomainResult<WorkItem> ValidateNew(WorkItemState proposed, WorkItem? parent = null)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        var errors = new List<ValidationError>();
        var state = proposed;

        if (Enum.IsDefined(state.Status) && state.Status.IsTerminal())
        {
            var resolved = TerminalResolution(state.Status, state.Resolution, hasDuplicatesLink: false, errors);
            state = state with { Resolution = resolved };
        }

        var invariantErrors = LocalInvariantErrors(state);
        var parentErrors = ParentInvariantErrors(state, parent);
        errors.InsertRange(0, invariantErrors.Concat(parentErrors));

        errors.AddRange(DescriptionErrors(state.Description));
        if (parentErrors.Count == 0 && Enum.IsDefined(state.Level))
        {
            errors.AddRange(AdjacentParentErrors(state.Level, parent));
        }

        errors.AddRange(KindLevelErrors(state.Kind, state.Level));
        errors.AddRange(TimeboxErrors(state.Kind, state.SpikeTimebox));
        errors.AddRange(FlagErrors(state.Status, WorkItemFlags.None, state.Flags ?? WorkItemFlags.None));
        errors.AddRange(AgentLayerErrors(state));

        return Build(state, errors);
    }

    /// <summary>
    /// The authoring rules of the agent layer (plan increment C1). They are authoring rather than invariant
    /// for the reason decision D12 gives: a provider's data must always be able to land, and no provider
    /// enforces a single mockup, a 0 to 100 score or a relative glob. So a mirrored write carries whatever the
    /// provider said, and a change Fravashi authors is held to these.
    /// </summary>
    public static List<ValidationError> AgentLayerErrors(WorkItemState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var errors = new List<ValidationError>();

        foreach (var role in AgentLayer.ArtifactRef.SingleValueRoles)
        {
            if ((state.Refs ?? []).Count(r => r.Role == role) > 1)
            {
                errors.Add(new ValidationError(
                    Codes.RefRoleNotRepeatable,
                    "refs",
                    $"An item holds at most one {StableNames.ToName(role)} reference: a second makes "
                    + "\"the\" one ambiguous for anything that reads it."));
            }
        }

        if ((state.Refs ?? []).Any(r => string.IsNullOrWhiteSpace(r.Target)))
        {
            errors.Add(new ValidationError(
                Codes.RefTargetRequired, "refs", "A reference needs a target; a blank one points at nothing."));
        }

        if (state.Ambiguity is { } ambiguity && (ambiguity.Score < 0 || ambiguity.Score > 100))
        {
            errors.Add(new ValidationError(
                Codes.AmbiguityOutOfRange,
                "ambiguity",
                "An ambiguity score runs from 0 (clear) to 100 (nobody knows what this means)."));
        }

        foreach (var pattern in state.ScopePatterns ?? [])
        {
            if (!IsRootRelative(pattern.Pattern))
            {
                errors.Add(new ValidationError(
                    Codes.ScopePatternNotRelative,
                    "scopePatterns",
                    "A scope pattern is a glob relative to the workspace root: not blank, not rooted, and "
                    + "never climbing out of it."));
            }
        }

        if (state.ChangeBudget is { MaxFiles: <= 0 })
        {
            errors.Add(new ValidationError(
                Codes.ChangeBudgetNotPositive,
                "changeBudget",
                "A change budget of zero files is a budget no attempt could satisfy; leave it unset instead."));
        }

        if ((state.AcceptanceCriteria ?? []).Any(c => string.IsNullOrWhiteSpace(c.Text))
            || (state.Readiness ?? []).Any(r => string.IsNullOrWhiteSpace(r.Text))
            || (state.Scenarios ?? []).Any(s => string.IsNullOrWhiteSpace(s.Title)
                || s.Steps.Any(step => string.IsNullOrWhiteSpace(step.Action) || string.IsNullOrWhiteSpace(step.Expected))))
        {
            errors.Add(new ValidationError(
                Codes.AgentLayerTextRequired,
                "agentLayer",
                "A criterion, a readiness line, a scenario and a step each say something; a blank one is noise."));
        }

        if ((state.PersonBlockers ?? []).Any(b => string.IsNullOrWhiteSpace(b.Person) || string.IsNullOrWhiteSpace(b.Reason)))
        {
            errors.Add(new ValidationError(
                Codes.PersonBlockerIncomplete,
                "personBlockers",
                "A person blocker names who is being waited on and what for; without both nobody can clear it."));
        }

        return errors;
    }

    /// <summary>
    /// Whether a glob stays inside the workspace root under BOTH readings it will get: the string as written
    /// (no rooted path, no drive, no leading separator) and its segments (no <c>..</c> anywhere). The same
    /// two-reading rule the agent containment work landed on, for the same reason: one reading alone has been
    /// enough to let a path out before.
    /// </summary>
    private static bool IsRootRelative(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var text = pattern.Trim();
        if (text.StartsWith('/') || text.StartsWith('\\') || text.Contains(':'))
        {
            return false;
        }

        return !text
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment == "..");
    }

    /// <summary>
    /// A change of an item's level, its parent, or both (plan section 1.1 rule 4). The item-local invariants are
    /// checked on the proposed state (an undefined level is <c>value_undefined</c>). Refused outright when it
    /// breaks the invariant form (a parent not strictly higher or in another project, a child no longer strictly
    /// lower); refused as authoring when the item's parent is not exactly one level up, and, when the level
    /// changes, when any existing child is not exactly one level down. A parent-only change does not re-judge
    /// the children, whose relation it leaves alone.
    /// </summary>
    public static DomainResult ValidateParent(
        WorkItem item,
        WorkItemLevel level,
        WorkItem? parent,
        IEnumerable<WorkItem> children)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(children);
        var state = item.ToState() with { Level = level, Parent = parent?.Id };
        var errors = LocalInvariantErrors(state);
        errors.AddRange(ParentChangeErrors(item, state, parent, children.ToList()));
        return errors.Count == 0 ? DomainResult.Valid : DomainResult.Refused(Distinct(errors));
    }

    /// <summary>
    /// An update Fravashi authors to any field except the status and the resolution (those are
    /// <see cref="ApplyTransition"/>'s) and the identity. Only what changed is judged: a description is
    /// re-judged only when the update sets it, so a mirrored item that arrived without one can still be edited;
    /// the level and parent through <see cref="ValidateParent"/>'s rules when either changes; the kind against
    /// the level when either changes; the timebox when it or the kind changes; the flags through
    /// <see cref="ValidateFlags"/>'s rules when they change. The invariants are always checked.
    /// </summary>
    public static DomainResult<WorkItem> ValidateUpdate(
        WorkItem current,
        WorkItemState proposed,
        WorkItem? proposedParent,
        IEnumerable<WorkItem> children)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(proposed);
        ArgumentNullException.ThrowIfNull(children);
        if (proposed.Id != current.Id || proposed.Project != current.Project || proposed.Number != current.Number)
        {
            throw new ArgumentException("An update cannot change an item's identity.", nameof(proposed));
        }

        if (proposed.Status != current.Status || proposed.Resolution != current.Resolution)
        {
            throw new ArgumentException("An update cannot change the status or resolution; that is a transition.", nameof(proposed));
        }

        var childList = children.ToList();
        var errors = LocalInvariantErrors(proposed);

        var levelChanged = proposed.Level != current.Level;
        var parentChanged = proposed.Parent != current.Parent;
        if (levelChanged || parentChanged)
        {
            errors.AddRange(ParentChangeErrors(current, proposed, proposedParent, childList));
        }
        else if (proposedParent is not null)
        {
            // The relation is unchanged, so it is only re-checked when the caller supplies the parent.
            errors.AddRange(ParentInvariantErrors(proposed, proposedParent));
        }

        if (!string.Equals(proposed.Description, current.Description, StringComparison.Ordinal))
        {
            errors.AddRange(DescriptionErrors(proposed.Description));
        }

        if (levelChanged || proposed.Kind != current.Kind)
        {
            errors.AddRange(KindLevelErrors(proposed.Kind, proposed.Level));
        }

        if (proposed.Kind != current.Kind || proposed.SpikeTimebox != current.SpikeTimebox)
        {
            errors.AddRange(TimeboxErrors(proposed.Kind, proposed.SpikeTimebox));
        }

        var flags = proposed.Flags ?? WorkItemFlags.None;
        if (flags != current.Flags)
        {
            errors.AddRange(FlagErrors(current.Status, current.Flags, flags));
        }

        if (proposed.MarkedForAutopilot != current.MarkedForAutopilot)
        {
            errors.AddRange(AutopilotMarkErrors(current.Status, proposed.MarkedForAutopilot));
        }

        // The agent layer is judged on the PROPOSED state rather than on what changed: a patch replaces a list
        // whole, so "one criterion moved" is not a thing to detect, and an item that already carried two
        // mockups (mirrored in before these rules existed) should not be able to keep them through an edit.
        errors.AddRange(AgentLayerErrors(proposed));

        return Build(proposed, errors);
    }

    /// <summary>
    /// A status change Fravashi authors (plan section 1.4). Entering Done sets resolution Completed and refuses
    /// any other; entering Cancelled requires WontDo, Duplicate or CannotReproduce, and Duplicate requires a
    /// <c>duplicates</c> link from the item among <paramref name="itemLinks"/>; landing on a terminal status clears
    /// both flags, including a move from one terminal status to another or a change of resolution on one; moving to an open status clears the resolution and refuses one being passed. Any status may
    /// otherwise move to any status (decision D12). A transition to the current status with no new resolution
    /// changes nothing.
    /// </summary>
    public static DomainResult<WorkItem> ApplyTransition(
        WorkItem item,
        WorkItemStatus to,
        WorkItemResolution? resolution,
        IEnumerable<WorkItemLink> itemLinks)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(itemLinks);
        if (!Enum.IsDefined(to))
        {
            return DomainResult.Refused<WorkItem>([Undefined("status", to)]);
        }

        if (to == item.Status && (resolution is null || resolution == item.Resolution))
        {
            return DomainResult.Ok(item);
        }

        var errors = new List<ValidationError>();
        var state = item.ToState() with { Status = to };
        if (to.IsOpen())
        {
            if (resolution is not null)
            {
                errors.Add(ResolutionOnOpen(to, resolution.Value));
            }

            state = state with { Resolution = null };
        }
        else
        {
            var hasDuplicatesLink = itemLinks.Any(l => l.Type == WorkItemLinkType.Duplicates && l.Source == item.Id);
            state = state with
            {
                Resolution = TerminalResolution(to, resolution, hasDuplicatesLink, errors),
                // Rule 4 without qualification: every authored transition that lands on a terminal status
                // clears both flags, a terminal-to-terminal move included (a mirrored flag is dropped there).
                Flags = WorkItemFlags.None,
                // ADR 0031 decision 1: a mark lasts until the item is done (or a person unmarks it), so
                // landing on Done or Cancelled clears it the same way a terminal move clears the flags above.
                MarkedForAutopilot = false,
            };
        }

        if (errors.Count > 0)
        {
            return DomainResult.Refused<WorkItem>(errors);
        }

        return WorkItem.Restore(state);
    }

    /// <summary>
    /// A change to the Blocked or Deferred flag Fravashi authors. Setting Blocked, or changing its reason,
    /// requires a non-blank reason; setting either flag on a terminal item is refused. Clearing a flag is always
    /// allowed, and a flag the change leaves alone is not re-judged (a mirrored Blocked flag with no reason stays
    /// as it is while Deferred is set).
    /// </summary>
    public static DomainResult<WorkItem> ValidateFlags(WorkItem item, WorkItemFlags flags)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(flags);
        var errors = LocalInvariantErrors(item.ToState() with { Flags = flags });
        errors.AddRange(FlagErrors(item.Status, item.Flags, flags));
        return Build(item.ToState() with { Flags = flags }, errors);
    }

    /// <summary>
    /// A link Fravashi authors, stated from either end in any reading. On top of
    /// <see cref="CheckLinkInvariants"/>: a <c>blocks</c> or <c>supersedes</c> edge that would close a cycle
    /// among <paramref name="existingEdges"/> is refused naming the cycle, and a <c>duplicates</c> edge whose
    /// target is itself resolved Duplicate is refused naming the original that target duplicates. An edge that
    /// already exists is valid (linking is idempotent). <paramref name="describe"/> renders an item in a
    /// sentence (the store passes <c>#n</c>); the id is used when it is omitted.
    /// </summary>
    public static DomainResult<WorkItemLink> ValidateLink(
        WorkItem from,
        WorkItemLinkType type,
        WorkItem to,
        IEnumerable<WorkItemLink> existingEdges,
        Func<WorkItemId, string>? describe = null)
    {
        ArgumentNullException.ThrowIfNull(existingEdges);
        var checkedLink = CheckLinkInvariants(from, type, to);
        if (!checkedLink.IsValid)
        {
            return checkedLink;
        }

        describe ??= id => id.Value;
        var link = checkedLink.Value;
        var edges = existingEdges.ToList();
        if (edges.Contains(link))
        {
            return checkedLink;
        }

        switch (link.Type)
        {
            case WorkItemLinkType.Blocks or WorkItemLinkType.Supersedes:
            {
                var cycle = BlockerGraph.FindCycle(edges, link);
                if (cycle is not null)
                {
                    var blocks = link.Type == WorkItemLinkType.Blocks;
                    return DomainResult.Refused<WorkItemLink>(
                    [
                        new ValidationError(
                            blocks ? Codes.BlockedByCycle : Codes.SupersedesCycle,
                            "link",
                            (blocks ? "This blocking link would create a cycle: " : "This supersedes link would create a cycle: ")
                            + string.Join(" -> ", cycle.Select(describe)) + "."),
                    ]);
                }

                break;
            }

            case WorkItemLinkType.Duplicates:
            {
                var target = link.Target == to.Id ? to : from;
                if (target.Resolution == WorkItemResolution.Duplicate)
                {
                    var original = edges.FirstOrDefault(e => e.Type == WorkItemLinkType.Duplicates && e.Source == target.Id)?.Target;
                    var naming = original is null
                        ? "which is itself resolved Duplicate"
                        : $"which is itself a duplicate of {describe(original)}; link to that original instead";
                    return DomainResult.Refused<WorkItemLink>(
                    [
                        new ValidationError(
                            Codes.DuplicateChained,
                            "link",
                            $"A duplicate must point at a live original, not at {describe(target.Id)}, {naming}."),
                    ]);
                }

                break;
            }
        }

        return checkedLink;
    }

    /// <summary>
    /// An unlink Fravashi authors. Removing the last <c>duplicates</c> edge of an item resolved Duplicate is
    /// refused (<c>duplicate_link_required</c>), because the resolution would then point at nothing.
    /// <paramref name="source"/> is the item at the link's source end and <paramref name="sourceLinks"/> its
    /// current edges.
    /// </summary>
    public static DomainResult ValidateUnlink(WorkItem source, WorkItemLink link, IEnumerable<WorkItemLink> sourceLinks)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(sourceLinks);
        if (link.Type != WorkItemLinkType.Duplicates
            || link.Source != source.Id
            || source.Resolution != WorkItemResolution.Duplicate)
        {
            return DomainResult.Valid;
        }

        var remaining = sourceLinks.Count(l => l.Type == WorkItemLinkType.Duplicates && l.Source == source.Id && l != link);
        return remaining > 0
            ? DomainResult.Valid
            : DomainResult.Refused(
            [
                new ValidationError(
                    Codes.DuplicateLinkRequired,
                    "link",
                    "This is the only duplicates link of an item resolved Duplicate; change its resolution first, or link the original before unlinking."),
            ]);
    }

    // ------------------------------------------------------------------------------------------------
    // The pieces
    // ------------------------------------------------------------------------------------------------

    internal static List<ValidationError> LocalInvariantErrors(WorkItemState state)
    {
        var errors = new List<ValidationError>();
        if (!Enum.IsDefined(state.Level))
        {
            errors.Add(Undefined("level", state.Level));
        }

        if (!Enum.IsDefined(state.Kind))
        {
            errors.Add(Undefined("kind", state.Kind));
        }

        var statusDefined = Enum.IsDefined(state.Status);
        if (!statusDefined)
        {
            errors.Add(Undefined("status", state.Status));
        }

        if (state.Resolution is { } resolution && !Enum.IsDefined(resolution))
        {
            errors.Add(Undefined("resolution", resolution));
        }

        if (state.Priority is { } priority && !Enum.IsDefined(priority))
        {
            errors.Add(Undefined("priority", priority));
        }

        // Requested 2026-10-22: the suggestion becomes a CLI flag, so an undefined value must never exist at all.
        if (state.SuggestedModel is { } model && !Enum.IsDefined(model))
        {
            errors.Add(Undefined("suggestedModel", model));
        }

        if (state.SuggestedEffort is { } effort && !Enum.IsDefined(effort))
        {
            errors.Add(Undefined("suggestedEffort", effort));
        }

        if (string.IsNullOrWhiteSpace(state.Title))
        {
            errors.Add(new ValidationError(Codes.TitleRequired, "title", "A work item needs a title."));
        }

        if (statusDefined && state.Status.IsOpen() && state.Resolution is { } openResolution && Enum.IsDefined(openResolution))
        {
            errors.Add(ResolutionOnOpen(state.Status, openResolution));
        }

        if (state.Parent is not null && state.Parent == state.Id)
        {
            errors.Add(new ValidationError(Codes.ParentLevelNotHigher, "parent", "A work item cannot be its own parent."));
        }

        return errors;
    }

    private static List<ValidationError> ParentInvariantErrors(WorkItemState state, WorkItem? parent)
    {
        var errors = new List<ValidationError>();
        if (state.Parent is null || state.Parent == state.Id)
        {
            return errors;
        }

        if (parent is null || parent.Id != state.Parent)
        {
            errors.Add(new ValidationError(
                Codes.ParentNotFound,
                "parent",
                $"The parent {state.Parent.Value} was not found."));
            return errors;
        }

        if (Enum.IsDefined(state.Level) && !parent.Level.IsHigherThan(state.Level))
        {
            errors.Add(new ValidationError(
                Codes.ParentLevelNotHigher,
                "parent",
                $"A {Name(state.Level)} cannot sit under a {Name(parent.Level)}: a parent must be at a higher level."));
        }

        if (parent.Project != state.Project)
        {
            errors.Add(new ValidationError(
                Codes.ParentOtherProject,
                "parent",
                $"The parent is in project {parent.Project.Value}, not {state.Project.Value}; a parent never crosses a project."));
        }

        return errors;
    }

    private static List<ValidationError> ChildInvariantErrors(WorkItemLevel level, IEnumerable<WorkItem>? children)
    {
        var errors = new List<ValidationError>();
        if (children is null || !Enum.IsDefined(level))
        {
            return errors;
        }

        foreach (var child in children)
        {
            if (!level.IsHigherThan(child.Level))
            {
                errors.Add(new ValidationError(
                    Codes.ChildLevelNotLower,
                    "level",
                    $"A {Name(level)} cannot hold its existing child {child.Id.Value}, a {Name(child.Level)}: a child must be at a lower level."));
            }
        }

        return errors;
    }

    private static List<ValidationError> AdjacentParentErrors(WorkItemLevel level, WorkItem? parent)
    {
        var errors = new List<ValidationError>();
        if (parent is null)
        {
            return errors;
        }

        var allowed = level.AllowedParentLevel();
        if (parent.Level != allowed)
        {
            var expected = allowed is null ? "no parent" : $"a {Name(allowed.Value)} parent";
            errors.Add(new ValidationError(
                Codes.ParentLevelNotAdjacent,
                "parent",
                $"A {Name(level)} is placed exactly one level down, so it takes {expected}, not a {Name(parent.Level)}."));
        }

        return errors;
    }

    private static List<ValidationError> ParentChangeErrors(
        WorkItem current,
        WorkItemState proposed,
        WorkItem? parent,
        List<WorkItem> children)
    {
        var errors = ParentInvariantErrors(proposed, parent);
        var childInvariantErrors = ChildInvariantErrors(proposed.Level, children);
        errors.AddRange(childInvariantErrors);
        if (!Enum.IsDefined(proposed.Level))
        {
            return errors;
        }

        if (errors.Count == 0)
        {
            errors.AddRange(AdjacentParentErrors(proposed.Level, parent));
        }

        if (proposed.Level != current.Level && childInvariantErrors.Count == 0)
        {
            foreach (var child in children)
            {
                if (child.Level.AllowedParentLevel() != proposed.Level)
                {
                    errors.Add(new ValidationError(
                        Codes.ChildLevelNotAdjacent,
                        "level",
                        $"Changing this item to a {Name(proposed.Level)} would leave its child {child.Id.Value}, a {Name(child.Level)}, more than one level down."));
                }
            }
        }

        return errors;
    }

    private static List<ValidationError> DescriptionErrors(string? description) =>
        string.IsNullOrWhiteSpace(description)
            ? [new ValidationError(Codes.DescriptionRequired, "description", "A work item needs a description: the problem it solves and the outcome it produces.")]
            : [];

    private static List<ValidationError> KindLevelErrors(WorkItemKind kind, WorkItemLevel level) =>
        Enum.IsDefined(kind) && Enum.IsDefined(level) && !kind.IsAuthorableAt(level)
            ? [new ValidationError(Codes.KindLevelNotAllowed, "kind", $"A {Name(kind)} is Increment-sized or Task-sized; it cannot be a {Name(level)}.")]
            : [];

    private static List<ValidationError> TimeboxErrors(WorkItemKind kind, TimeSpan? timebox)
    {
        var errors = new List<ValidationError>();
        if (timebox is not { } value)
        {
            return errors;
        }

        if (kind != WorkItemKind.Spike)
        {
            errors.Add(new ValidationError(Codes.TimeboxRequiresSpike, "spikeTimebox", "Only a Spike carries a timebox."));
        }

        if (value <= TimeSpan.Zero)
        {
            errors.Add(new ValidationError(
                Codes.TimeboxNotPositive,
                "spikeTimebox",
                string.Create(CultureInfo.InvariantCulture, $"A timebox must be longer than zero, not {value.TotalMinutes} minutes.")));
        }

        return errors;
    }

    private static List<ValidationError> FlagErrors(WorkItemStatus status, WorkItemFlags before, WorkItemFlags after)
    {
        var errors = new List<ValidationError>();
        var blockedChanged = after.Blocked && (!before.Blocked || !string.Equals(before.BlockedReason, after.BlockedReason, StringComparison.Ordinal));
        var deferredChanged = after.Deferred && (!before.Deferred || !string.Equals(before.DeferredReason, after.DeferredReason, StringComparison.Ordinal));

        if (blockedChanged && string.IsNullOrWhiteSpace(after.BlockedReason))
        {
            errors.Add(new ValidationError(Codes.BlockedReasonRequired, "blockedReason", "Say why the item is blocked."));
        }

        if (Enum.IsDefined(status) && status.IsTerminal())
        {
            if (blockedChanged)
            {
                errors.Add(FlagOnTerminal("blocked", status));
            }

            if (deferredChanged)
            {
                errors.Add(FlagOnTerminal("deferred", status));
            }
        }

        return errors;
    }

    /// <summary>
    /// Turning the mark ON requires the item to be Ready right now (ADR 0031 decision 1); turning it OFF is
    /// always allowed, from any status, since unmarking must never be blocked by a rule written for the
    /// other direction.
    /// </summary>
    private static List<ValidationError> AutopilotMarkErrors(WorkItemStatus status, bool marking)
    {
        if (!marking || status == WorkItemStatus.Ready)
        {
            return [];
        }

        return
        [
            new ValidationError(
                Codes.AutopilotMarkRequiresReady,
                "markedForAutopilot",
                "Only a Ready item can be marked for autopilot."),
        ];
    }

    private static WorkItemResolution? TerminalResolution(
        WorkItemStatus to,
        WorkItemResolution? resolution,
        bool hasDuplicatesLink,
        List<ValidationError> errors)
    {
        if (to == WorkItemStatus.Done)
        {
            if (resolution is not (null or WorkItemResolution.Completed))
            {
                errors.Add(new ValidationError(
                    Codes.DoneResolutionNotCompleted,
                    "resolution",
                    $"Done means the work was completed; use Cancelled for {Name(resolution.Value)}."));
            }

            return WorkItemResolution.Completed;
        }

        switch (resolution)
        {
            case null:
                errors.Add(new ValidationError(
                    Codes.CancelledResolutionRequired,
                    "resolution",
                    "Cancelling needs a resolution: wont_do, duplicate or cannot_reproduce."));
                break;
            case WorkItemResolution.Completed:
                errors.Add(new ValidationError(
                    Codes.CancelledResolutionInvalid,
                    "resolution",
                    "A cancelled item was not completed; use wont_do, duplicate or cannot_reproduce, or move it to Done."));
                break;
            case WorkItemResolution.Duplicate when !hasDuplicatesLink:
                errors.Add(new ValidationError(
                    Codes.DuplicateLinkRequired,
                    "resolution",
                    "Link the item this duplicates before resolving it as a duplicate."));
                break;
        }

        return resolution;
    }

    private static DomainResult<WorkItem> Build(WorkItemState state, List<ValidationError> errors)
    {
        if (errors.Count > 0)
        {
            return DomainResult.Refused<WorkItem>(Distinct(errors));
        }

        return WorkItem.Restore(state);
    }

    private static List<ValidationError> Distinct(List<ValidationError> errors) => errors.Distinct().ToList();

    private static ValidationError Undefined<TEnum>(string field, TEnum value)
        where TEnum : struct, Enum =>
        new(Codes.ValueUndefined, field, string.Create(CultureInfo.InvariantCulture, $"{Convert.ToInt64(value, CultureInfo.InvariantCulture)} is not a {typeof(TEnum).Name}."));

    private static ValidationError ResolutionOnOpen(WorkItemStatus status, WorkItemResolution resolution) =>
        new(Codes.ResolutionOnOpenItem, "resolution", $"An open item carries no resolution; {Name(status)} cannot be resolved {Name(resolution)}.");

    private static ValidationError FlagOnTerminal(string flag, WorkItemStatus status) =>
        new(Codes.FlagOnTerminalItem, flag, $"A {Name(status)} item cannot be flagged {flag}; flags decorate open items only.");

    private static string Name<TEnum>(TEnum value)
        where TEnum : struct, Enum => StableNames.ToName(value);
}
