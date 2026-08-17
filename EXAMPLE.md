# ShellLang 0.1 Map Bootstrap Example

This example is the first script that runs after a game host loads a map. It prepares the world, spawns every configured monster, chooses a random player spawn, equips the player, and starts the encounter.

The host must register every referenced global, type, member, enum, and command. The main globals are `map : GameMap`, `world : GameWorld`, `local_player : Player`, `game_rules : GameRules`, `encounter_director : EncounterDirector`, and `navigation : NavigationSystem`.

`find_entities(classname:)` returns `Array<MapMarker>` for every classname. This broad return type is an intentional host tradeoff for this example.

Marker-specific commands validate `MapMarker.classname`. A mismatch returns the declared `GAME1001` runtime fault and aborts the compilation.

Default-input mutation commands return their primary input on success. This fluent rule makes a lifted mutation return `Result<Array<T>,E>` instead of `Result<Void,E>`.

Zero-input effects return `Result<Void,E>`. The script uses `require` when a failure must stop map startup.

Seed arguments are channel seeds. Per-entity commands derive a local seed from the channel seed and the entity's stable identity.

The fenced script contains 280 physical lines.

## Script

```shelllang
# First map compilation.
# A runtime fault or host fault aborts the remaining bootstrap.
map_name = map.name
map_seed = map.seed
map_difficulty = game_rules.difficulty
map_name -> system::print
map_difficulty -> system::print
world::set_loading_stage(name: "prepare_world") -> require
world -> world::pause_simulation(reason: MapBootstrap) -> require
world -> world::disable_player_input -> require
world -> world::clear_transient_entities(keep_players: true) -> require
world -> world::set_time_scale(scale: 1.0) -> require
world -> world::set_gravity(scale: 1.0) -> require
game_rules -> world::set_friendly_fire(enabled: false) -> require
game_rules -> world::set_respawn_policy(policy: Checkpoint) -> require
game_rules -> world::set_monster_scaling(difficulty: map_difficulty) -> require
# Give each random system a stable channel from the map seed.
player_spawn_seed = map::derive_seed(
    base: map_seed,
    channel: "player_spawn"
)
monster_ai_seed = map::derive_seed(
    base: map_seed,
    channel: "monster_ai"
)
loot_seed = map::derive_seed(
    base: map_seed,
    channel: "loot"
)
ambient_seed = map::derive_seed(
    base: map_seed,
    channel: "ambient"
)
weather_seed = map::derive_seed(
    base: map_seed,
    channel: "weather"
)
# Read the map editor entities.
monster_spawn_locations = map::find_entities(
    classname: "info_monster_spawn"
)
player_spawn_locations = map::find_entities(
    classname: "info_spawn"
)
loot_spawn_locations = map::find_entities(
    classname: "info_loot_spawn"
)
checkpoint_locations = map::find_entities(
    classname: "info_checkpoint"
)
patrol_locations = map::find_entities(
    classname: "info_patrol"
)
ambient_locations = map::find_entities(
    classname: "info_ambient"
)
monster_spawn_count = monster_spawn_locations -> count
player_spawn_count = player_spawn_locations -> count
loot_spawn_count = loot_spawn_locations -> count
monster_spawn_count -> system::print
player_spawn_count -> system::print
loot_spawn_count -> system::print
player_spawn_locations
    -> map::validate_player_spawns(navigation <- navigation)
    -> require
monster_spawn_locations
    -> map::validate_monster_spawns(navigation <- navigation)
    -> require
# validate_navigation returns a multi-output record.
navigation_report = navigation
    -> map::validate_navigation(map <- map)
    -> require
navigation_report.summary -> system::print
navigation_report.is_complete
    -> system::require_true(message: "Map navigation is incomplete.")
    -> require
navigation -> map::activate_navigation(map <- map) -> require
# Select one random info_spawn. The command consumes the array once.
chosen_player_spawn = player_spawn_locations
    -> map::choose_random_spawn(seed: player_spawn_seed)
    -> require
chosen_player_spawn.name -> system::print
chosen_player_spawn.position -> system::print
# Spawn every info_monster_spawn marker through scalar lifting.
ordered_monster_spawns = monster_spawn_locations
    -> sort(by: .spawn_order)
spawned_monsters = ordered_monster_spawns
    -> encounter::spawn_monster(
        director <- encounter_director,
        difficulty: map_difficulty,
        faction: Hostile,
        reason: MapStart,
        seed: monster_ai_seed,
        start_awake: false
    )
    -> require
spawned_monster_count = spawned_monsters -> count
spawned_monster_count -> system::print
spawned_monsters
    -> encounter::attach_to_encounter(director <- encounter_director)
    -> require
spawned_monsters
    -> encounter::initialize_monster_ai(
        navigation <- navigation,
        seed: monster_ai_seed
    )
    -> require
spawned_monsters
    -> encounter::assign_patrol_routes(
        routes <- patrol_locations,
        seed: monster_ai_seed
    )
    -> require
# Contextual predicates run once per spawned monster.
boss_monsters = spawned_monsters -> where(.rank == Boss)
elite_monsters = spawned_monsters -> where(.rank == Elite)
ambush_monsters = spawned_monsters -> where(.group == "ambush")
guard_monsters = spawned_monsters -> where(.group == "guard")
boss_monsters
    -> encounter::set_health_multiplier(multiplier: 1.5)
    -> require
boss_monsters
    -> encounter::set_damage_multiplier(multiplier: 1.25)
    -> require
elite_monsters
    -> encounter::give_monster_armor(amount: 50)
    -> require
ambush_monsters
    -> encounter::set_monster_dormant(dormant: true)
    -> require
guard_monsters
    -> encounter::set_guard_radius(radius: 12.0)
    -> require
# Spawn authored loot and register checkpoints.
spawned_loot = loot_spawn_locations
    -> encounter::spawn_loot(difficulty: map_difficulty, seed: loot_seed)
    -> require
spawned_loot -> count -> system::print
checkpoint_locations
    -> map::register_checkpoint(game_rules <- game_rules)
    -> require
first_checkpoint = checkpoint_locations
    -> sort(by: .spawn_order)
    -> first
    -> require
game_rules
    -> map::set_initial_checkpoint(checkpoint <- first_checkpoint)
    -> require
# Spawn the player. The command returns player and camera outputs.
player_spawn_output = chosen_player_spawn
    -> player::spawn_player(
        player <- local_player,
        world <- world,
        facing: MarkerAngles,
        protection_seconds: 3.0
    )
    -> require
spawned_player = player_spawn_output.player
player_camera = player_spawn_output.camera
spawned_player.name -> system::print
spawned_player.position -> system::print
player_camera -> player::set_camera_mode(mode: FirstPerson) -> require
player_camera -> player::fade_from_black(seconds: 1.25) -> require
# Configure the player only through registered commands.
spawned_player -> player::set_max_health(amount: 100) -> require
spawned_player -> player::heal(amount: 100) -> require
spawned_player -> player::set_max_armor(amount: 100) -> require
spawned_player -> player::give_armor(amount: 25) -> require
spawned_player -> inventory::set_inventory_capacity(slots: 32) -> require
spawned_player -> inventory::give_credits(amount: 250) -> require
# Lift over Weapon values and reuse the player port each time.
starter_weapons = [
    Weapon.Crowbar,
    Weapon.Pistol,
    Weapon.Shotgun,
    Weapon.SubmachineGun,
    Weapon.Crossbow
]
starter_weapons
    -> inventory::grant_weapon_to(
        player <- spawned_player,
        condition: 1.0,
        source: MapLoadout
    )
    -> require
spawned_player -> inventory::give_ammo(ammo: PistolRounds, amount: 90) -> require
spawned_player -> inventory::give_ammo(ammo: ShotgunShells, amount: 24) -> require
spawned_player -> inventory::give_ammo(ammo: SmgRounds, amount: 150) -> require
spawned_player -> inventory::give_ammo(ammo: CrossbowBolts, amount: 12) -> require
spawned_player -> inventory::equip_weapon(weapon: Pistol) -> require
# Grant tools and mission supplies through the same lifting rule.
starter_items = [
    Item.Flashlight,
    Item.Binoculars,
    Item.MapScanner,
    Item.RepairTool,
    Item.FieldRadio,
    Item.AccessCardBlue
]
starter_items
    -> inventory::grant_item_to(
        player <- spawned_player,
        count: 1,
        source: MapLoadout
    )
    -> require
spawned_player -> inventory::give_item(item: Medkit, count: 3) -> require
spawned_player -> inventory::give_item(item: ArmorBattery, count: 2) -> require
spawned_player -> inventory::give_item(item: HandGrenade, count: 4) -> require
spawned_player -> inventory::give_item(item: ProximityMine, count: 2) -> require
spawned_player -> inventory::give_item(item: EmergencyBeacon, count: 1) -> require
# Create mission objectives.
game_rules -> objectives::clear_objectives -> require
game_rules
    -> objectives::add_objective(
        id: "reach_control_room",
        title: "Reach the control room",
        priority: Primary
    )
    -> require
game_rules
    -> objectives::add_objective(
        id: "restore_power",
        title: "Restore auxiliary power",
        priority: Primary
    )
    -> require
game_rules
    -> objectives::add_objective(
        id: "find_survivors",
        title: "Search for survivors",
        priority: Optional
    )
    -> require
game_rules -> objectives::activate_objective(id: "reach_control_room") -> require
# Build the map atmosphere.
ambient_emitters = ambient_locations
    -> audio::spawn_ambient_emitter(seed: ambient_seed)
    -> require
ambient_emitters
    -> audio::start_ambient_emitter(fade_seconds: 2.0)
    -> require
weather_profile = map
    -> world::choose_weather(seed: weather_seed)
    -> require
world -> world::apply_weather(profile <- weather_profile) -> require
audio::play_music(
    track: "music/map_start_tension",
    fade_seconds: 2.5,
    loop: true
)
    -> require
# Arm the encounter and release the player.
encounter_director
    -> encounter::set_player(player <- spawned_player)
    -> require
encounter_director
    -> encounter::set_difficulty(difficulty: map_difficulty)
    -> require
encounter_director
    -> encounter::arm_encounter(monsters <- spawned_monsters)
    -> require
guard_monsters -> encounter::wake_monster(reason: PlayerSpawned) -> require
world -> world::enable_player_input -> require
world -> world::resume_simulation(reason: MapBootstrapComplete) -> require
spawned_player
    -> player::show_message(
        text: "Find the control room. Stay alert.",
        duration_seconds: 5.0
    )
    -> require
world::set_loading_stage(name: "ready") -> require
final_loot_count = spawned_loot -> count
telemetry::log_map_started(
    player <- spawned_player,
    map_name: map_name,
    monster_count: spawned_monster_count,
    loot_count: final_loot_count
)
    -> require
```

The example uses no explicit general-purpose control flow. Array lifting and collection intrinsics provide iteration.

Result propagation conditionally skips operations after an `Err`. Descriptor types decide whether an operation consumes a complete array or maps over elements.

## Assumed host descriptor catalog

This catalog is normative for this example only. It is not part of the ShellLang standard library.

The signature tables use `default` for a default input port. They use `<-` for additional input ports and `:` for arguments.

### Globals

| Name | Type |
| --- | --- |
| `map` | `GameMap` |
| `world` | `GameWorld` |
| `local_player` | `Player` |
| `game_rules` | `GameRules` |
| `encounter_director` | `EncounterDirector` |
| `navigation` | `NavigationSystem` |

### Registered members and output records

| Receiver | Member | Type |
| --- | --- | --- |
| `GameMap` | `name` | `String` |
| `GameMap` | `seed` | `UInt64` |
| `GameRules` | `difficulty` | `Difficulty` |
| `MapMarker` | `classname` | `String` |
| `MapMarker` | `name` | `String` |
| `MapMarker` | `position` | `Vector3` |
| `MapMarker` | `spawn_order` | `Int32` |
| `MapMarker` | `stable_id` | `UInt64` |
| `Monster` | `rank` | `MonsterRank` |
| `Monster` | `group` | `String` |
| `Monster` | `stable_id` | `UInt64` |
| `Player` | `name` | `String` |
| `Player` | `position` | `Vector3` |
| `ValidateNavigation.Output` | `summary` | `String` |
| `ValidateNavigation.Output` | `is_complete` | `Bool` |
| `SpawnPlayer.Output` | `player` | `Player` and default output |
| `SpawnPlayer.Output` | `camera` | `Camera` |

`ValidateNavigation.Output` has no default output. Both output records are nominal and immutable.

### Enums

| Type | Members used by the script |
| --- | --- |
| `Difficulty` | Supplied by `game_rules.difficulty` |
| `WorldTransitionReason` | `MapBootstrap`, `MapBootstrapComplete` |
| `RespawnPolicy` | `Checkpoint` |
| `MonsterFaction` | `Hostile` |
| `SpawnReason` | `MapStart` |
| `MonsterRank` | `Boss`, `Elite` |
| `SpawnFacing` | `MarkerAngles` |
| `CameraMode` | `FirstPerson` |
| `GrantSource` | `MapLoadout` |
| `Weapon` | `Crowbar`, `Pistol`, `Shotgun`, `SubmachineGun`, `Crossbow` |
| `AmmoType` | `PistolRounds`, `ShotgunShells`, `SmgRounds`, `CrossbowBolts` |
| `Item` | `Flashlight`, `Binoculars`, `MapScanner`, `RepairTool`, `FieldRadio`, `AccessCardBlue`, `Medkit`, `ArmorBattery`, `HandGrenade`, `ProximityMine`, `EmergencyBeacon` |
| `ObjectivePriority` | `Primary`, `Optional` |
| `WakeReason` | `PlayerSpawned` |

### Error types and runtime faults

The example uses this single-parent error hierarchy:

```text
Error
└── GameError
    ├── WorldError
    ├── MapError
    │   ├── NavigationError
    │   └── SpawnError
    ├── EncounterError
    ├── PlayerError
    │   └── InventoryError
    ├── ObjectiveError
    ├── AudioError
    ├── UIError
    └── TelemetryError
```

Core `EmptyCollectionError` and `CollectionCardinalityError` derive directly from `Error`. The `first` and `single` intrinsics use them respectively.

The host also registers this runtime fault:

| Code | Name | Meaning |
| --- | --- | --- |
| `GAME1001` | `MapMarkerKindMismatch` | A marker-specific command received the wrong `MapMarker.classname`. |

An empty valid marker array is not a kind mismatch. Commands use typed errors for expected failures such as no available player spawn.

### Core intrinsics used

| Intrinsic | Signature |
| --- | --- |
| `where` | `Array<T>, predicate: T -> Bool -> Array<T>` |
| `sort` | `Array<T>, by: T -> K -> Array<T>` |
| `count` | `Array<T> -> Int32` |
| `first` | `Array<T> -> Result<T,EmptyCollectionError>` |
| `require` | `Result<T,E> -> T` |

### Bootstrap and world commands

| Command | Default input | Explicit inputs | Arguments | Output |
| --- | --- | --- | --- | --- |
| `print` | `Any` | — | — | `Void` |
| `set_loading_stage` | — | — | `name: String` | `Result<Void,WorldError>` |
| `derive_seed` | — | — | `base: UInt64`, `channel: String` | `UInt64` |
| `find_entities` | — | — | `classname: String` | `Array<MapMarker>` |
| `pause_simulation` | `GameWorld` | — | `reason: WorldTransitionReason` | `Result<GameWorld,WorldError>` |
| `disable_player_input` | `GameWorld` | — | — | `Result<GameWorld,WorldError>` |
| `enable_player_input` | `GameWorld` | — | — | `Result<GameWorld,WorldError>` |
| `resume_simulation` | `GameWorld` | — | `reason: WorldTransitionReason` | `Result<GameWorld,WorldError>` |
| `clear_transient_entities` | `GameWorld` | — | `keep_players: Bool` | `Result<GameWorld,WorldError>` |
| `set_time_scale` | `GameWorld` | — | `scale: Float32` | `Result<GameWorld,WorldError>` |
| `set_gravity` | `GameWorld` | — | `scale: Float32` | `Result<GameWorld,WorldError>` |
| `set_friendly_fire` | `GameRules` | — | `enabled: Bool` | `Result<GameRules,WorldError>` |
| `set_respawn_policy` | `GameRules` | — | `policy: RespawnPolicy` | `Result<GameRules,WorldError>` |
| `set_monster_scaling` | `GameRules` | — | `difficulty: Difficulty` | `Result<GameRules,WorldError>` |

### Navigation and marker commands

| Command | Default input | Explicit inputs | Arguments | Output |
| --- | --- | --- | --- | --- |
| `validate_player_spawns` | `Array<MapMarker>` | `navigation <- NavigationSystem` | — | `Result<Array<MapMarker>,NavigationError>` |
| `validate_monster_spawns` | `Array<MapMarker>` | `navigation <- NavigationSystem` | — | `Result<Array<MapMarker>,NavigationError>` |
| `validate_navigation` | `NavigationSystem` | `map <- GameMap` | — | `Result<ValidateNavigation.Output,NavigationError>` |
| `require_true` | `Bool` | — | `message: String` | `Result<Bool,MapError>` |
| `activate_navigation` | `NavigationSystem` | `map <- GameMap` | — | `Result<NavigationSystem,NavigationError>` |
| `choose_random_spawn` | `Array<MapMarker>` | — | `seed: UInt64` | `Result<MapMarker,SpawnError>` |

`validate_player_spawns`, `validate_monster_spawns`, and `choose_random_spawn` declare `GAME1001`.

### Monster commands

| Command | Default input | Explicit inputs | Arguments | Output |
| --- | --- | --- | --- | --- |
| `spawn_monster` | `MapMarker` | `director <- EncounterDirector` | `difficulty: Difficulty`, `faction: MonsterFaction`, `reason: SpawnReason`, `seed: UInt64`, `start_awake: Bool` | `Result<Monster,SpawnError>` |
| `attach_to_encounter` | `Monster` | `director <- EncounterDirector` | — | `Result<Monster,EncounterError>` |
| `initialize_monster_ai` | `Monster` | `navigation <- NavigationSystem` | `seed: UInt64` | `Result<Monster,EncounterError>` |
| `assign_patrol_routes` | `Monster` | `routes <- Array<MapMarker>` | `seed: UInt64` | `Result<Monster,EncounterError>` |
| `set_health_multiplier` | `Monster` | — | `multiplier: Float32` | `Result<Monster,EncounterError>` |
| `set_damage_multiplier` | `Monster` | — | `multiplier: Float32` | `Result<Monster,EncounterError>` |
| `give_monster_armor` | `Monster` | — | `amount: Int32` | `Result<Monster,EncounterError>` |
| `set_monster_dormant` | `Monster` | — | `dormant: Bool` | `Result<Monster,EncounterError>` |
| `set_guard_radius` | `Monster` | — | `radius: Float32` | `Result<Monster,EncounterError>` |
| `wake_monster` | `Monster` | — | `reason: WakeReason` | `Result<Monster,EncounterError>` |

`spawn_monster` declares `GAME1001` for its default marker. `assign_patrol_routes` declares it for each route marker.

`assign_patrol_routes` is scalar over `Monster`. Array lifting reuses the complete route array and channel seed for each monster.

### Loot and checkpoint commands

| Command | Default input | Explicit inputs | Arguments | Output |
| --- | --- | --- | --- | --- |
| `spawn_loot` | `MapMarker` | — | `difficulty: Difficulty`, `seed: UInt64` | `Result<Loot,SpawnError>` |
| `register_checkpoint` | `MapMarker` | `game_rules <- GameRules` | — | `Result<MapMarker,MapError>` |
| `set_initial_checkpoint` | `GameRules` | `checkpoint <- MapMarker` | — | `Result<GameRules,MapError>` |

All three commands declare `GAME1001` for their marker input.

### Player, camera, and inventory commands

| Command | Default input | Explicit inputs | Arguments | Output |
| --- | --- | --- | --- | --- |
| `spawn_player` | `MapMarker` | `player <- Player`, `world <- GameWorld` | `facing: SpawnFacing`, `protection_seconds: Float32` | `Result<SpawnPlayer.Output,SpawnError>` |
| `set_camera_mode` | `Camera` | — | `mode: CameraMode` | `Result<Camera,PlayerError>` |
| `fade_from_black` | `Camera` | — | `seconds: Float32` | `Result<Camera,PlayerError>` |
| `set_max_health` | `Player` | — | `amount: Int32` | `Result<Player,PlayerError>` |
| `heal` | `Player` | — | `amount: Int32` | `Result<Player,PlayerError>` |
| `set_max_armor` | `Player` | — | `amount: Int32` | `Result<Player,PlayerError>` |
| `give_armor` | `Player` | — | `amount: Int32` | `Result<Player,PlayerError>` |
| `set_inventory_capacity` | `Player` | — | `slots: Int32` | `Result<Player,InventoryError>` |
| `give_credits` | `Player` | — | `amount: Int32` | `Result<Player,InventoryError>` |
| `grant_weapon_to` | `Weapon` | `player <- Player` | `condition: Float32`, `source: GrantSource` | `Result<Weapon,InventoryError>` |
| `give_ammo` | `Player` | — | `ammo: AmmoType`, `amount: Int32` | `Result<Player,InventoryError>` |
| `equip_weapon` | `Player` | — | `weapon: Weapon` | `Result<Player,InventoryError>` |
| `grant_item_to` | `Item` | `player <- Player` | `count: Int32`, `source: GrantSource` | `Result<Item,InventoryError>` |
| `give_item` | `Player` | — | `item: Item`, `count: Int32` | `Result<Player,InventoryError>` |
| `show_message` | `Player` | — | `text: String`, `duration_seconds: Float32` | `Result<Player,UIError>` |

`spawn_player` declares `GAME1001` for its default marker.

### Objectives, atmosphere, and encounter commands

| Command | Default input | Explicit inputs | Arguments | Output |
| --- | --- | --- | --- | --- |
| `clear_objectives` | `GameRules` | — | — | `Result<GameRules,ObjectiveError>` |
| `add_objective` | `GameRules` | — | `id: String`, `title: String`, `priority: ObjectivePriority` | `Result<GameRules,ObjectiveError>` |
| `activate_objective` | `GameRules` | — | `id: String` | `Result<GameRules,ObjectiveError>` |
| `spawn_ambient_emitter` | `MapMarker` | — | `seed: UInt64` | `Result<AmbientEmitter,SpawnError>` |
| `start_ambient_emitter` | `AmbientEmitter` | — | `fade_seconds: Float32` | `Result<AmbientEmitter,AudioError>` |
| `choose_weather` | `GameMap` | — | `seed: UInt64` | `Result<WeatherProfile,WorldError>` |
| `apply_weather` | `GameWorld` | `profile <- WeatherProfile` | — | `Result<GameWorld,WorldError>` |
| `play_music` | — | — | `track: String`, `fade_seconds: Float32`, `loop: Bool` | `Result<Void,AudioError>` |
| `set_player` | `EncounterDirector` | `player <- Player` | — | `Result<EncounterDirector,EncounterError>` |
| `set_difficulty` | `EncounterDirector` | — | `difficulty: Difficulty` | `Result<EncounterDirector,EncounterError>` |
| `arm_encounter` | `EncounterDirector` | `monsters <- Array<Monster>` | — | `Result<EncounterDirector,EncounterError>` |
| `log_map_started` | — | `player <- Player` | `map_name: String`, `monster_count: Int32`, `loot_count: Int32` | `Result<Void,TelemetryError>` |

`spawn_ambient_emitter` declares `GAME1001` for its default marker.

### Marker preconditions

| Command | Required classname |
| --- | --- |
| `validate_player_spawns` | Every marker is `info_spawn` |
| `validate_monster_spawns` | Every marker is `info_monster_spawn` |
| `choose_random_spawn` | Every marker is `info_spawn` |
| `spawn_monster` | `info_monster_spawn` |
| `assign_patrol_routes` | Every route is `info_patrol` |
| `spawn_loot` | `info_loot_spawn` |
| `register_checkpoint` | `info_checkpoint` |
| `set_initial_checkpoint` | `info_checkpoint` |
| `spawn_player` | `info_spawn` |
| `spawn_ambient_emitter` | `info_ambient` |

A violation returns `CommandOutcome.Fault(GAME1001, safe_message)`. An unexpected invoker exception remains a host fault.

### Seed derivation

`choose_random_spawn` and `choose_weather` consume their channel seeds once.

Each lifted per-entity command computes its local seed as `hash(channel_seed, stable_id)`. It never initializes every entity with the unchanged channel seed.

`spawn_monster`, `spawn_loot`, and `spawn_ambient_emitter` use `MapMarker.stable_id`. Monster AI and patrol commands use `Monster.stable_id`.
