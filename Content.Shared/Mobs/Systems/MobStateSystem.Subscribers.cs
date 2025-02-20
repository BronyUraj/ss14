﻿using Content.Shared.Bed.Sleep;
using Content.Shared.Disease.Events;
using Content.Shared.DragDrop;
using Content.Shared.Emoting;
using Content.Shared.Interaction.Events;
using Content.Shared.Inventory.Events;
using Content.Shared.Item;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Events;
using Content.Shared.Pulling.Events;
using Content.Shared.Speech;
using Content.Shared.Standing;
using Content.Shared.Strip.Components;
using Content.Shared.Throwing;
using Robust.Shared.Physics.Components;

namespace Content.Shared.Mobs.Systems;

public partial class MobStateSystem
{
    //General purpose event subscriptions. If you can avoid it register these events inside their own systems
    private void SubscribeEvents()
    {
        SubscribeLocalEvent<MobStateComponent, BeforeGettingStrippedEvent>(OnGettingStripped);
        SubscribeLocalEvent<MobStateComponent, ChangeDirectionAttemptEvent>(CheckAct);
        SubscribeLocalEvent<MobStateComponent, UseAttemptEvent>(CheckAct);
        SubscribeLocalEvent<MobStateComponent, AttackAttemptEvent>(CheckAct);
        SubscribeLocalEvent<MobStateComponent, InteractionAttemptEvent>(CheckAct);
        SubscribeLocalEvent<MobStateComponent, ThrowAttemptEvent>(CheckAct);
        SubscribeLocalEvent<MobStateComponent, SpeakAttemptEvent>(CheckAct);
        SubscribeLocalEvent<MobStateComponent, IsEquippingAttemptEvent>(OnEquipAttempt);
        SubscribeLocalEvent<MobStateComponent, EmoteAttemptEvent>(CheckAct);
        SubscribeLocalEvent<MobStateComponent, IsUnequippingAttemptEvent>(OnUnequipAttempt);
        SubscribeLocalEvent<MobStateComponent, DropAttemptEvent>(CheckAct);
        SubscribeLocalEvent<MobStateComponent, PickupAttemptEvent>(CheckAct);
        SubscribeLocalEvent<MobStateComponent, StartPullAttemptEvent>(CheckAct);
        SubscribeLocalEvent<MobStateComponent, UpdateCanMoveEvent>(CheckAct);
        SubscribeLocalEvent<MobStateComponent, StandAttemptEvent>(CheckAct);
        SubscribeLocalEvent<MobStateComponent, TryingToSleepEvent>(OnSleepAttempt);
        SubscribeLocalEvent<MobStateComponent, AttemptSneezeCoughEvent>(OnSneezeAttempt);
    }

    private void OnStateExitSubscribers(EntityUid target, MobStateComponent component, MobState state)
    {
        var uid = component.Owner;
        switch (state)
        {
            case MobState.Alive:
                //unused
                break;
            case MobState.Critical:
                _standing.Stand(uid);
                break;
            case MobState.Dead:
                RemComp<CollisionWakeComponent>(uid);
                _standing.Stand(uid);
                if (!_standing.IsDown(uid) && TryComp<PhysicsComponent>(uid, out var physics))
                {
                    _physics.SetCanCollide(physics, true);
                }

                break;
            case MobState.Invalid:
                //unused
                break;
            default:
                throw new NotImplementedException();
        }
    }

    private void OnStateEnteredSubscribers(EntityUid target, MobStateComponent component, MobState state)
    {
        var uid = component.Owner;
        _blocker.UpdateCanMove(uid); //update movement anytime a state changes
        switch (state)
        {
            case MobState.Alive:
                _standing.Stand(uid);
                _appearance.SetData(uid, MobStateVisuals.State, MobState.Alive);
                break;
            case MobState.Critical:
                _standing.Down(uid);
                _appearance.SetData(uid, MobStateVisuals.State, MobState.Critical);
                break;
            case MobState.Dead:
                EnsureComp<CollisionWakeComponent>(uid);
                _standing.Down(uid);

                if (_standing.IsDown(uid) && TryComp<PhysicsComponent>(uid, out var physics))
                {
                    _physics.SetCanCollide(physics, false);
                }

                _appearance.SetData(uid, MobStateVisuals.State, MobState.Dead);
                break;
            case MobState.Invalid:
                //unused;
                break;
            default:
                throw new NotImplementedException();
        }
    }

    #region Event Subscribers

    private void OnSleepAttempt(EntityUid target, MobStateComponent component, ref TryingToSleepEvent args)
    {
        if (IsDead(target, component))
            args.Cancelled = true;
    }

    private void OnSneezeAttempt(EntityUid target, MobStateComponent component, ref AttemptSneezeCoughEvent args)
    {
        if (IsDead(target, component))
            args.Cancelled = true;
    }

    private void OnGettingStripped(EntityUid target, MobStateComponent component, BeforeGettingStrippedEvent args)
    {
        // Incapacitated or dead targets get stripped two or three times as fast. Makes stripping corpses less tedious.
        if (IsDead(target, component))
            args.Multiplier /= 3;
        else if (IsCritical(target, component))
            args.Multiplier /= 2;
    }

    private void CheckAct(EntityUid target, MobStateComponent component, CancellableEntityEventArgs args)
    {
        switch (component.CurrentState)
        {
            case MobState.Dead:
            case MobState.Critical:
                args.Cancel();
                break;
        }
    }

    private void OnEquipAttempt(EntityUid target, MobStateComponent component, IsEquippingAttemptEvent args)
    {
        // is this a self-equip, or are they being stripped?
        if (args.Equipee == target)
            CheckAct(target, component, args);
    }

    private void OnUnequipAttempt(EntityUid target, MobStateComponent component, IsUnequippingAttemptEvent args)
    {
        // is this a self-equip, or are they being stripped?
        if (args.Unequipee == target)
            CheckAct(target, component, args);
    }

    #endregion
}
