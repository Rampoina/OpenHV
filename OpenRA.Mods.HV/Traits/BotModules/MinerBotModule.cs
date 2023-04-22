#region Copyright & License Information
/*
 * Copyright 2019-2022 The OpenHV Developers (see CREDITS)
 * This file is part of OpenHV, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.HV.Traits
{
	[Desc("Manages AI miner deployment logic.")]
	public class MinerBotModuleInfo : ConditionalTraitInfo
	{
		[ActorReference]
		[FieldLoader.Require]
		[Desc("Actor types that can deploy onto resources.")]
		public readonly HashSet<string> DeployableActorTypes = new();

		[Desc("Where to request production of additional deployable actors.")]
		public readonly string VehiclesQueue = "Vehicle";

		[FieldLoader.Require]
		[Desc("Terrain types that can be targeted for deployment.")]
		public readonly HashSet<string> DeployableTerrainTypes = new();

		[ActorReference]
		[FieldLoader.Require]
		[Desc("Actor types that have been deployed onto resources.")]
		public readonly HashSet<string> DeployedActorTypes = new();

		[Desc("Prioritize this many resource towers before building other units.")]
		public readonly int MinimumDeployedActors = 1;

		[Desc("Minimum delay (in ticks) between trying to deploy with DeployableActorTypes.")]
		public readonly int MinimumScanDelay = 20;

		[Desc("Minimum delay (in ticks) after the last search for resources failed.")]
		public readonly int LastSearchFailedDelay = 500;

		[Desc("Avoid enemy actors nearby when searching for a new resource patch. Should be somewhere near the max weapon range.")]
		public readonly WDist EnemyAvoidanceRadius = WDist.FromCells(8);

		public override object Create(ActorInitializer init) { return new MinerBotModule(init.Self, this); }
	}

	public class MinerBotModule : ConditionalTrait<MinerBotModuleInfo>, IBotTick
	{
		readonly World world;
		readonly Player player;

		readonly Func<Actor, bool> unitCannotBeOrdered;

		int scanForIdleMinersTicks;

		IResourceLayer resourceLayer;

		IBotRequestUnitProduction[] requestUnitProduction;

		class MinerTraitWrapper
		{
			public readonly Actor Actor;
			public readonly Mobile Mobile;
			public readonly Transforms Transforms;

			public MinerTraitWrapper(Actor actor)
			{
				Actor = actor;
				Mobile = actor.Trait<Mobile>();
				Transforms = actor.Trait<Transforms>();
			}
		}

		readonly Dictionary<Actor, MinerTraitWrapper> miners = new();

		public MinerBotModule(Actor self, MinerBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;

			unitCannotBeOrdered = a => a.Owner != self.Owner || a.IsDead || !a.IsInWorld;
		}

		protected override void Created(Actor self)
		{
			requestUnitProduction = self.Owner.PlayerActor.TraitsImplementing<IBotRequestUnitProduction>().ToArray();
		}

		protected override void TraitEnabled(Actor self)
		{
			// PERF: Avoid all AIs reevaluating assignments on the same tick, randomize their initial evaluation delay.
			scanForIdleMinersTicks = world.LocalRandom.Next(0, Info.MinimumScanDelay);

			resourceLayer = world.WorldActor.TraitOrDefault<IResourceLayer>();
		}

		void IBotTick.BotTick(IBot bot)
		{
			if (resourceLayer == null || resourceLayer.IsEmpty)
				return;

			if (--scanForIdleMinersTicks > 0)
				return;

			scanForIdleMinersTicks = Info.MinimumScanDelay;

			var toRemove = miners.Keys.Where(unitCannotBeOrdered).ToList();
			foreach (var a in toRemove)
				miners.Remove(a);

			// TODO: Look for a more performance friendly way to update this list
			var newMiners = world.Actors.Where(a => Info.DeployableActorTypes.Contains(a.Info.Name) && a.Owner == player && !miners.ContainsKey(a));
			foreach (var a in newMiners)
				miners[a] = new MinerTraitWrapper(a);

			foreach (var miner in miners)
			{
				if (!miner.Key.IsIdle)
					continue;

				// Tell the idle miner to quit slacking:
				var newSafeResourcePatch = FindNextResource(miner.Key, miner.Value);
				if (newSafeResourcePatch.Type == TargetType.Invalid)
				{
					scanForIdleMinersTicks = Info.LastSearchFailedDelay;
					return;
				}

				var cell = world.Map.CellContaining(newSafeResourcePatch.CenterPosition);
				AIUtils.BotDebug($"{miner.Key.Owner}: {miner.Key} is idle. Ordering to {cell} for deployment.");
				bot.QueueOrder(new Order("DeployMiner", miner.Key, newSafeResourcePatch, false));
			}

			// Keep the economy running before starving out.
			var queue = AIUtils.FindQueues(player, Info.VehiclesQueue).FirstOrDefault();
			var minerInfo = AIUtils.GetInfoByCommonName(Info.DeployableActorTypes, player);
			if (queue == null || !queue.CanBuild(minerInfo))
				return;

			var unitBuilder = requestUnitProduction.FirstOrDefault(Exts.IsTraitEnabled);
			if (unitBuilder == null)
				return;

			var miningTowers = AIUtils.CountBuildingByCommonName(Info.DeployedActorTypes, player);
			if (miningTowers < Info.MinimumDeployedActors && unitBuilder.RequestedProductionCount(bot, minerInfo.Name) == 0)
				unitBuilder.RequestUnitProduction(bot, minerInfo.Name);
		}

		Target FindNextResource(Actor actor, MinerTraitWrapper miner)
		{
			var towerInfo = AIUtils.GetInfoByCommonName(Info.DeployedActorTypes, player);
			var buildingInfo = towerInfo.TraitInfo<BuildingInfo>();
			bool IsValidResource(CPos cell) =>
				Info.DeployableTerrainTypes.Contains(world.Map.GetTerrainInfo(cell).Type)
					&& miner.Mobile.Locomotor.CanStayInCell(cell)
					&& world.CanPlaceBuilding(cell + miner.Transforms.Info.Offset, towerInfo, buildingInfo, actor);

			var path = miner.Mobile.PathFinder.FindPathToTargetCellByPredicate(
				actor, new[] { actor.Location }, IsValidResource, BlockedByActor.Stationary,
				location => world.FindActorsInCircle(world.Map.CenterOfCell(location), Info.EnemyAvoidanceRadius)
					.Where(u => !u.IsDead && actor.Owner.RelationshipWith(u.Owner) == PlayerRelationship.Enemy)
					.Sum(u => Math.Max(WDist.Zero.Length, Info.EnemyAvoidanceRadius.Length - (world.Map.CenterOfCell(location) - u.CenterPosition).Length)));

			if (path.Count == 0)
				return Target.Invalid;

			return Target.FromCell(world, path[0]);
		}
	}
}
