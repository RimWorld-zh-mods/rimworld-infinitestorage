﻿using Harmony;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace InfiniteStorage
{
    [StaticConstructorOnStartup]
    partial class HarmonyPatches
    {
        static HarmonyPatches()
        {
            var harmony = HarmonyInstance.Create("com.InfiniteStorage.rimworld.mod");
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            Log.Message("InfiniteStorage: Adding Harmony Postfix to Pawn_TraderTracker.DrawMedOperationsTab");
            Log.Message("InfiniteStorage: Adding Harmony Postfix to Pawn_TraderTracker.ThingsInGroup");
            Log.Message("InfiniteStorage: Adding Harmony Postfix to Pawn_TraderTracker.ColonyThingsWillingToBuy");
            Log.Message("InfiniteStorage: Adding Harmony Postfix to TradeShip.ColonyThingsWillingToBuy");
            Log.Message("InfiniteStorage: Adding Harmony Postfix to Window.PreClose");
            Log.Message("InfiniteStorage: Adding Harmony Postfix to WorkGiver_DoBill.TryFindBestBillIngredients");
            Log.Message("InfiniteStorage: Adding Harmony Postfix to ReservationManager.CanReserve");
            Log.Message("InfiniteStorage: Adding Harmony Prefix to Designator_Build.ProcessInput - will block if looking for things.");
        }
    }

    [HarmonyPatch(typeof(HealthCardUtility), "DrawMedOperationsTab")]
    static class Patch_HealthCardUtility_DrawMedOperationsTab
    {
        [HarmonyPriority(Priority.First)]
        static void Prefix(Pawn pawn)
        {
            if (pawn == null)
                return;

            if (Patch_ListerThings_ThingsInGroup.AvailableMedicalThing != null)
            {
                Patch_ListerThings_ThingsInGroup.AvailableMedicalThing.Clear();
            }

            Patch_ListerThings_ThingsInGroup.AvailableMedicalThing = new List<Thing>();
            foreach (Building_InfiniteStorage storage in WorldComp.GetInfiniteStorages(pawn.Map))
            {
                if (storage.IsOperational && storage.Map == pawn.Map)
                {
                    foreach (Thing t in storage.StoredThings)
                    {
                        if (t.def.IsDrug || t.def.isBodyPartOrImplant)
                        {
                            Patch_ListerThings_ThingsInGroup.AvailableMedicalThing.AddRange(storage.StoredThings);
                        }
                    }
                }
            }
        }

        static void Postfix()
        {
            if (Patch_ListerThings_ThingsInGroup.AvailableMedicalThing != null)
            {
                Patch_ListerThings_ThingsInGroup.AvailableMedicalThing.Clear();
                Patch_ListerThings_ThingsInGroup.AvailableMedicalThing = null;
            }
        }
    }

    [HarmonyPatch(typeof(ListerThings), "ThingsInGroup")]
    static class Patch_ListerThings_ThingsInGroup
    {
        public static List<Thing> AvailableMedicalThing = null;
        static void Postfix(ref List<Thing> __result, ThingRequestGroup group)
        {
            if (AvailableMedicalThing != null)
            {
                __result.AddRange(AvailableMedicalThing);
            }
        }
    }

    #region Used by the left hand tally
    [HarmonyPatch(typeof(ResourceCounter), "UpdateResourceCounts")]
    static class Patch_ResourceCounter_UpdateResourceCounts
    {
        static FieldInfo countedAmountsFI = null;
        static void Postfix(ResourceCounter __instance)
        {
            if (countedAmountsFI == null)
            {
                countedAmountsFI = typeof(ResourceCounter).GetField("countedAmounts", BindingFlags.Instance | BindingFlags.NonPublic);
            }

            Dictionary<ThingDef, int> countedAmounts = (Dictionary<ThingDef, int>)countedAmountsFI.GetValue(__instance);

            foreach (Building_InfiniteStorage ts in WorldComp.GetInfiniteStorages(Find.VisibleMap))
            {
                foreach (Thing thing in ts.StoredThings)
                {
                    if (thing.def.EverStoreable && thing.def.CountAsResource && !thing.IsNotFresh())
                    {
                        int count;
                        if (countedAmounts.TryGetValue(thing.def, out count))
                        {
                            count += thing.stackCount;
                        }
                        else
                        {
                            count = thing.stackCount;
                        }
                        countedAmounts[thing.def] = count;
                    }
                }
            }
        }
    }
    #endregion

    #region Used for creating other buildings
    [HarmonyPatch(typeof(Designator_Build), "ProcessInput")]
    static class Patch_Designator_Build_ProcessInput
    {
        private static FieldInfo entDefFI = null;
        private static FieldInfo stuffDefFI = null;
        private static FieldInfo writeStuffFI = null;
        static bool Prefix(Designator_Build __instance, Event ev)
        {
            if (entDefFI == null)
            {
                entDefFI = typeof(Designator_Build).GetField("entDef", BindingFlags.NonPublic | BindingFlags.Instance);
                stuffDefFI = typeof(Designator_Build).GetField("stuffDef", BindingFlags.NonPublic | BindingFlags.Instance);
                writeStuffFI = typeof(Designator_Build).GetField("writeStuff", BindingFlags.NonPublic | BindingFlags.Instance);
            }

            Map map = Find.VisibleMap;

            ThingDef thingDef = entDefFI.GetValue(__instance) as ThingDef;
            if (thingDef == null || !thingDef.MadeFromStuff || !WorldComp.HasInfiniteStorages(map))
            {
                return true;
            }

            List<FloatMenuOption> list = new List<FloatMenuOption>();

            foreach (ThingDef current in map.resourceCounter.AllCountedAmounts.Keys)
            {
                if (current.IsStuff && current.stuffProps.CanMake(thingDef) && (DebugSettings.godMode || map.listerThings.ThingsOfDef(current).Count > 0))
                {
                    ThingDef localStuffDef = current;
                    string labelCap = localStuffDef.LabelCap;
                    list.Add(new FloatMenuOption(labelCap, delegate
                    {
                        __instance.ProcessInput(ev);
                        Find.DesignatorManager.Select(__instance);
                        stuffDefFI.SetValue(__instance, current);
                        writeStuffFI.SetValue(__instance, true);
                    }, MenuOptionPriority.Default, null, null, 0f, null, null));
                }
            }

            foreach (Building_InfiniteStorage storage in WorldComp.GetInfiniteStorages(map))
            {
                if (storage.Spawned)
                {
                    foreach (Thing t in storage.StoredThings)
                    {
                        ThingDef current = t.def;
                        if (current.IsStuff &&
                            current.stuffProps.CanMake(thingDef) &&
                            (DebugSettings.godMode || t.stackCount > 0))
                        {
                            string labelCap = current.LabelCap;
                            list.Add(new FloatMenuOption(labelCap, delegate
                            {
                                __instance.ProcessInput(ev);
                                Find.DesignatorManager.Select(__instance);
                                stuffDefFI.SetValue(__instance, current);
                                writeStuffFI.SetValue(__instance, true);
                            }, MenuOptionPriority.Default, null, null, 0f, null, null));
                        }
                    }
                }
            }

            if (list.Count == 0)
            {
                Messages.Message("NoStuffsToBuildWith".Translate(), MessageTypeDefOf.RejectInput);
            }
            else
            {
                FloatMenu floatMenu = new FloatMenu(list);
                floatMenu.vanishIfMouseDistant = true;
                Find.WindowStack.Add(floatMenu);
                Find.DesignatorManager.Select(__instance);
            }
            return false;
        }
    }
    #endregion

    [HarmonyPatch(typeof(WorkGiver_Refuel), "FindBestFuel")]
    static class Patch_WorkGiver_Refuel_FindBestFuel
    {
        private static Dictionary<Thing, Building_InfiniteStorage> droppedAndStorage = null;
        static void Prefix(Pawn pawn, Thing refuelable)
        {
            if (WorldComp.HasInfiniteStorages(refuelable.Map))
            {
                droppedAndStorage = new Dictionary<Thing, Building_InfiniteStorage>();

                ThingFilter filter = refuelable.TryGetComp<CompRefuelable>().Props.fuelFilter;

                foreach (Building_InfiniteStorage storage in WorldComp.GetInfiniteStorages(refuelable.Map))
                {
                    if (storage.Spawned && storage.Map == pawn.Map && storage.IsOperational)
                    {
                        Thing t;
                        if (storage.TryRemove(filter, out t))
                        {
                            List<Thing> removedThings = new List<Thing>();
                            BuildingUtil.DropThing(t, t.def.stackLimit, storage, storage.Map, false, removedThings);
                            if (removedThings.Count > 0)
                                droppedAndStorage.Add(removedThings[0], storage);
                        }
                    }
                }
            }
        }

        static void Postfix(Thing __result)
        {
            if (droppedAndStorage != null)
            {
                foreach (KeyValuePair<Thing, Building_InfiniteStorage> kv in droppedAndStorage)
                {
                    if (kv.Key != __result)
                    {
                        kv.Value.Add(kv.Key);
                    }
                }
                droppedAndStorage.Clear();
            }
        }
    }

    [HarmonyPatch(typeof(ItemAvailability), "ThingsAvailableAnywhere")]
    static class Patch_ItemAvailability_ThingsAvailableAnywhere
    {
        private static FieldInfo cachedResultsFI = null;
        private static FieldInfo CachedResultsFI
        {
            get
            {
                if (cachedResultsFI == null)
                {
                    cachedResultsFI = typeof(ItemAvailability).GetField("cachedResults", BindingFlags.NonPublic | BindingFlags.Instance);
                }
                return cachedResultsFI;
            }
        }

        static void Postfix(ref bool __result, ItemAvailability __instance, ThingCountClass need, Pawn pawn)
        {
            if (!__result && pawn != null && pawn.Faction == Faction.OfPlayer)
            {
                foreach (Building_InfiniteStorage storage in WorldComp.GetInfiniteStorages(pawn.Map))
                {
                    Thing thing;
                    if (storage.IsOperational &&
                        storage.Spawned &&
                        storage.TryGetValue(need.thingDef, out thing))
                    {
                        if (thing.stackCount >= need.count)
                        {
                            Thing removed;
                            int toDrop = (need.count < thing.def.stackLimit) ? thing.def.stackLimit : need.count;
                            if (storage.TryRemove(thing, toDrop, out removed))
                            {
                                BuildingUtil.DropThing(removed, removed.stackCount, storage, storage.Map, false);

                                __result = true;
                                ((Dictionary<int, bool>)CachedResultsFI.GetValue(__instance))[Gen.HashCombine<Faction>(need.GetHashCode(), pawn.Faction)] = __result;
                            }
                            break;
                        }
                    }
                }
            }
        }
    }

    #region Reserve
    static class ReservationManagerUtil
    {
        private static FieldInfo mapFI = null;
        public static Map GetMap(ReservationManager mgr)
        {
            if (mapFI == null)
            {
                mapFI = typeof(ReservationManager).GetField("map", BindingFlags.NonPublic | BindingFlags.Instance);
            }
            return (Map)mapFI.GetValue(mgr);
        }

        public static bool IsInfiniteStorageAt(Map map, IntVec3 position)
        {
            IEnumerable<Thing> things = map.thingGrid.ThingsAt(position);
            if (things != null)
            {
                foreach (Thing t in things)
                {
                    if (t.GetType() == typeof(Building_InfiniteStorage))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(ReservationManager), "Reserve")]
    static class Patch_ReservationManager_Reserve
    {
        static bool Prefix(ref bool __result, ReservationManager __instance, Pawn claimant, LocalTargetInfo target)
        {
            Map map = ReservationManagerUtil.GetMap(__instance);
            if (!__result && target != null && target.IsValid && !target.ThingDestroyed &&
                ReservationManagerUtil.IsInfiniteStorageAt(map, target.Cell))
            {
                __result = true;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(ReservationManager), "Release")]
    static class Patch_ReservationManager_Release
    {
        static bool Prefix(ReservationManager __instance, Pawn claimant, LocalTargetInfo target)
        {
            Map map = ReservationManagerUtil.GetMap(__instance);
            if (target != null && target.IsValid && !target.ThingDestroyed &&
                ReservationManagerUtil.IsInfiniteStorageAt(map, target.Cell))
            {
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(ReservationManager), "CanReserve")]
    static class Patch_ReservationManager_CanReserve
    {
        static bool Prefix(ref bool __result, ReservationManager __instance, Pawn claimant, LocalTargetInfo target)
        {
            Map map = ReservationManagerUtil.GetMap(__instance);
            if (!__result && target != null && target.IsValid && !target.ThingDestroyed &&
                ReservationManagerUtil.IsInfiniteStorageAt(map, target.Cell))
            {
                __result = true;
                return false;
            }
            return true;
        }
    }
    #endregion

    #region Trades
    static class TradeUtil
    {
        public static IEnumerable<Thing> EmptyStorages(Map map)
        {
            List<Thing> l = new List<Thing>();
            foreach (Building_InfiniteStorage storage in WorldComp.GetInfiniteStorages(map))
            {
                if (storage.Map == map && storage.Spawned && storage.IncludeInTradeDeals)
                {
                    storage.Empty(l);
                }
            }
            return l;
        }

        public static void ReclaimThings()
        {
            foreach (Building_InfiniteStorage storage in WorldComp.GetAllInfiniteStorages())
            {
                if (storage.Map != null && storage.Spawned)
                {
                    storage.Reclaim();
                }
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_TraderTracker), "ColonyThingsWillingToBuy")]
    static class Patch_TradeShip_ColonyThingsWillingToBuy
    {
        // Before a caravan trade
        static void Postfix(ref IEnumerable<Thing> __result, Pawn playerNegotiator)
        {
            if (playerNegotiator != null && playerNegotiator.Map != null)
            {
                List<Thing> result = new List<Thing>(__result);
                result.AddRange(TradeUtil.EmptyStorages(playerNegotiator.Map));
                __result = result;
            }
        }
    }

    [HarmonyPatch(typeof(TradeShip), "ColonyThingsWillingToBuy")]
    static class Patch_PassingShip_TryOpenComms
    {
        // Before an orbital trade
        static void Postfix(ref IEnumerable<Thing> __result, Pawn playerNegotiator)
        {
            if (playerNegotiator != null && playerNegotiator.Map != null)
            {
                List<Thing> result = new List<Thing>(__result);
                result.AddRange(TradeUtil.EmptyStorages(playerNegotiator.Map));
                __result = result;
            }
        }
    }

    [HarmonyPatch(typeof(Dialog_Trade), "Close")]
    static class Patch_Window_PreClose
    {
        [HarmonyPriority(Priority.First)]
        static void Postfix(bool doCloseSound)
        {
            TradeUtil.ReclaimThings();
        }
    }
    #endregion

    #region Caravan Forming
    [HarmonyPatch(typeof(Dialog_FormCaravan), "PostOpen")]
    static class Patch_Dialog_FormCaravan_PostOpen
    {
        static void Prefix(Window __instance)
        {
            Type type = __instance.GetType();
            if (type == typeof(Dialog_FormCaravan))
            {
                Map map = __instance.GetType().GetField("map", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(__instance) as Map;
                TradeUtil.EmptyStorages(map);

                foreach (Building_InfiniteStorage storage in WorldComp.GetInfiniteStorages(map))
                {
                    storage.CanAutoCollect = false;
                }
            }
        }
    }

    [HarmonyPatch(typeof(CaravanFormingUtility), "StopFormingCaravan")]
    static class Patch_CaravanFormingUtility_StopFormingCaravan
    {
        [HarmonyPriority(Priority.First)]
        static void Postfix(Lord lord)
        {
            foreach (Building_InfiniteStorage storage in WorldComp.GetInfiniteStorages(lord.Map))
            {
                storage.CanAutoCollect = true;
                storage.Reclaim();
            }
        }
    }

    [HarmonyPatch(
        typeof(CaravanExitMapUtility), "ExitMapAndCreateCaravan",
        new Type[] { typeof(IEnumerable<Pawn>), typeof(Faction), typeof(int), typeof(int) })]
    static class Patch_CaravanExitMapUtility_ExitMapAndCreateCaravan_1
    {
        [HarmonyPriority(Priority.First)]
        static void Prefix(IEnumerable<Pawn> pawns, Faction faction, int exitFromTile, int directionTile)
        {
            if (faction == Faction.OfPlayer)
            {
                List<Pawn> p = new List<Pawn>(pawns);
                if (p.Count > 0)
                {
                    foreach (Building_InfiniteStorage storage in WorldComp.GetInfiniteStorages(p[0].Map))
                    {
                        storage.CanAutoCollect = true;
                        storage.Reclaim();
                    }
                }
            }
        }
    }

    [HarmonyPatch(
        typeof(CaravanExitMapUtility), "ExitMapAndCreateCaravan",
        new Type[] { typeof(IEnumerable<Pawn>), typeof(Faction), typeof(int) })]
    static class Patch_CaravanExitMapUtility_ExitMapAndCreateCaravan_2
    {
        static void Prefix(IEnumerable<Pawn> pawns, Faction faction, int startingTile)
        {
            if (faction == Faction.OfPlayer)
            {
                List<Pawn> p = new List<Pawn>(pawns);
                if (p.Count > 0)
                {
                    foreach (Building_InfiniteStorage storage in WorldComp.GetInfiniteStorages(p[0].Map))
                    {
                        storage.CanAutoCollect = true;
                        storage.Reclaim();
                    }
                }
            }
        }
    }
    #endregion

    #region Fix Broken Tool
    [HarmonyPatch(typeof(WorkGiver_FixBrokenDownBuilding), "FindClosestComponent")]
    static class Patch_WorkGiver_FixBrokenDownBuilding_FindClosestComponent
    {
        static void Postfix(Thing __result, Pawn pawn)
        {
            if (pawn != null && __result == null)
            {
                foreach (Building_InfiniteStorage storage in WorldComp.GetInfiniteStorages(pawn.Map))
                {
                    Thing t;
                    if (storage.TryRemove(ThingDefOf.Component, 1, out t))
                    {
                        BuildingUtil.DropThing(t, storage, storage.Map, false);
                    }
                }
                __result = GenClosest.ClosestThingReachable(pawn.Position, pawn.Map, ThingRequest.ForDef(ThingDefOf.Component), PathEndMode.InteractionCell, TraverseParms.For(pawn, pawn.NormalMaxDanger(), TraverseMode.ByPawn, false), 9999f, (Thing x) => !x.IsForbidden(pawn) && pawn.CanReserve(x, 1, -1, null, false), null, 0, -1, false, RegionType.Set_Passable, false);
            }
        }
    }
    #endregion

    #region Handle "Do until X" for stored weapons
    [HarmonyPatch(typeof(RecipeWorkerCounter), "CountProducts")]
    static class Patch_RecipeWorkerCounter_CountProducts
    {
        static void Postfix(ref int __result, RecipeWorkerCounter __instance, Bill_Production bill)
        {
            if (bill.Map == null)
            {
                Log.Error("Bill has null map");
            }

            List<ThingCountClass> products = __instance.recipe.products;
            if (WorldComp.HasInfiniteStorages(bill.Map) && products != null)
            {
                foreach (ThingCountClass product in products)
                {
                    ThingDef def = product.thingDef;
                    foreach (Building_InfiniteStorage s in WorldComp.GetInfiniteStorages(bill.Map))
                    {
                        __result += s.StoredThingCount(def);
                    }
                }
            }
        }
    }
    #endregion
}