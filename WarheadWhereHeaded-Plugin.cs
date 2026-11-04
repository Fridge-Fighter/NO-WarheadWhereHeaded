using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace NavalLogistics
{
	[BepInPlugin("com.playableships.warheadwhereheaded", "WarheadWhereHeaded?", "1.0.5")]
	public class Plugin : BaseUnityPlugin
	{
		public const int WarheadsPerContainer = 10;
		public const float AtlasCapacity = 200000f;
		public const float AtlasRange = 2000f;

		internal static Plugin Instance;
		internal static WeaponMount SourceMount;
		internal static WeaponMount WarheadMount;
		internal static WeaponInfo WarheadInfo;
		internal static UnitDefinition WarheadCargo;
		internal static int SpawnDepth;
		internal static bool SpawningWarhead;
		internal static bool SuppressWarheadCount;

		private readonly HashSet<int> _supplied = new HashSet<int>();
		private readonly Dictionary<int, Airbase> _origins = new Dictionary<int, Airbase>();
		private float _nextScan;
		private bool _loggedWeapons;
		private bool _loggedIbis;
		private FieldInfo _maxCapacity;
		private FieldInfo _accessTransform;
		private FieldInfo _airbaseUnit;
		private FieldInfo _hangarAircraft;
		private FieldInfo _parachute;

		private void Awake()
		{
			Instance = this;
			Logger.LogInfo("WarheadWhereHeaded? 1.0.5 loaded.");
			_maxCapacity = AccessTools.Field(typeof(Rearmer), "maxCapacity");
			_accessTransform = AccessTools.Field(typeof(Rearmer), "accessTransform");
			_airbaseUnit = AccessTools.Field(typeof(Airbase), "attachedUnit");
			_hangarAircraft = AccessTools.Field(typeof(Hangar), "availableAircraft");
			_parachute = AccessTools.Field(typeof(Container), "parachuteSystem");
			try
			{
				Harmony.CreateAndPatchAll(typeof(Patches));
			}
			catch (Exception ex)
			{
				Logger.LogError("WarheadWhereHeaded? failed to patch: " + ex);
			}
		}

		internal void Warn(string message)
		{
			Logger.LogWarning(message);
		}

		private void Update()
		{
			if (Time.unscaledTime < _nextScan)
				return;
			_nextScan = Time.unscaledTime + 2f;
			try
			{
				EnsureWeapons();
				SupplyAtlases();
			}
			catch (Exception ex)
			{
				Logger.LogWarning("Naval Logistics scan failed: " + ex.Message);
			}
		}

		internal bool IsAtlas(Unit unit)
		{
			if (unit == null || unit.disabled)
				return false;
			if (Matches(unit.unitName) || Matches(unit.definition != null ? unit.definition.unitName : null) || Matches(unit.definition != null ? unit.definition.jsonKey : null))
				return true;
			return false;
		}

		internal bool IsAtlasAirbase(Airbase airbase)
		{
			if (airbase == null || _airbaseUnit == null)
				return false;
			return IsAtlas(_airbaseUnit.GetValue(airbase) as Unit);
		}

		private static bool Matches(string value)
		{
			if (string.IsNullOrEmpty(value))
				return false;
			return value.IndexOf("Atlas", StringComparison.OrdinalIgnoreCase) >= 0
				|| value.IndexOf("SupplyShip", StringComparison.OrdinalIgnoreCase) >= 0
				|| value.IndexOf("Supply Ship", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		private void SupplyAtlases()
		{
			Ship[] ships = UnityEngine.Object.FindObjectsOfType<Ship>();
			for (int i = 0; i < ships.Length; i++)
			{
				Ship ship = ships[i];
				if (!IsAtlas(ship) || _supplied.Contains(ship.GetInstanceID()))
					continue;
				MakeSupplyPoint(ship);
				OfferTarantula(ship);
				if (ship.NetworkHQ != null)
					_supplied.Add(ship.GetInstanceID());
			}
		}

		private void MakeSupplyPoint(Ship ship)
		{
			Rearmer rearmer = ship.GetComponent<Rearmer>();
			if (rearmer == null)
				rearmer = ship.gameObject.AddComponent<Rearmer>();
			rearmer.Unit = ship;
			rearmer.Capacity = AtlasCapacity;
			rearmer.Range = AtlasRange;
			rearmer.AvailableForMission = true;
			if (_maxCapacity != null)
				_maxCapacity.SetValue(rearmer, AtlasCapacity);
			if (_accessTransform != null)
				_accessTransform.SetValue(rearmer, ship.transform);
			if (ship.NetworkHQ != null)
				ship.NetworkHQ.RearmMissionController.RegisterRearmer(rearmer);
			Logger.LogInfo("Atlas supply point ready on " + ship.unitName + " (200 t).");
		}

		private void OfferTarantula(Ship ship)
		{
			AircraftDefinition tarantula = FindTarantula();
			if (tarantula == null || _hangarAircraft == null)
				return;
			Hangar[] hangars = ship.GetComponentsInChildren<Hangar>(true);
			for (int i = 0; i < hangars.Length; i++)
			{
				AircraftDefinition[] current = _hangarAircraft.GetValue(hangars[i]) as AircraftDefinition[];
				if (current != null)
				{
					bool found = false;
					for (int a = 0; a < current.Length; a++)
					{
						if (current[a] == tarantula)
							found = true;
					}
					if (found)
						continue;
				}
				int length = current != null ? current.Length : 0;
				AircraftDefinition[] next = new AircraftDefinition[length + 1];
				if (current != null)
					Array.Copy(current, next, length);
				next[length] = tarantula;
				_hangarAircraft.SetValue(hangars[i], next);
				Logger.LogInfo("Atlas hangar can spawn the Tarantula.");
			}
		}

		internal void EnsureWeapons()
		{
			if (WarheadMount != null)
			{
				AttachToAircraft();
				return;
			}
			Encyclopedia book = Encyclopedia.i;
			if (book == null || book.weaponMounts == null || book.weaponMounts.Count == 0)
				return;
			SourceMount = FindMunitionsContainer(book);
			if (SourceMount == null || SourceMount.info == null || SourceMount.prefab == null)
			{
				if (!_loggedWeapons)
				{
					Logger.LogWarning("Naval munitions container was not found.");
					_loggedWeapons = true;
				}
				return;
			}
			MountedCargo sourceCargo = SourceMount.prefab.GetComponentInChildren<MountedCargo>(true);
			if (sourceCargo == null || sourceCargo.cargo == null)
			{
				Logger.LogWarning("Munitions container has no cargo definition.");
				return;
			}
			WarheadCargo = UnityEngine.Object.Instantiate(sourceCargo.cargo);
			WarheadCargo.name = "WarheadContainerUnit";
			WarheadCargo.jsonKey = "WarheadContainerUnit";
			WarheadCargo.unitName = "Warhead Container";
			WarheadCargo.description = Description();
			WarheadCargo.dontAutomaticallyAddToEncyclopedia = true;
			if (Encyclopedia.Lookup != null && !Encyclopedia.Lookup.ContainsKey(WarheadCargo.jsonKey))
				Encyclopedia.Lookup.Add(WarheadCargo.jsonKey, WarheadCargo);

			WarheadInfo = UnityEngine.Object.Instantiate(SourceMount.info);
			WarheadInfo.name = "WarheadContainerInfo";
			WarheadInfo.weaponName = "Warhead Container";
			WarheadInfo.shortName = "Warheads";
			WarheadInfo.description = Description();
			WarheadInfo.nuclear = true;
			WarheadInfo.strategic = false;
			WarheadInfo.cargo = true;
			WarheadInfo.rearmShip = false;
			WarheadInfo.rearmGround = false;
			WarheadInfo.costPerRound = 0f;
			WarheadInfo.blastDamage = 0f;
			WarheadInfo.pierceDamage = 0f;
			WarheadInfo.weaponPrefab = SourceMount.prefab;

			WarheadMount = UnityEngine.Object.Instantiate(SourceMount);
			WarheadMount.name = "WarheadContainer";
			WarheadMount.jsonKey = "WarheadContainer";
			WarheadMount.mountName = "Warhead Container";
			WarheadMount.ammo = WarheadsPerContainer;
			WarheadMount.emptyCost = 0f;
			WarheadMount.Cargo = true;
			WarheadMount.prefab = SourceMount.prefab;
			WarheadMount.info = WarheadInfo;
			WarheadMount.dontAutomaticallyAddToEncyclopedia = false;
			try
			{
				WarheadMount.Initialize();
			}
			catch (Exception ex)
			{
				Logger.LogWarning("Warhead mount init: " + ex.Message);
			}

			if (!book.weaponMounts.Contains(WarheadMount))
				book.weaponMounts.Add(WarheadMount);
			if (Encyclopedia.WeaponLookup != null && !Encyclopedia.WeaponLookup.ContainsKey(WarheadMount.jsonKey))
				Encyclopedia.WeaponLookup.Add(WarheadMount.jsonKey, WarheadMount);
			RegisterIndexes();
			AttachToAircraft();
			Logger.LogInfo("Warhead Container added beside the naval munitions container.");
		}

		internal void RegisterIndexes()
		{
			Encyclopedia book = Encyclopedia.i;
			if (book == null || book.IndexLookup == null)
				return;
			if (WarheadCargo != null && Encyclopedia.Lookup != null && !Encyclopedia.Lookup.ContainsKey(WarheadCargo.jsonKey))
				Encyclopedia.Lookup.Add(WarheadCargo.jsonKey, WarheadCargo);
			if (WarheadMount != null && Encyclopedia.WeaponLookup != null && !Encyclopedia.WeaponLookup.ContainsKey(WarheadMount.jsonKey))
				Encyclopedia.WeaponLookup.Add(WarheadMount.jsonKey, WarheadMount);
			RegisterOne(book, WarheadCargo);
			RegisterOne(book, WarheadMount);
		}

		private static void RegisterOne(Encyclopedia book, ScriptableObject item)
		{
			if (!(item is INetworkDefinition definition))
				return;
			int index = book.IndexLookup.IndexOf(definition);
			if (index < 0)
			{
				index = book.IndexLookup.Count;
				book.IndexLookup.Add(definition);
			}
			definition.LookupIndex = index;
		}

		private static string Description()
		{
			return "Carries 10 nuclear warheads from the airbase that loaded it to the airbase where this aircraft lands. It costs nothing, the warheads are already yours. Since no sane person would throw ten warheads out of an aircraft, it has no parachute. Keep an eye on the bay door lever.";
		}

		private static WeaponMount FindMunitionsContainer(Encyclopedia book)
		{
			WeaponMount fallback = null;
			for (int i = 0; i < book.weaponMounts.Count; i++)
			{
				WeaponMount mount = book.weaponMounts[i];
				if (mount == null || mount.info == null || !mount.info.cargo || !mount.info.rearmShip)
					continue;
				string name = (mount.mountName ?? "") + " " + (mount.info.weaponName ?? "") + " " + mount.name;
				if (name.IndexOf("munition", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("naval", StringComparison.OrdinalIgnoreCase) >= 0)
					return mount;
				if (fallback == null)
					fallback = mount;
			}
			return fallback;
		}

		private static AircraftDefinition FindTarantula()
		{
			Encyclopedia book = Encyclopedia.i;
			if (book == null || book.aircraft == null)
				return null;
			for (int i = 0; i < book.aircraft.Count; i++)
			{
				AircraftDefinition aircraft = book.aircraft[i];
				if (aircraft == null)
					continue;
				string name = (aircraft.unitName ?? "") + " " + (aircraft.jsonKey ?? "");
				if (name.IndexOf("Tarantula", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("VL-49", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("VL49", StringComparison.OrdinalIgnoreCase) >= 0)
					return aircraft;
			}
			return null;
		}

		private void AttachToAircraft()
		{
			if (SourceMount == null || WarheadMount == null)
				return;
			Encyclopedia book = Encyclopedia.i;
			if (book != null && book.aircraft != null)
			{
				for (int i = 0; i < book.aircraft.Count; i++)
				{
					AircraftDefinition definition = book.aircraft[i];
					if (definition == null || definition.unitPrefab == null)
						continue;
					WeaponManager manager = definition.unitPrefab.GetComponentInChildren<WeaponManager>(true);
					AddOption(manager, definition);
				}
			}
			Aircraft[] flying = UnityEngine.Object.FindObjectsOfType<Aircraft>();
			for (int i = 0; i < flying.Length; i++)
				AddOption(flying[i].weaponManager, flying[i].definition);
		}

		private void AddOption(WeaponManager manager, AircraftDefinition definition)
		{
			if (manager == null || manager.hardpointSets == null || WarheadMount == null)
				return;
			bool ibis = IsIbis(definition, manager);
			bool added = false;
			for (int i = 0; i < manager.hardpointSets.Length; i++)
			{
				HardpointSet set = manager.hardpointSets[i];
				if (set == null || set.weaponOptions == null)
					continue;
				bool hasSource = SourceMount != null && set.weaponOptions.Contains(SourceMount);
				if (!hasSource && !(ibis && IsCargoBay(set)))
					continue;
				if (set.weaponOptions.Contains(WarheadMount))
					continue;
				set.weaponOptions.Add(WarheadMount);
				added = true;
			}
			if (added && ibis && !_loggedIbis)
			{
				_loggedIbis = true;
				Logger.LogInfo("Warhead Container added to the Ibis cargo bay.");
			}
		}

		private static bool IsIbis(AircraftDefinition definition, WeaponManager manager)
		{
			string blob = "";
			if (definition != null)
				blob += (definition.unitName ?? "") + " " + (definition.jsonKey ?? "") + " " + definition.name;
			if (manager != null)
			{
				Transform transform = manager.transform;
				while (transform != null)
				{
					blob += " " + transform.name;
					transform = transform.parent;
				}
			}
			return blob.IndexOf("Ibis", StringComparison.OrdinalIgnoreCase) >= 0 || blob.IndexOf("UtilityHelo1", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		private static bool IsCargoBay(HardpointSet set)
		{
			string name = set.name ?? "";
			if (name.IndexOf("bay", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("cargo", StringComparison.OrdinalIgnoreCase) >= 0)
				return true;
			if (set.hardpoints != null)
			{
				for (int i = 0; i < set.hardpoints.Count; i++)
				{
					Hardpoint hardpoint = set.hardpoints[i];
					if (hardpoint == null || hardpoint.transform == null)
						continue;
					string hardpointName = hardpoint.transform.name ?? "";
					if (hardpointName.IndexOf("bay", StringComparison.OrdinalIgnoreCase) >= 0 || hardpointName.IndexOf("cargo", StringComparison.OrdinalIgnoreCase) >= 0)
						return true;
				}
			}
			if (set.weaponOptions != null)
			{
				for (int i = 0; i < set.weaponOptions.Count; i++)
				{
					WeaponMount option = set.weaponOptions[i];
					if (option != null && (option.Cargo || (option.info != null && option.info.cargo)))
						return true;
				}
			}
			return false;
		}

		internal void RememberOrigin(Aircraft aircraft, Airbase airbase)
		{
			if (aircraft == null || airbase == null)
				return;
			_origins[aircraft.GetInstanceID()] = airbase;
		}

		internal bool TryOrigin(Aircraft aircraft, out Airbase airbase)
		{
			airbase = null;
			if (aircraft == null)
				return false;
			return _origins.TryGetValue(aircraft.GetInstanceID(), out airbase) && airbase != null;
		}

		internal void ForgetOrigin(Aircraft aircraft)
		{
			if (aircraft != null)
				_origins.Remove(aircraft.GetInstanceID());
		}

		internal static bool CanStore(Airbase airbase)
		{
			if (airbase == null || !airbase.HasStorage() || airbase.stores == null)
				return false;
			for (int i = 0; i < airbase.stores.Count; i++)
			{
				WarheadStorage store = airbase.stores[i];
				if (store != null && !store.Disabled)
					return true;
			}
			return false;
		}

		internal static Aircraft AircraftOf(WeaponManager manager)
		{
			if (manager == null)
				return null;
			Aircraft aircraft = manager.GetComponent<Aircraft>();
			if (aircraft == null)
				aircraft = manager.GetComponentInParent<Aircraft>();
			return aircraft;
		}

		internal static int LoadedWarheads(Aircraft aircraft)
		{
			if (aircraft == null || aircraft.weaponStations == null || WarheadInfo == null)
				return 0;
			int containers = 0;
			for (int i = 0; i < aircraft.weaponStations.Count; i++)
			{
				WeaponStation station = aircraft.weaponStations[i];
				if (station == null || station.Weapons == null)
					continue;
				for (int w = 0; w < station.Weapons.Count; w++)
				{
					Weapon weapon = station.Weapons[w];
					if (weapon != null && weapon.info == WarheadInfo && weapon.GetAmmoTotal() > 0)
						containers++;
				}
			}
			return containers * WarheadsPerContainer;
		}

		internal void StripParachute(Container container)
		{
			if (container == null)
				return;
			if (_parachute != null)
				_parachute.SetValue(container, null);
			Transform[] children = container.GetComponentsInChildren<Transform>(true);
			for (int i = 0; i < children.Length; i++)
			{
				string name = children[i].name;
				if (name.IndexOf("parachute", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("chute", StringComparison.OrdinalIgnoreCase) >= 0)
					children[i].gameObject.SetActive(false);
			}
			FieldInfo effects = AccessTools.Field(typeof(UnitPart), "damageEffects");
			FieldInfo boom = AccessTools.Field(typeof(UnitPart), "disintegrationEffects");
			UnitPart[] parts = container.GetComponentsInChildren<UnitPart>(true);
			for (int i = 0; i < parts.Length; i++)
			{
				if (effects != null)
					effects.SetValue(parts[i], new List<DamageEffect>());
				if (boom != null)
					boom.SetValue(parts[i], new GameObject[0]);
			}
			NameWarhead(container);
			if (container.GetComponent<WarheadBreak>() == null)
				container.gameObject.AddComponent<WarheadBreak>();
		}

		internal void NameWarhead(Container container)
		{
			if (container == null)
				return;
			container.unitName = "Warhead Container";
			if (UnitRegistry.TryGetPersistentUnit(container.persistentID, out PersistentUnit persistent) && persistent != null)
				persistent.unitName = "Warhead Container";
		}
	}

	public class WarheadBreak : MonoBehaviour
	{
		private float _armAt;
		private bool _broke;
		private Unit _unit;

		private void Awake()
		{
			_unit = GetComponent<Unit>();
			_armAt = Time.time + 1f;
		}

		private void OnCollisionEnter(Collision collision)
		{
			if (_broke || Time.time < _armAt || collision == null)
				return;
			if (collision.relativeVelocity.magnitude < 8f)
				return;
			Break();
		}

		private void Break()
		{
			_broke = true;
			Transform[] children = GetComponentsInChildren<Transform>(true);
			for (int i = 0; i < children.Length; i++)
			{
				string name = children[i].name;
				if (name.IndexOf("explos", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("deton", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("parachute", StringComparison.OrdinalIgnoreCase) >= 0)
					children[i].gameObject.SetActive(false);
			}
			MonoBehaviour[] behaviours = GetComponentsInChildren<MonoBehaviour>(true);
			for (int i = 0; i < behaviours.Length; i++)
			{
				if (behaviours[i] == null || behaviours[i] == this)
					continue;
				string name = behaviours[i].GetType().Name;
				if (name.IndexOf("Explos", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Deton", StringComparison.OrdinalIgnoreCase) >= 0)
					behaviours[i].enabled = false;
			}
			if (_unit != null)
				_unit.Networkdisabled = true;
		}
	}

	public static class Patches
	{
		[HarmonyPostfix]
		[HarmonyPatch(typeof(Encyclopedia), "AfterLoad", new System.Type[0])]
		private static void KeepRegistered()
		{
			if (Plugin.Instance == null)
				return;
			try
			{
				Plugin.Instance.EnsureWeapons();
			}
			catch (Exception ex)
			{
				Plugin.Instance.Warn("Warhead container was not created: " + ex.Message);
			}
		}

		[HarmonyPostfix]
		[HarmonyPatch(typeof(MountedCargo), "AttachToHardpoint")]
		private static void UseWarheadCargo(MountedCargo __instance, WeaponMount mount)
		{
			if (__instance == null || mount == null || mount != Plugin.WarheadMount)
				return;
			__instance.cargo = Plugin.WarheadCargo;
			__instance.info = Plugin.WarheadInfo;
			__instance.ammo = Plugin.WarheadsPerContainer;
		}

		[HarmonyPrefix]
		[HarmonyPatch(typeof(Rearmer), "Awake")]
		private static bool SkipEmptyRearmer(Rearmer __instance)
		{
			return __instance != null && __instance.Unit != null;
		}

		[HarmonyPostfix]
		[HarmonyPatch(typeof(WeaponChecker), nameof(WeaponChecker.MountAllowedAirbase))]
		private static void AtlasCanIssueContainers(WeaponMount mount, Airbase airbase, ref bool __result)
		{
			if (__result || Plugin.Instance == null || mount == null || mount.info == null || airbase == null)
				return;
			if (mount.info.cargo && mount.info.rearmShip && Plugin.Instance.IsAtlasAirbase(airbase))
				__result = true;
		}

		[HarmonyPrefix]
		[HarmonyPatch(typeof(WeaponChecker), nameof(WeaponChecker.PreferNukesFilter))]
		private static void KeepWarheadsManual(List<WeaponMount> listToFilter)
		{
			if (listToFilter == null || Plugin.WarheadMount == null)
				return;
			listToFilter.Remove(Plugin.WarheadMount);
		}

		[HarmonyPostfix]
		[HarmonyPatch(typeof(WeaponManager), nameof(WeaponManager.GetCurrentWarheads))]
		private static void CountWarheadContainers(WeaponManager __instance, ref int __result)
		{
			if (__instance == null || Plugin.WarheadInfo == null)
				return;
			Aircraft aircraft = __instance.GetComponent<Aircraft>();
			if (aircraft == null)
				aircraft = __instance.GetComponentInParent<Aircraft>();
			if (aircraft == null || aircraft.weaponStations == null)
				return;
			for (int i = 0; i < aircraft.weaponStations.Count; i++)
			{
				WeaponStation station = aircraft.weaponStations[i];
				if (station == null || station.Weapons == null)
					continue;
				int containers = 0;
				int counted = 0;
				for (int w = 0; w < station.Weapons.Count; w++)
				{
					Weapon weapon = station.Weapons[w];
					if (weapon == null || weapon.info != Plugin.WarheadInfo)
						continue;
					counted += weapon.ammo;
					if (weapon.GetAmmoTotal() > 0)
						containers++;
				}
				if (Plugin.SuppressWarheadCount)
				{
					if (station.WeaponInfo != null && station.WeaponInfo.nuclear)
						__result -= counted;
					continue;
				}
				if (containers == 0)
					continue;
				if (station.WeaponInfo != null && station.WeaponInfo.nuclear)
					__result -= counted;
				__result += containers * Plugin.WarheadsPerContainer;
			}
		}

		[HarmonyPrefix]
		[HarmonyPatch(typeof(WeaponChecker), nameof(WeaponChecker.VetLoadout))]
		private static void KeepIbisOption(AircraftDefinition definition)
		{
			if (Plugin.Instance == null || definition == null)
				return;
			Plugin.Instance.EnsureWeapons();
		}

		[HarmonyPrefix]
		[HarmonyPatch(typeof(WeaponManager), nameof(WeaponManager.SpawnWeapons))]
		private static void NoteOriginBefore(WeaponManager __instance, out int __state)
		{
			__state = 0;
			if (__instance == null)
				return;
			try
			{
				__state = __instance.GetCurrentWarheads();
			}
			catch (Exception)
			{
				__state = 0;
			}
		}

		[HarmonyPostfix]
		[HarmonyPatch(typeof(WeaponManager), nameof(WeaponManager.SpawnWeapons))]
		private static void NoteOriginAfter(WeaponManager __instance, int __state)
		{
			if (Plugin.Instance == null || __instance == null)
				return;
			Aircraft aircraft = Plugin.AircraftOf(__instance);
			if (aircraft == null)
				return;
			int after = 0;
			try
			{
				after = __instance.GetCurrentWarheads();
			}
			catch (Exception)
			{
				return;
			}
			if (after <= 0)
			{
				Plugin.Instance.ForgetOrigin(aircraft);
				return;
			}
			if (after <= __state || aircraft.NetworkHQ == null)
				return;
			Airbase origin = aircraft.NetworkHQ.GetNearestAirbase(aircraft.transform.position);
			Plugin.Instance.RememberOrigin(aircraft, origin);
		}

		[HarmonyPrefix]
		[HarmonyPatch(typeof(WeaponManager), nameof(WeaponManager.ReturnWeapons))]
		private static void RefundUnstorable(WeaponManager __instance)
		{
			if (Plugin.Instance == null || __instance == null)
				return;
			Aircraft aircraft = Plugin.AircraftOf(__instance);
			if (aircraft == null || aircraft.NetworkHQ == null)
				return;
			Airbase destination = aircraft.NetworkHQ.GetNearestAirbase(aircraft.transform.position);
			if (Plugin.CanStore(destination))
				return;
			if (!Plugin.Instance.TryOrigin(aircraft, out Airbase origin))
				return;
			int warheads = Plugin.LoadedWarheads(aircraft);
			if (warheads <= 0)
				return;
			Plugin.SuppressWarheadCount = true;
			origin.AddWarheads(warheads);
			Plugin.Instance.ForgetOrigin(aircraft);
			Plugin.Instance.Warn("Returned " + warheads + " warheads to the airbase they came from. The landing airbase has no storage.");
		}

		[HarmonyPostfix]
		[HarmonyPatch(typeof(WeaponManager), nameof(WeaponManager.ReturnWeapons))]
		private static void ClearWarheadSuppress()
		{
			Plugin.SuppressWarheadCount = false;
		}

		[HarmonyPrefix]
		[HarmonyPatch(typeof(Spawner), nameof(Spawner.SpawnUnit), new[] { typeof(UnitDefinition), typeof(Vector3), typeof(Quaternion), typeof(Vector3), typeof(Unit), typeof(Player) })]
		private static void NoteWarheadSpawn(UnitDefinition unit)
		{
			Plugin.SpawnDepth++;
			if (unit != null && unit == Plugin.WarheadCargo)
				Plugin.SpawningWarhead = true;
		}

		[HarmonyPostfix]
		[HarmonyPatch(typeof(Spawner), nameof(Spawner.SpawnUnit), new[] { typeof(UnitDefinition), typeof(Vector3), typeof(Quaternion), typeof(Vector3), typeof(Unit), typeof(Player) })]
		private static void ClearWarheadSpawn()
		{
			Plugin.SpawnDepth--;
			if (Plugin.SpawnDepth <= 0)
			{
				Plugin.SpawnDepth = 0;
				Plugin.SpawningWarhead = false;
			}
		}

		[HarmonyPrefix]
		[HarmonyPatch(typeof(Container), "InitializeUnit")]
		private static void NoParachute(Container __instance)
		{
			if (Plugin.Instance == null || __instance == null)
				return;
			bool warhead = Plugin.SpawningWarhead || (__instance.definition != null && __instance.definition == Plugin.WarheadCargo);
			if (warhead)
				Plugin.Instance.StripParachute(__instance);
		}

		[HarmonyPostfix]
		[HarmonyPatch(typeof(Container), "InitializeUnit")]
		private static void NameAfterSpawn(Container __instance)
		{
			if (Plugin.Instance == null || __instance == null || __instance.definition != Plugin.WarheadCargo)
				return;
			Plugin.Instance.NameWarhead(__instance);
		}
	}
}
