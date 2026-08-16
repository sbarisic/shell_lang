# ShellLang 0.1 Map Bootstrap Example

This example is the first script that runs after a game host loads a map. It prepares the world, spawns every configured monster, chooses a random player spawn, equips the player, and starts the encounter.

The host must register every referenced global, type, member, enum, and command. The main globals are `map : GameMap`, `world : GameWorld`, `local_player : Player`, `game_rules : GameRules`, `encounter_director : EncounterDirector`, and `navigation : NavigationSystem`.

`find_entities(classname:)` returns map markers. `spawn_monster` accepts one marker, so its pipeline maps over all `info_monster_spawn` locations. `choose_random_spawn` accepts the complete marker array, so it runs once.

State-changing commands return typed Results in this host. The script uses `require` when a failure must stop map startup. The fenced script contains 280 physical lines.

## Script

```text
# First map compilation.
# A runtime fault or host fault aborts the remaining bootstrap.
map_name = map.name
map_seed = map.seed
map_difficulty = game_rules.difficulty
map_name -> print
map_difficulty -> print
set_loading_stage(name: "prepare_world") -> require
world -> pause_simulation(reason: MapBootstrap) -> require
world -> disable_player_input -> require
world -> clear_transient_entities(keep_players: true) -> require
world -> set_time_scale(scale: 1.0) -> require
world -> set_gravity(scale: 1.0) -> require
game_rules -> set_friendly_fire(enabled: false) -> require
game_rules -> set_respawn_policy(policy: Checkpoint) -> require
game_rules -> set_monster_scaling(difficulty: map_difficulty) -> require
# Give each random system a stable channel from the map seed.
player_spawn_seed = derive_seed(
    base: map_seed,
    channel: "player_spawn"
)
monster_ai_seed = derive_seed(
    base: map_seed,
    channel: "monster_ai"
)
loot_seed = derive_seed(
    base: map_seed,
    channel: "loot"
)
ambient_seed = derive_seed(
    base: map_seed,
    channel: "ambient"
)
weather_seed = derive_seed(
    base: map_seed,
    channel: "weather"
)
# Read the map editor entities.
monster_spawn_locations = find_entities(
    classname: "info_monster_spawn"
)
player_spawn_locations = find_entities(
    classname: "info_spawn"
)
loot_spawn_locations = find_entities(
    classname: "info_loot_spawn"
)
checkpoint_locations = find_entities(
    classname: "info_checkpoint"
)
patrol_locations = find_entities(
    classname: "info_patrol"
)
ambient_locations = find_entities(
    classname: "info_ambient"
)
monster_spawn_count = monster_spawn_locations -> count
player_spawn_count = player_spawn_locations -> count
loot_spawn_count = loot_spawn_locations -> count
monster_spawn_count -> print
player_spawn_count -> print
loot_spawn_count -> print
player_spawn_locations
    -> validate_player_spawns(navigation <- navigation)
    -> require
monster_spawn_locations
    -> validate_monster_spawns(navigation <- navigation)
    -> require
# validate_navigation returns a multi-output record.
navigation_report = navigation
    -> validate_navigation(map <- map)
    -> require
navigation_report.summary -> print
navigation_report.is_complete
    -> require_true(message: "Map navigation is incomplete.")
    -> require
navigation -> activate_navigation(map <- map) -> require
# Select one random info_spawn. The command consumes the array once.
chosen_player_spawn = player_spawn_locations
    -> choose_random_spawn(seed: player_spawn_seed)
    -> require
chosen_player_spawn.name -> print
chosen_player_spawn.position -> print
# Spawn every info_monster_spawn marker through scalar lifting.
ordered_monster_spawns = monster_spawn_locations
    -> sort(by: .spawn_order)
spawned_monsters = ordered_monster_spawns
    -> spawn_monster(
        director <- encounter_director,
        difficulty: map_difficulty,
        faction: Hostile,
        reason: MapStart,
        seed: monster_ai_seed,
        start_awake: false
    )
    -> require
spawned_monster_count = spawned_monsters -> count
spawned_monster_count -> print
spawned_monsters
    -> attach_to_encounter(director <- encounter_director)
    -> require
spawned_monsters
    -> initialize_monster_ai(
        navigation <- navigation,
        seed: monster_ai_seed
    )
    -> require
spawned_monsters
    -> assign_patrol_routes(
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
    -> set_health_multiplier(multiplier: 1.5)
    -> require
boss_monsters
    -> set_damage_multiplier(multiplier: 1.25)
    -> require
elite_monsters
    -> give_monster_armor(amount: 50)
    -> require
ambush_monsters
    -> set_monster_dormant(dormant: true)
    -> require
guard_monsters
    -> set_guard_radius(radius: 12.0)
    -> require
# Spawn authored loot and register checkpoints.
spawned_loot = loot_spawn_locations
    -> spawn_loot(difficulty: map_difficulty, seed: loot_seed)
    -> require
spawned_loot -> count -> print
checkpoint_locations
    -> register_checkpoint(game_rules <- game_rules)
    -> require
first_checkpoint = checkpoint_locations
    -> sort(by: .spawn_order)
    -> first
    -> require
game_rules
    -> set_initial_checkpoint(checkpoint <- first_checkpoint)
    -> require
# Spawn the player. The command returns player and camera outputs.
player_spawn_output = chosen_player_spawn
    -> spawn_player(
        player <- local_player,
        world <- world,
        facing: MarkerAngles,
        protection_seconds: 3.0
    )
    -> require
spawned_player = player_spawn_output.player
player_camera = player_spawn_output.camera
spawned_player.name -> print
spawned_player.position -> print
player_camera -> set_camera_mode(mode: FirstPerson) -> require
player_camera -> fade_from_black(seconds: 1.25) -> require
# Configure the player only through registered commands.
spawned_player -> set_max_health(amount: 100) -> require
spawned_player -> heal(amount: 100) -> require
spawned_player -> set_max_armor(amount: 100) -> require
spawned_player -> give_armor(amount: 25) -> require
spawned_player -> set_inventory_capacity(slots: 32) -> require
spawned_player -> give_credits(amount: 250) -> require
# Lift over Weapon values and reuse the player port each time.
starter_weapons = [
    Weapon.Crowbar,
    Weapon.Pistol,
    Weapon.Shotgun,
    Weapon.SubmachineGun,
    Weapon.Crossbow
]
starter_weapons
    -> grant_weapon_to(
        player <- spawned_player,
        condition: 1.0,
        source: MapLoadout
    )
    -> require
spawned_player -> give_ammo(ammo: PistolRounds, amount: 90) -> require
spawned_player -> give_ammo(ammo: ShotgunShells, amount: 24) -> require
spawned_player -> give_ammo(ammo: SmgRounds, amount: 150) -> require
spawned_player -> give_ammo(ammo: CrossbowBolts, amount: 12) -> require
spawned_player -> equip_weapon(weapon: Pistol) -> require
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
    -> grant_item_to(
        player <- spawned_player,
        count: 1,
        source: MapLoadout
    )
    -> require
spawned_player -> give_item(item: Medkit, count: 3) -> require
spawned_player -> give_item(item: ArmorBattery, count: 2) -> require
spawned_player -> give_item(item: HandGrenade, count: 4) -> require
spawned_player -> give_item(item: ProximityMine, count: 2) -> require
spawned_player -> give_item(item: EmergencyBeacon, count: 1) -> require
# Create mission objectives.
game_rules -> clear_objectives -> require
game_rules
    -> add_objective(
        id: "reach_control_room",
        title: "Reach the control room",
        priority: Primary
    )
    -> require
game_rules
    -> add_objective(
        id: "restore_power",
        title: "Restore auxiliary power",
        priority: Primary
    )
    -> require
game_rules
    -> add_objective(
        id: "find_survivors",
        title: "Search for survivors",
        priority: Optional
    )
    -> require
game_rules -> activate_objective(id: "reach_control_room") -> require
# Build the map atmosphere.
ambient_emitters = ambient_locations
    -> spawn_ambient_emitter(seed: ambient_seed)
    -> require
ambient_emitters
    -> start_ambient_emitter(fade_seconds: 2.0)
    -> require
weather_profile = map
    -> choose_weather(seed: weather_seed)
    -> require
world -> apply_weather(profile <- weather_profile) -> require
play_music(
    track: "music/map_start_tension",
    fade_seconds: 2.5,
    loop: true
)
    -> require
# Arm the encounter and release the player.
encounter_director
    -> set_player(player <- spawned_player)
    -> require
encounter_director
    -> set_difficulty(difficulty: map_difficulty)
    -> require
encounter_director
    -> arm_encounter(monsters <- spawned_monsters)
    -> require
guard_monsters -> wake_monster(reason: PlayerSpawned) -> require
world -> enable_player_input -> require
world -> resume_simulation(reason: MapBootstrapComplete) -> require
spawned_player
    -> show_message(
        text: "Find the control room. Stay alert.",
        duration_seconds: 5.0
    )
    -> require
set_loading_stage(name: "ready") -> require
final_loot_count = spawned_loot -> count
log_map_started(
    player <- spawned_player,
    map_name: map_name,
    monster_count: spawned_monster_count,
    loot_count: final_loot_count
)
    -> require
```

The example uses no hidden control flow. Descriptor types decide whether an operation consumes a complete array or maps over its elements.
