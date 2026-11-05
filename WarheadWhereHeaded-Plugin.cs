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
	[BepInPlugin("com.playableships.warheadwhereheaded", "WarheadWhereHeaded?", "1.1.2")]
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
		private bool _loggedTarantula;
		private FieldInfo _maxCapacity;
		private FieldInfo _accessTransform;
		private FieldInfo _airbaseUnit;
		private FieldInfo _hangarAircraft;
		private FieldInfo _parachute;
		private FieldInfo _wmAircraft;

		private void Awake()
		{
			Instance = this;
			Logger.LogInfo("WarheadWhereHeaded? 1.1.2 loaded.");
			_maxCapacity = AccessTools.Field(typeof(Rearmer), "maxCapacity");
			_accessTransform = AccessTools.Field(typeof(Rearmer), "accessTransform");
			_airbaseUnit = AccessTools.Field(typeof(Airbase), "attachedUnit");
			_hangarAircraft = AccessTools.Field(typeof(Hangar), "availableAircraft");
			_parachute = AccessTools.Field(typeof(Container), "parachuteSystem");
			_wmAircraft = AccessTools.Field(typeof(WeaponManager), "aircraft");
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
			WarheadInfo.nuclear = false;
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
			return "Carries up to 10 nuclear warheads from the airbase that loaded it to the airbase where this aircraft lands. If fewer than 10 are in storage, it takes whatever is left. It costs nothing, the warheads are already yours. Since no sane person would throw warheads out of an aircraft, it has no parachute. Keep an eye on the bay door lever.";
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
			bool hauler = IsHauler(definition, manager);
			bool added = false;
			for (int i = 0; i < manager.hardpointSets.Length; i++)
			{
				HardpointSet set = manager.hardpointSets[i];
				if (set == null || set.weaponOptions == null)
					continue;
				bool hasSource = SourceMount != null && set.weaponOptions.Contains(SourceMount);
				if (!hasSource && !(hauler && IsCargoBay(set)))
					continue;
				if (set.weaponOptions.Contains(WarheadMount))
					continue;
				set.weaponOptions.Add(WarheadMount);
				added = true;
			}
			if (!added)
				return;
			if (IsIbis(definition, manager) && !_loggedIbis)
			{
				_loggedIbis = true;
				Logger.LogInfo("Warhead Container added to the Ibis cargo bay.");
			}
			if (IsTarantula(definition, manager) && !_loggedTarantula)
			{
				_loggedTarantula = true;
				Logger.LogInfo("Warhead Container added to the Tarantula cargo bay.");
			}
		}

		private static bool IsHauler(AircraftDefinition definition, WeaponManager manager)
		{
			return IsIbis(definition, manager) || IsTarantula(definition, manager);
		}

		private static bool IsTarantula(AircraftDefinition definition, WeaponManager manager)
		{
			string blob = AircraftBlob(definition, manager);
			return blob.IndexOf("Tarantula", StringComparison.OrdinalIgnoreCase) >= 0
				|| blob.IndexOf("VL-49", StringComparison.OrdinalIgnoreCase) >= 0
				|| blob.IndexOf("VL49", StringComparison.OrdinalIgnoreCase) >= 0
				|| blob.IndexOf("QuadVTOL", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		private static bool IsIbis(AircraftDefinition definition, WeaponManager manager)
		{
			string blob = AircraftBlob(definition, manager);
			return blob.IndexOf("Ibis", StringComparison.OrdinalIgnoreCase) >= 0 || blob.IndexOf("UtilityHelo1", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		private static string AircraftBlob(AircraftDefinition definition, WeaponManager manager)
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
			return blob;
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
			if (aircraft == null && Instance != null && Instance._wmAircraft != null)
				aircraft = Instance._wmAircraft.GetValue(manager) as Aircraft;
			return aircraft;
		}

		internal static int LoadedWarheads(Aircraft aircraft)
		{
			if (aircraft == null || aircraft.weaponStations == null || WarheadInfo == null)
				return 0;
			int warheads = 0;
			for (int i = 0; i < aircraft.weaponStations.Count; i++)
			{
				WeaponStation station = aircraft.weaponStations[i];
				if (station == null || station.Weapons == null)
					continue;
				for (int w = 0; w < station.Weapons.Count; w++)
				{
					Weapon weapon = station.Weapons[w];
					if (weapon != null && weapon.info == WarheadInfo && weapon.ammo > 0)
						warheads += weapon.ammo;
				}
			}
			return warheads;
		}

		internal void Note(string message)
		{
			Logger.LogInfo(message);
		}

		internal static Airbase FillSource;
		internal static int LoadedBefore;
		internal static Airbase PendingSpawnAirbase;
		internal static int PendingSpawnPlayerId;
		internal static float PendingSpawnTime;

		internal static bool IsWarheadMount(WeaponMount mount)
		{
			if (mount == null || WarheadInfo == null)
				return false;
			if (mount == WarheadMount || mount.info == WarheadInfo)
				return true;
			if (mount.info != null && (mount.info.name == "WarheadContainerInfo" || mount.info.weaponName == "Warhead Container"))
				return true;
			return !string.IsNullOrEmpty(mount.jsonKey) && mount.jsonKey.IndexOf("Warhead", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		internal static bool WantsWarheads(Aircraft aircraft)
		{
			if (aircraft == null || aircraft.loadout == null || aircraft.loadout.weapons == null)
				return false;
			for (int i = 0; i < aircraft.loadout.weapons.Count; i++)
			{
				if (IsWarheadMount(aircraft.loadout.weapons[i]))
					return true;
			}
			return false;
		}

		internal static bool UsableBin(WarheadStorage bin)
		{
			if (bin == null)
				return false;
			Unit unit = bin.GetUnit();
			if (unit != null && unit.disabled)
				return false;
			try
			{
				return !bin.Disabled;
			}
			catch
			{
				return false;
			}
		}

		internal static void CollectBins(Airbase airbase, List<WarheadStorage> into)
		{
			if (airbase == null || into == null)
				return;
			Ship ship = airbase.GetComponent<Ship>() ?? airbase.GetComponentInParent<Ship>();
			if (ship == null && airbase.TryGetAttachedUnit(out Unit unit) && unit != null)
				ship = unit.GetComponent<Ship>() ?? unit.GetComponentInParent<Ship>();
			if (ship != null)
			{
				WarheadStorage[] bins = ship.GetComponentsInChildren<WarheadStorage>(true);
				for (int i = 0; i < bins.Length; i++)
				{
					if (UsableBin(bins[i]) && !into.Contains(bins[i]))
						into.Add(bins[i]);
				}
			}
			if (airbase.stores == null)
				return;
			for (int i = 0; i < airbase.stores.Count; i++)
			{
				WarheadStorage bin = airbase.stores[i];
				if (UsableBin(bin) && !into.Contains(bin))
					into.Add(bin);
			}
		}

		internal static int SumBins(List<WarheadStorage> bins)
		{
			int sum = 0;
			for (int i = 0; i < bins.Count; i++)
				sum += bins[i].GetNumber();
			return sum;
		}

		internal static int CountStock(Airbase airbase)
		{
			if (airbase == null)
				return 0;
			AdoptStores(airbase);
			int registered = 0;
			try
			{
				registered = airbase.GetWarheads();
			}
			catch
			{
				registered = 0;
			}
			List<WarheadStorage> bins = new List<WarheadStorage>();
			CollectBins(airbase, bins);
			return Math.Max(registered, SumBins(bins));
		}

		internal static int TakeFrom(Airbase airbase, int count)
		{
			if (airbase == null || count <= 0)
				return 0;
			AdoptStores(airbase);
			int removed = 0;
			int registered = 0;
			try
			{
				registered = airbase.GetWarheads();
			}
			catch
			{
				registered = 0;
			}
			int via = Math.Min(count, registered);
			if (via > 0)
			{
				airbase.RemoveWarheads(via);
				removed = via;
			}
			int left = count - removed;
			if (left <= 0)
				return removed;
			List<WarheadStorage> bins = new List<WarheadStorage>();
			CollectBins(airbase, bins);
			bins.Sort((a, b) => b.GetNumber().CompareTo(a.GetNumber()));
			for (int i = 0; i < bins.Count && left > 0; i++)
			{
				int have = bins[i].GetNumber();
				if (have <= 0)
					continue;
				int pull = Math.Min(have, left);
				bins[i].RemoveWarhead(pull);
				left -= pull;
				removed += pull;
			}
			return removed;
		}

		private static FieldInfo _previewAircraft;

		internal static bool IsMenuPreview(Aircraft aircraft)
		{
			if (aircraft == null)
				return false;
			if (_previewAircraft == null)
			{
				System.Type menuType = AccessTools.TypeByName("AircraftSelectionMenu");
				_previewAircraft = menuType != null ? AccessTools.Field(menuType, "previewAircraft") : null;
			}
			if (_previewAircraft == null)
				return false;
			UnityEngine.Object[] menus = UnityEngine.Object.FindObjectsOfType(_previewAircraft.DeclaringType);
			for (int i = 0; i < menus.Length; i++)
			{
				if (_previewAircraft.GetValue(menus[i]) as Aircraft == aircraft)
					return true;
			}
			return false;
		}

		internal static bool Committing(Aircraft aircraft)
		{
			if (aircraft == null || !aircraft.networked || IsMenuPreview(aircraft))
				return false;
			return true;
		}

		internal static void EnsureBudget(Aircraft aircraft)
		{
			if (BudgetReady)
				return;
			BudgetReady = true;
			Airbase source = ResolveSource(aircraft);
			int stock = CountStock(source);
			if (stock <= 0)
			{
				Airbase near = StorageNear(aircraft);
				int nearStock = CountStock(near);
				if (nearStock > stock)
				{
					source = near;
					stock = nearStock;
				}
			}
			FillSource = source;
			StockAtSpawn = stock;
			bool commit = Committing(aircraft);
			CommitSpawn = commit;
			FillBudget = stock + (commit ? Math.Max(0, LoadedBefore) : 0);
			if (Instance != null)
			{
				if (commit)
				{
					string aboard = LoadedBefore > 0 ? " plus " + LoadedBefore + " already aboard" : "";
					Instance.Note("Spawn source " + AirbaseName(source) + " has " + stock + " warheads" + aboard + ".");
				}
				else
					Instance.Note("Hangar preview only. " + AirbaseName(source) + " still has " + stock + " warheads.");
			}
		}

		internal static void AdoptStores(Airbase airbase)
		{
			if (airbase == null)
				return;
			Ship ship = airbase.GetComponent<Ship>();
			if (ship == null)
				ship = airbase.GetComponentInParent<Ship>();
			if (ship == null && airbase.TryGetAttachedUnit(out Unit unit) && unit != null)
				ship = unit.GetComponent<Ship>() ?? unit.GetComponentInParent<Ship>();
			if (ship == null)
				return;
			WarheadStorage[] bins = ship.GetComponentsInChildren<WarheadStorage>(true);
			for (int i = 0; i < bins.Length; i++)
			{
				WarheadStorage bin = bins[i];
				if (bin == null || bin.GetUnit() == null || bin.Disabled)
					continue;
				if (airbase.stores != null && airbase.stores.Contains(bin))
					continue;
				airbase.AddStorage(bin);
			}
		}

		internal static Airbase StorageNear(Aircraft aircraft)
		{
			if (aircraft == null)
				return null;
			WarheadStorage[] bins = UnityEngine.Object.FindObjectsOfType<WarheadStorage>();
			Airbase best = null;
			float bestDist = 1600f;
			Vector3 pos = aircraft.transform.position;
			for (int i = 0; i < bins.Length; i++)
			{
				WarheadStorage bin = bins[i];
				if (bin == null || bin.GetUnit() == null || bin.Disabled || bin.GetNumber() <= 0)
					continue;
				Unit unit = bin.GetUnit();
				if (unit != null && aircraft.NetworkHQ != null && unit.NetworkHQ != null && unit.NetworkHQ != aircraft.NetworkHQ)
					continue;
				float dist = Vector3.Distance(pos, bin.transform.position);
				if (dist >= bestDist)
					continue;
				Airbase home = null;
				if (unit != null)
					home = unit.GetAirbase() ?? unit.GetComponentInChildren<Airbase>(true);
				if (home == null)
					home = bin.GetComponentInParent<Airbase>();
				if (home == null || home.disabled)
					continue;
				bestDist = dist;
				best = home;
			}
			return best;
		}

		internal static Airbase ResolveSource(Aircraft aircraft)
		{
			if (aircraft == null)
				return null;
			Airbase pending = PendingSpawnAirbase;
			bool samePlayer = aircraft.Player != null && aircraft.Player.GetInstanceID() == PendingSpawnPlayerId;
			bool fresh = pending != null && Time.time - PendingSpawnTime < 20f;
			if (pending != null && (samePlayer || fresh) && SameFaction(aircraft, pending))
				return pending;
			Airbase stocked = ClosestStocked(aircraft);
			if (stocked != null)
				return stocked;
			return ClosestAirbase(aircraft);
		}

		private static bool SameFaction(Aircraft aircraft, Airbase airbase)
		{
			if (aircraft == null || airbase == null)
				return false;
			if (aircraft.NetworkHQ == null || airbase.CurrentHQ == null)
				return true;
			return airbase.CurrentHQ == aircraft.NetworkHQ;
		}

		internal static Airbase ClosestStocked(Aircraft aircraft)
		{
			Airbase[] all = UnityEngine.Object.FindObjectsOfType<Airbase>();
			Airbase best = null;
			float bestDist = float.MaxValue;
			Vector3 pos = aircraft.transform.position;
			for (int i = 0; i < all.Length; i++)
			{
				Airbase airbase = all[i];
				if (airbase == null || airbase.disabled || airbase.GetWarheads() <= 0 || !SameFaction(aircraft, airbase))
					continue;
				float dist = Vector3.Distance(pos, CenterOf(airbase));
				float radius = airbase.AttachedAirbase ? 1200f : Mathf.Max(airbase.GetRadius(), 500f);
				if (dist <= radius && dist < bestDist)
				{
					bestDist = dist;
					best = airbase;
				}
			}
			return best;
		}

		internal static Airbase ClosestAirbase(Aircraft aircraft)
		{
			Airbase[] all = UnityEngine.Object.FindObjectsOfType<Airbase>();
			Airbase best = null;
			float bestDist = float.MaxValue;
			Vector3 pos = aircraft.transform.position;
			for (int i = 0; i < all.Length; i++)
			{
				Airbase airbase = all[i];
				if (airbase == null || airbase.disabled || !Within(aircraft, airbase))
					continue;
				if (aircraft.NetworkHQ != null && airbase.CurrentHQ != null && airbase.CurrentHQ != aircraft.NetworkHQ)
					continue;
				float dist = Vector3.Distance(pos, CenterOf(airbase));
				if (dist < bestDist)
				{
					bestDist = dist;
					best = airbase;
				}
			}
			return best;
		}

		private static bool Within(Aircraft aircraft, Airbase airbase)
		{
			if (aircraft == null || airbase == null)
				return false;
			float radius = airbase.GetRadius();
			if (radius < 200f)
				radius = 2500f;
			return Vector3.Distance(aircraft.transform.position, CenterOf(airbase)) <= radius;
		}

		private static Vector3 CenterOf(Airbase airbase)
		{
			if (airbase.center != null)
				return airbase.center.position;
			return airbase.transform.position;
		}

		internal static string AirbaseName(Airbase airbase)
		{
			if (airbase == null)
				return "nowhere";
			if (airbase.TryGetAttachedUnit(out Unit unit) && unit != null && !string.IsNullOrEmpty(unit.unitName))
				return unit.unitName;
			if (Instance != null && Instance._airbaseUnit != null)
			{
				Unit reflected = Instance._airbaseUnit.GetValue(airbase) as Unit;
				if (reflected != null && !string.IsNullOrEmpty(reflected.unitName))
					return reflected.unitName;
			}
			Ship ship = airbase.GetComponentInParent<Ship>();
			if (ship != null && !string.IsNullOrEmpty(ship.unitName))
				return ship.unitName;
			return airbase.name;
		}
		internal static bool Filling;
		internal static bool BudgetReady;
		internal static bool SawWarheadCargo;
		internal static bool CommitSpawn;
		internal static int FillBudget;
		internal static int FilledTotal;
		internal static int StockAtSpawn;
		internal static readonly List<MountedCargo> PendingCargos = new List<MountedCargo>();
		internal static readonly Queue<int> DropFills = new Queue<int>();
		internal static readonly HashSet<int> ScaledIds = new HashSet<int>();
		internal static readonly HashSet<int> FiredIds = new HashSet<int>();
		private static FieldInfo _hardpointField;
		private static float _fullMass;

		internal static void DistributeFills()
		{
			if (PendingCargos.Count == 0)
				return;
			int remaining = FillBudget;
			FilledTotal = 0;
			for (int i = 0; i < PendingCargos.Count; i++)
			{
				MountedCargo cargo = PendingCargos[i];
				if (cargo == null)
					continue;
				int take = remaining > 0 ? Math.Min(WarheadsPerContainer, remaining) : 0;
				remaining -= take;
				FilledTotal += take;
				cargo.ammo = take;
				ScaleCarryMass(cargo, take);
			}
			FillBudget = remaining;
			PendingCargos.Clear();
		}

		internal static float FullMass()
		{
			if (_fullMass > 1f)
				return _fullMass;
			if (WarheadCargo != null && WarheadCargo.mass > 1f)
				_fullMass = WarheadCargo.mass;
			if (WarheadCargo != null && WarheadCargo.unitPrefab != null)
			{
				Unit unit = WarheadCargo.unitPrefab.GetComponent<Unit>();
				if (unit != null)
				{
					float prefabMass = unit.GetPrefabMass();
					if (prefabMass > _fullMass)
						_fullMass = prefabMass;
				}
			}
			if (_fullMass <= 1f)
				_fullMass = 6000f;
			return _fullMass;
		}

		internal static float ScaledMass(float full, int count)
		{
			count = Mathf.Clamp(count, 0, WarheadsPerContainer);
			float crate = full * 0.2f;
			return crate + (full - crate) * count / WarheadsPerContainer;
		}

		internal static void ScaleCarryMass(MountedCargo cargo, int count)
		{
			if (cargo == null || !ScaledIds.Add(cargo.GetInstanceID()))
				return;
			Hardpoint hardpoint = HardpointOf(cargo);
			if (hardpoint == null)
				return;
			float full = FullMass();
			hardpoint.ModifyMass(ScaledMass(full, count) - full);
		}

		internal static void RestoreCarryMass(MountedCargo cargo)
		{
			if (cargo == null || !ScaledIds.Remove(cargo.GetInstanceID()))
				return;
			Hardpoint hardpoint = HardpointOf(cargo);
			if (hardpoint == null)
				return;
			float full = FullMass();
			hardpoint.ModifyMass(full - ScaledMass(full, cargo.ammo));
		}

		private static Hardpoint HardpointOf(MountedCargo cargo)
		{
			if (_hardpointField == null)
				_hardpointField = AccessTools.Field(typeof(Weapon), "hardpoint");
			if (_hardpointField == null)
				return null;
			return _hardpointField.GetValue(cargo) as Hardpoint;
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
			int fill = WarheadsPerContainer;
			if (DropFills.Count > 0)
				fill = DropFills.Dequeue();
			float full = FullMass();
			float scale = full > 1f ? ScaledMass(full, fill) / full : 1f;
			for (int i = 0; i < parts.Length; i++)
			{
				if (scale < 0.99f)
				{
					parts[i].mass *= scale;
					if (parts[i].rb != null)
						parts[i].rb.mass = parts[i].mass;
				}
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
		private static void UseWarheadCargo(MountedCargo __instance, Aircraft aircraft, WeaponMount mount)
		{
			if (__instance == null || !Plugin.IsWarheadMount(mount))
				return;
			__instance.cargo = Plugin.WarheadCargo;
			__instance.info = Plugin.WarheadInfo;
			if (!Plugin.Filling)
			{
				__instance.ammo = Plugin.WarheadsPerContainer;
				return;
			}
			Plugin.SawWarheadCargo = true;
			Plugin.EnsureBudget(aircraft);
			Plugin.PendingCargos.Add(__instance);
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
		[HarmonyPatch(typeof(WeaponChecker), nameof(WeaponChecker.MountAllowedNuclear))]
		private static void SeeShipBunkers(Airbase airbase)
		{
			if (airbase != null)
				Plugin.AdoptStores(airbase);
		}

		[HarmonyPostfix]
		[HarmonyPatch(typeof(WeaponChecker), nameof(WeaponChecker.MountAllowedNuclear))]
		private static void AllowPartialFill(WeaponMount mount, HardpointSet hardpointSet, Airbase airbase, Player player, ref bool __result)
		{
			if (!Plugin.IsWarheadMount(mount) || airbase == null)
				return;
			int rank = player != null ? player.PlayerRank : 0;
			if (!MissionManager.AllowTactical() || (float)rank < NetworkSceneSingleton<MissionManager>.i.tacticalMinRank)
			{
				__result = false;
				return;
			}
			if (mount.info != null && mount.info.strategic && (!MissionManager.AllowStrategic() || (float)rank < NetworkSceneSingleton<MissionManager>.i.strategicMinRank))
			{
				__result = false;
				return;
			}
			int need = hardpointSet != null && hardpointSet.hardpoints != null ? hardpointSet.hardpoints.Count : 1;
			if (need < 1)
				need = 1;
			__result = Plugin.CountStock(airbase) >= need;
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
			Aircraft aircraft = Plugin.AircraftOf(__instance);
			if (aircraft == null || aircraft.weaponStations == null)
				return;
			int hidden = 0;
			for (int i = 0; i < aircraft.weaponStations.Count; i++)
			{
				WeaponStation station = aircraft.weaponStations[i];
				if (station == null || station.WeaponInfo == null || !station.WeaponInfo.nuclear)
					continue;
				int ours = 0;
				bool stationIsOurs = station.WeaponInfo == Plugin.WarheadInfo || station.WeaponInfo.weaponName == "Warhead Container";
				if (station.Weapons != null)
				{
					for (int w = 0; w < station.Weapons.Count; w++)
					{
						Weapon weapon = station.Weapons[w];
						if (weapon != null && weapon.info == Plugin.WarheadInfo)
							ours += Math.Max(0, weapon.ammo);
					}
				}
				if (stationIsOurs)
					ours = Math.Max(ours, station.Ammo);
				if (ours <= 0)
					continue;
				hidden += Math.Min(ours, station.Ammo);
			}
			if (hidden <= 0)
				return;
			__result -= hidden;
			if (__result < 0)
				__result = 0;
		}

		[HarmonyPrefix]
		[HarmonyPatch(typeof(WeaponChecker), nameof(WeaponChecker.VetLoadout))]
		private static void KeepIbisOption(AircraftDefinition definition, Player player, Airbase airbase)
		{
			if (player != null && airbase != null)
			{
				Plugin.PendingSpawnAirbase = airbase;
				Plugin.PendingSpawnPlayerId = player.GetInstanceID();
				Plugin.PendingSpawnTime = Time.time;
			}
			if (Plugin.Instance == null || definition == null)
				return;
			Plugin.Instance.EnsureWeapons();
		}

		[HarmonyPrefix]
		[HarmonyPatch(typeof(WeaponManager), nameof(WeaponManager.SpawnWeapons))]
		private static void NoteOriginBefore(WeaponManager __instance, out int __state)
		{
			__state = 0;
			Plugin.Filling = true;
			Plugin.BudgetReady = false;
			Plugin.SawWarheadCargo = false;
			Plugin.CommitSpawn = false;
			Plugin.FilledTotal = 0;
			Plugin.StockAtSpawn = 0;
			Plugin.SuppressWarheadCount = false;
			Plugin.PendingCargos.Clear();
			Plugin.FillSource = null;
			Plugin.LoadedBefore = 0;
			if (__instance == null)
				return;
			Aircraft aircraft = Plugin.AircraftOf(__instance);
			Plugin.LoadedBefore = Plugin.LoadedWarheads(aircraft);
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
			int filled = Plugin.FilledTotal;
			bool saw = Plugin.SawWarheadCargo;
			Airbase source = Plugin.FillSource;
			int stock = Plugin.StockAtSpawn;
			bool commit = Plugin.CommitSpawn;
			Plugin.SuppressWarheadCount = false;
			Plugin.Filling = false;
			Plugin.BudgetReady = false;
			Plugin.SawWarheadCargo = false;
			Plugin.CommitSpawn = false;
			Plugin.PendingCargos.Clear();
			if (!saw || aircraft == null || Plugin.Instance == null)
				return;
			if (!commit)
				return;
			int paid = Math.Max(0, Plugin.LoadedBefore);
			int newly = filled - paid;
			if (newly > 0 && source != null)
			{
				int fromBunker = Math.Min(newly, stock);
				int removed = fromBunker > 0 ? Plugin.TakeFrom(source, fromBunker) : 0;
				Plugin.Instance.RememberOrigin(aircraft, source);
				Plugin.Instance.Note("Loaded " + filled + " warheads and removed " + removed + " from " + Plugin.AirbaseName(source) + ". " + Plugin.CountStock(source) + " remain.");
				return;
			}
			if (newly < 0 && source != null)
			{
				source.AddWarheads(-newly);
				if (filled > 0)
					Plugin.Instance.RememberOrigin(aircraft, source);
				else
					Plugin.Instance.ForgetOrigin(aircraft);
				Plugin.Instance.Note("Returned " + (-newly) + " warheads to " + Plugin.AirbaseName(source) + ". " + Plugin.CountStock(source) + " remain.");
				return;
			}
			if (filled > 0 && source != null)
				Plugin.Instance.RememberOrigin(aircraft, source);
			if (filled <= 0)
				Plugin.Instance.Note("No warhead stock found at " + Plugin.AirbaseName(source) + ". Containers left empty.");
		}

		[HarmonyPostfix]
		[HarmonyPatch(typeof(WeaponManager), nameof(WeaponManager.SpawnWeapons))]
		private static void StopFilling()
		{
			Plugin.Filling = false;
			Plugin.PendingCargos.Clear();
		}

		[HarmonyPrefix]
		[HarmonyPatch(typeof(WeaponStation), nameof(WeaponStation.AccountAmmo))]
		private static void FillBeforeAmmoCount()
		{
			if (!Plugin.Filling || Plugin.PendingCargos.Count == 0)
				return;
			Plugin.DistributeFills();
		}

		[HarmonyPostfix]
		[HarmonyPatch(typeof(WeaponStation), nameof(WeaponStation.AccountAmmo))]
		private static void HideFillFromVanillaCount()
		{
			if (Plugin.Filling)
				Plugin.SuppressWarheadCount = true;
		}

		[HarmonyPostfix]
		[HarmonyPatch(typeof(MountedCargo), nameof(MountedCargo.GetMass))]
		private static void WeightFollowsFill(MountedCargo __instance, ref float __result)
		{
			if (__instance == null || __instance.info != Plugin.WarheadInfo || __instance.cargo != Plugin.WarheadCargo)
				return;
			if (Plugin.FiredIds.Contains(__instance.GetInstanceID()))
			{
				__result = 0f;
				return;
			}
			__result = Plugin.ScaledMass(Plugin.FullMass(), __instance.ammo);
		}

		[HarmonyPrefix]
		[HarmonyPatch(typeof(MountedCargo), "Fire")]
		private static void RememberDropFill(MountedCargo __instance)
		{
			if (__instance == null || __instance.info != Plugin.WarheadInfo)
				return;
			Plugin.DropFills.Enqueue(Mathf.Clamp(__instance.ammo, 0, Plugin.WarheadsPerContainer));
			Plugin.FiredIds.Add(__instance.GetInstanceID());
		}

		[HarmonyPrefix]
		[HarmonyPatch(typeof(MountedCargo), "OnDestroy")]
		private static void UnscaleOnRemove(MountedCargo __instance)
		{
			if (__instance == null || __instance.info != Plugin.WarheadInfo)
				return;
			int id = __instance.GetInstanceID();
			if (Plugin.FiredIds.Remove(id))
			{
				Plugin.ScaledIds.Remove(id);
				return;
			}
			Plugin.RestoreCarryMass(__instance);
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
			int warheads = Plugin.LoadedWarheads(aircraft);
			if (warheads <= 0)
				return;
			if (Plugin.CanStore(destination))
			{
				destination.AddWarheads(warheads);
				Plugin.Instance.ForgetOrigin(aircraft);
				Plugin.Instance.Note("Delivered " + warheads + " warheads to " + Plugin.AirbaseName(destination) + ".");
				return;
			}
			if (!Plugin.Instance.TryOrigin(aircraft, out Airbase origin))
				return;
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
