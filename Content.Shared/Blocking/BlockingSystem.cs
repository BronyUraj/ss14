﻿using System.Linq;
using Content.Shared.Actions;
using Content.Shared.Actions.ActionTypes;
using Content.Shared.Doors.Components;
using Content.Shared.Hands;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.IdentityManagement;
using Content.Shared.Interaction.Events;
using Content.Shared.Maps;
using Content.Shared.Mobs.Components;
using Content.Shared.Physics;
using Content.Shared.Popups;
using Content.Shared.Toggleable;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Shared.Blocking;

public sealed partial class BlockingSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly SharedActionsSystem _actionsSystem = default!;
    [Dependency] private readonly SharedTransformSystem _transformSystem = default!;
    [Dependency] private readonly FixtureSystem _fixtureSystem = default!;
    [Dependency] private readonly SharedHandsSystem _handsSystem = default!;
    [Dependency] private readonly SharedPopupSystem _popupSystem = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;

    public override void Initialize()
    {
        base.Initialize();
        InitializeUser();

        SubscribeLocalEvent<BlockingComponent, GotEquippedHandEvent>(OnEquip);
        SubscribeLocalEvent<BlockingComponent, GotUnequippedHandEvent>(OnUnequip);
        SubscribeLocalEvent<BlockingComponent, DroppedEvent>(OnDrop);

        SubscribeLocalEvent<BlockingComponent, GetItemActionsEvent>(OnGetActions);
        SubscribeLocalEvent<BlockingComponent, ToggleActionEvent>(OnToggleAction);

        SubscribeLocalEvent<BlockingComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnEquip(EntityUid uid, BlockingComponent component, GotEquippedHandEvent args)
    {
        component.User = args.User;

        //To make sure that this bodytype doesn't get set as anything but the original
        if (TryComp<PhysicsComponent>(args.User, out var physicsComponent) && physicsComponent.BodyType != BodyType.Static && !HasComp<BlockingUserComponent>(args.User))
        {
            var userComp = EnsureComp<BlockingUserComponent>(args.User);
            userComp.BlockingItem = uid;
            userComp.OriginalBodyType = physicsComponent.BodyType;
        }
    }

    private void OnUnequip(EntityUid uid, BlockingComponent component, GotUnequippedHandEvent args)
    {
        StopBlockingHelper(uid, component, args.User);
    }

    private void OnDrop(EntityUid uid, BlockingComponent component, DroppedEvent args)
    {
        StopBlockingHelper(uid, component, args.User);
    }

    private void OnGetActions(EntityUid uid, BlockingComponent component, GetItemActionsEvent args)
    {
        if (component.BlockingToggleAction == null && _proto.TryIndex(component.BlockingToggleActionId, out InstantActionPrototype? act))
            component.BlockingToggleAction = new(act);

        if (component.BlockingToggleAction != null)
            args.Actions.Add(component.BlockingToggleAction);
    }

    private void OnToggleAction(EntityUid uid, BlockingComponent component, ToggleActionEvent args)
    {
        if(args.Handled)
            return;

        var blockQuery = GetEntityQuery<BlockingComponent>();
        var handQuery = GetEntityQuery<SharedHandsComponent>();

        if (!handQuery.TryGetComponent(args.Performer, out var hands))
            return;

        var shields = _handsSystem.EnumerateHeld(args.Performer, hands).ToArray();

        foreach (var shield in shields)
        {
            if (shield == uid)
                continue;

            if (blockQuery.TryGetComponent(shield, out var otherBlockComp) && otherBlockComp.IsBlocking)
            {
                CantBlockError(args.Performer);
                return;
            }
        }

        if (component.IsBlocking)
            StopBlocking(uid, component, args.Performer);
        else
            StartBlocking(uid, component, args.Performer);

        args.Handled = true;
    }

    private void OnShutdown(EntityUid uid, BlockingComponent component, ComponentShutdown args)
    {
        //In theory the user should not be null when this fires off
        if (component.User != null)
        {
            _actionsSystem.RemoveProvidedActions(component.User.Value, uid);
            StopBlockingHelper(uid, component, component.User.Value);
        }
    }

    /// <summary>
    /// Called where you want the user to start blocking
    /// Creates a new hard fixture to bodyblock
    /// Also makes the user static to prevent prediction issues
    /// </summary>
    /// <param name="item"> The entity with the blocking component</param>
    /// <param name="component"> The <see cref="BlockingComponent"/></param>
    /// <param name="user"> The entity who's using the item to block</param>
    /// <returns></returns>
    public bool StartBlocking(EntityUid item, BlockingComponent component, EntityUid user)
    {
        if (component.IsBlocking)
            return false;

        var xform = Transform(user);

        var shieldName = Name(item);

        var blockerName = Identity.Entity(user, EntityManager);
        var msgUser = Loc.GetString("action-popup-blocking-user", ("shield", shieldName));
        var msgOther = Loc.GetString("action-popup-blocking-other", ("blockerName", blockerName), ("shield", shieldName));

        if (component.BlockingToggleAction != null)
        {
            //Don't allow someone to block if they're not parented to a grid
            if (xform.GridUid != xform.ParentUid)
            {
                CantBlockError(user);
                return false;
            }

            //Don't allow someone to block if someone else is on the same tile or if they're inside of a doorway
            var playerTileRef = xform.Coordinates.GetTileRef();
            if (playerTileRef != null)
            {
                var intersecting = _lookup.GetEntitiesIntersecting(playerTileRef.Value);
                var mobQuery = GetEntityQuery<MobStateComponent>();
                var doorQuery = GetEntityQuery<DoorComponent>();
                var xformQuery = GetEntityQuery<TransformComponent>();

                foreach (var uid in intersecting)
                {
                    if (uid != user && mobQuery.HasComponent(uid) || xformQuery.GetComponent(uid).Anchored && doorQuery.HasComponent(uid))
                    {
                        TooCloseError(user);
                        return false;
                    }
                }
            }

            //Don't allow someone to block if they're somehow not anchored.
            _transformSystem.AnchorEntity(xform);
            if (!xform.Anchored)
            {
                CantBlockError(user);
                return false;
            }
            _actionsSystem.SetToggled(component.BlockingToggleAction, true);
            _popupSystem.PopupEntity(msgUser, user, user);
            _popupSystem.PopupEntity(msgOther, user, Filter.PvsExcept(user), true);
        }

        if (TryComp<PhysicsComponent>(user, out var physicsComponent))
        {
            var fixture = new Fixture(physicsComponent, component.Shape)
            {
                ID = BlockingComponent.BlockFixtureID,
                Hard = true,
                CollisionLayer = (int) CollisionGroup.WallLayer
            };

            _fixtureSystem.TryCreateFixture(physicsComponent, fixture);
        }

        component.IsBlocking = true;

        return true;
    }

    private void CantBlockError(EntityUid user)
    {
        var msgError = Loc.GetString("action-popup-blocking-user-cant-block");
        _popupSystem.PopupEntity(msgError, user, user);
    }

    private void TooCloseError(EntityUid user)
    {
        var msgError = Loc.GetString("action-popup-blocking-user-too-close");
        _popupSystem.PopupEntity(msgError, user, user);
    }

    /// <summary>
    /// Called where you want the user to stop blocking.
    /// </summary>
    /// <param name="item"> The entity with the blocking component</param>
    /// <param name="component"> The <see cref="BlockingComponent"/></param>
    /// <param name="user"> The entity who's using the item to block</param>
    /// <returns></returns>
    public bool StopBlocking(EntityUid item, BlockingComponent component, EntityUid user)
    {
        if (!component.IsBlocking)
            return false;

        var xform = Transform(user);

        var shieldName = Name(item);

        var blockerName = Identity.Entity(user, EntityManager);
        var msgUser = Loc.GetString("action-popup-blocking-disabling-user", ("shield", shieldName));
        var msgOther = Loc.GetString("action-popup-blocking-disabling-other", ("blockerName", blockerName), ("shield", shieldName));

        //If the component blocking toggle isn't null, grab the users SharedBlockingUserComponent and PhysicsComponent
        //then toggle the action to false, unanchor the user, remove the hard fixture
        //and set the users bodytype back to their original type
        if (component.BlockingToggleAction != null && TryComp<BlockingUserComponent>(user, out var blockingUserComponent)
                                                     && TryComp<PhysicsComponent>(user, out var physicsComponent))
        {
            if (xform.Anchored)
                _transformSystem.Unanchor(xform);

            _actionsSystem.SetToggled(component.BlockingToggleAction, false);
            _fixtureSystem.DestroyFixture(physicsComponent, BlockingComponent.BlockFixtureID);
            _physics.SetBodyType(physicsComponent, blockingUserComponent.OriginalBodyType);
            _popupSystem.PopupEntity(msgUser, user, user);
            _popupSystem.PopupEntity(msgOther, user, Filter.PvsExcept(user), true);
        }

        component.IsBlocking = false;

        return true;
    }

    /// <summary>
    /// Called where you want someone to stop blocking and to remove the <see cref="BlockingUserComponent"/> from them
    /// Won't remove the <see cref="BlockingUserComponent"/> if they're holding another blocking item
    /// </summary>
    /// <param name="uid"> The item the component is attached to</param>
    /// <param name="component"> The <see cref="BlockingComponent"/> </param>
    /// <param name="user"> The person holding the blocking item </param>
    private void StopBlockingHelper(EntityUid uid, BlockingComponent component, EntityUid user)
    {
        if (component.IsBlocking)
            StopBlocking(uid, component, user);

        var userQuery = GetEntityQuery<BlockingUserComponent>();
        var handQuery = GetEntityQuery<SharedHandsComponent>();

        if (!handQuery.TryGetComponent(user, out var hands))
            return;

        var shields = _handsSystem.EnumerateHeld(user, hands).ToArray();

        foreach (var shield in shields)
        {
            if (HasComp<BlockingComponent>(shield) && userQuery.TryGetComponent(user, out var blockingUserComponent))
            {
                blockingUserComponent.BlockingItem = shield;
                return;
            }
        }

        RemComp<BlockingUserComponent>(user);
        component.User = null;
    }

}
