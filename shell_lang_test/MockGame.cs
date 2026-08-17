using ShellLang;
using System.Globalization;

namespace ShellLangTest;

internal sealed record Vec3(float X, float Y, float Z)
{
    public override string ToString() => $"({X}, {Y}, {Z})";
}
internal sealed record GameMap(string Name, ulong Seed);
internal sealed record GameWorld(string Name);
internal sealed record GameRules(Difficulty Difficulty);
internal sealed record EncounterDirector(string Name);
internal sealed record NavigationSystem(string Name);
internal sealed record MapMarker(string Classname, string Name, Vec3 Position, int SpawnOrder, ulong StableId);
internal sealed record Monster(ulong StableId, MonsterRank Rank, string Group);
internal sealed record Player(string Name, Vec3 Position);
internal sealed record Camera(string Name);
internal sealed record Loot(string Name);
internal sealed record AmbientEmitter(string Name);
internal sealed record WeatherProfile(string Name);
internal sealed record GameFailure(string Message);
internal record BaseActor(string Name);
internal sealed record DerivedActor(string DerivedName) : BaseActor(DerivedName);

internal enum Difficulty
{
    Normal, Hard
}
internal enum WorldTransitionReason
{
    MapBootstrap, MapBootstrapComplete
}
internal enum RespawnPolicy
{
    Checkpoint
}
internal enum MonsterFaction
{
    Hostile
}
internal enum SpawnReason
{
    MapStart
}
internal enum MonsterRank
{
    Boss, Elite, Normal
}
internal enum SpawnFacing
{
    MarkerAngles
}
internal enum CameraMode
{
    FirstPerson
}
internal enum GrantSource
{
    MapLoadout
}
internal enum Weapon
{
    Crowbar, Pistol, Shotgun, SubmachineGun, Crossbow
}
internal enum AmmoType
{
    PistolRounds, ShotgunShells, SmgRounds, CrossbowBolts
}
internal enum Item
{
    Flashlight, Binoculars, MapScanner, RepairTool, FieldRadio, AccessCardBlue, Medkit, ArmorBattery, HandGrenade, ProximityMine, EmergencyBeacon
}
internal enum ObjectivePriority
{
    Primary, Optional
}
internal enum WakeReason
{
    PlayerSpawned
}

internal sealed class MockGame
{
    private readonly Dictionary<string, ShellTypeId> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShellTypeId> _errors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<MapMarker>> _markers;
    private readonly Action<string>? _output;
    private ShellEngine _engine = null!;
    public List<string> Trace { get; } = [];
    public int SpawnedMonsters
    {
        get; private set;
    }
    public int SpawnedPlayers
    {
        get; private set;
    }
    public HashSet<Weapon> GrantedWeapons { get; } = [];
    public HashSet<Item> GrantedItems { get; } = [];

    public MockGame(Action<string>? output = null)
    {
        _output = output;
        MapMarker M(string kind, string name, int order, ulong id) => new(kind, name, new Vec3(order * 10, order, 0), order, id);
        _markers = new Dictionary<string, IReadOnlyList<MapMarker>>(StringComparer.Ordinal)
        {
            ["info_monster_spawn"] = [M("info_monster_spawn", "monster_a", 2, 102), M("info_monster_spawn", "monster_b", 1, 101), M("info_monster_spawn", "monster_c", 3, 103), M("info_monster_spawn", "monster_d", 4, 104)],
            ["info_spawn"] = [M("info_spawn", "player_a", 1, 201), M("info_spawn", "player_b", 2, 202), M("info_spawn", "player_c", 3, 203)],
            ["info_loot_spawn"] = [M("info_loot_spawn", "loot_a", 1, 301), M("info_loot_spawn", "loot_b", 2, 302)],
            ["info_checkpoint"] = [M("info_checkpoint", "checkpoint_a", 1, 401), M("info_checkpoint", "checkpoint_b", 2, 402)],
            ["info_patrol"] = [M("info_patrol", "patrol_a", 1, 501), M("info_patrol", "patrol_b", 2, 502)],
            ["info_ambient"] = [M("info_ambient", "ambient_a", 1, 601), M("info_ambient", "ambient_b", 2, 602)]
        };
    }

    public RegistrationResult Register(ShellEngine engine)
    {
        _engine = engine;
        var core = engine.Core;
        var enums = new List<EnumTypeDescriptor>
        {
            Enum<Difficulty>("Difficulty"), Enum<WorldTransitionReason>("WorldTransitionReason"), Enum<RespawnPolicy>("RespawnPolicy"),
            Enum<MonsterFaction>("MonsterFaction"), Enum<SpawnReason>("SpawnReason"), Enum<MonsterRank>("MonsterRank"),
            Enum<SpawnFacing>("SpawnFacing"), Enum<CameraMode>("CameraMode"), Enum<GrantSource>("GrantSource"), Enum<Weapon>("Weapon"),
            Enum<AmmoType>("AmmoType"), Enum<Item>("Item"), Enum<ObjectivePriority>("ObjectivePriority"), Enum<WakeReason>("WakeReason")
        };

        var types = new List<TypeDescriptor>
        {
            Host<GameMap>("GameMap", b => b.Member("name", "Map name.", core.String, x => x.Name).Member("seed", "Map seed.", core.UInt64, x => x.Seed)),
            Host<GameWorld>("GameWorld"),
            Host<GameRules>("GameRules", b => b.Member("difficulty", "Difficulty.", EnumId<Difficulty>("Difficulty"), x => x.Difficulty)),
            Host<EncounterDirector>("EncounterDirector"), Host<NavigationSystem>("NavigationSystem"),
            Host<Vec3>("Vector3"),
            Host<MapMarker>("MapMarker", b => b.Member("classname", "Editor classname.", core.String, x => x.Classname)
                .Member("name", "Marker name.", core.String, x => x.Name).Member("position", "Marker position.", TypeId("Vector3"), x => x.Position)
                .Member("spawn_order", "Spawn order.", core.Int32, x => x.SpawnOrder).Member("stable_id", "Stable id.", core.UInt64, x => x.StableId)),
            Host<Monster>("Monster", b => b.Member("rank", "Monster rank.", EnumId<MonsterRank>("MonsterRank"), x => x.Rank)
                .Member("group", "Monster group.", core.String, x => x.Group).Member("stable_id", "Stable id.", core.UInt64, x => x.StableId)),
            Host<Player>("Player", b => b.Member("name", "Player name.", core.String, x => x.Name).Member("position", "Player position.", TypeId("Vector3"), x => x.Position)
                .Query("name_length", "Length of the player name.", null, core.Int32, (_, player, _) => player.Name.Length)),
            Host<Camera>("Camera"), Host<Loot>("Loot"), Host<AmbientEmitter>("AmbientEmitter"), Host<WeatherProfile>("WeatherProfile")
        };

        var errors = new List<ErrorTypeDescriptor>();
        foreach (var name in new[] { "GameError", "WorldError", "NavigationError", "SpawnError", "EncounterError", "InventoryError", "ObjectiveError", "MapError", "PlayerError", "UIError", "AudioError", "TelemetryError" })
        {
            var descriptor = new ErrorTypeDescriptor(name, $"{name} value.", typeof(GameFailure), new ValueAdapter<GameFailure>(),
                name == "GameError" ? core.Error : _errors["GameError"]);
            _errors[name] = descriptor.Id;
            errors.Add(descriptor);
        }

        var globals = new[]
        {
            Global("map", "GameMap", new GameMap("research_complex", 0x1234_5678UL)),
            Global("world", "GameWorld", new GameWorld("world")),
            Global("local_player", "Player", new Player("Morgan", new Vec3(0, 0, 0))),
            Global("game_rules", "GameRules", new GameRules(Difficulty.Hard)),
            Global("encounter_director", "EncounterDirector", new EncounterDirector("director")),
            Global("navigation", "NavigationSystem", new NavigationSystem("navigation"))
        };

        var commands = BuildCommands();
        var fault = new RuntimeFaultDescriptor(new RuntimeFaultCode("GAME1001"), "MapMarkerKindMismatch", "A marker command received the wrong classname.");
        return engine.Register(new DescriptorSet(types, enums, errors, globals, commands, [fault]));
    }

    private TypeDescriptor Host<T>(string name, Func<TypeDescriptorBuilder<T>, TypeDescriptorBuilder<T>>? configure = null) where T : notnull
    {
        var builder = TypeDescriptorBuilder.For<T>(name).Description($"Mock {name}.");
        var descriptor = (configure?.Invoke(builder) ?? builder).Build();
        _types[name] = descriptor.Id;
        return descriptor;
    }

    private EnumTypeDescriptor Enum<T>(string name) where T : struct, Enum
    {
        var adapter = new ValueAdapter<T>();
        var descriptor = new EnumTypeDescriptor(name, $"{name} values.", typeof(T), adapter,
            System.Enum.GetValues<T>().Select(x => new EnumMemberDescriptor(x.ToString(), x)),
            new OrderingDescriptor((a, b) => Comparer<T>.Default.Compare((T)a, (T)b)));
        _types[name] = descriptor.Id;
        return descriptor;
    }

    private ShellTypeId EnumId<T>(string name) where T : struct, Enum
    {
        return TypeId(name);
    }

    private ShellTypeId TypeId(string name) => _types[name];
    private GlobalDescriptor Global<T>(string name, string type, T value) where T : notnull =>
        new(name, $"Mock {name} global.", TypeId(type), context => context.Engine.CreateValue(TypeId(type), value));
    private ShellValue V(string type, object value) => _engine.CreateValue(TypeId(type), value);
    private ShellValue E(string type, string message) => _engine.CreateValue(_errors[type], new GameFailure(message));

    private IReadOnlyList<CommandDescriptor> BuildCommands()
    {
        var c = new List<CommandDescriptor>();
        var core = _engine.Core;
        InputPortDescriptor I(string name, string type, bool primary = false) => new(name, $"{name} input.", TypeId(type), primary);
        ArgumentDescriptor A(string name, ShellTypeId type, int position) => new(name, $"{name} argument.", type, position);
        OutputPortDescriptor O(string name, string type, bool primary = false) => new(name, $"{name} output.", TypeId(type), primary);
        OutputPortDescriptor OC(string name, ShellTypeId type, bool primary = false) => new(name, $"{name} output.", type, primary);
        CommandDescriptor Add(string name, IEnumerable<InputPortDescriptor>? inputs, IEnumerable<ArgumentDescriptor>? args,
            IEnumerable<OutputPortDescriptor>? outputs, string? error, CommandInvoker invoke, bool markerFault = false)
        {
            var inputList = (inputs ?? []).ToArray();
            var argumentList = (args ?? []).ToArray();
            var outputList = (outputs ?? []).ToArray();
            CommandInvoker traced = (context, values) =>
            {
                try
                {
                    var outcome = invoke(context, values);
                    Trace.Add(FormatInvocation(name, inputList, argumentList, outputList, error, context, values, outcome));
                    return outcome;
                }
                catch (Exception exception)
                {
                    Trace.Add($"{FormatCall(name, inputList, argumentList, context, values)} -> HostFault({exception.GetType().Name})");
                    throw;
                }
            };
            var cmd = new CommandDescriptor(name, $"Mock {name} command.", inputList, argumentList, outputList, traced,
                error is null ? null : _errors[error], markerFault ? [new RuntimeFaultCode("GAME1001")] : null);
            c.Add(cmd);
            return cmd;
        }
        CommandOutcome.Success One(string name, ShellValue value) => CommandOutcome.Success.Single(name, value);
        CommandInvoker Fluent(string input = "target", string output = "value", Action<InvocationContext, InvocationValues>? effect = null) => (ctx, values) =>
        {
            effect?.Invoke(ctx, values);
            return One(output, values.GetInput(input));
        };
        CommandInvoker Effect(Action<InvocationValues>? effect = null) => (_, values) => { effect?.Invoke(values); return CommandOutcome.Success.Empty; };
        bool Marker(InvocationValues values, string input, string classname, out CommandOutcome.Fault? fault)
        {
            var marker = values.GetInput<MapMarker>(input);
            fault = marker.Classname == classname ? null : new(new RuntimeFaultCode("GAME1001"), $"Expected {classname}, received {marker.Classname}.");
            return fault is null;
        }

        Add("print", [new("value", "Value to print.", core.Any, true)], null, null, null, (_, values) =>
        {
            _output?.Invoke(values.GetInput("value").ToString());
            return CommandOutcome.Success.Empty;
        });
        Add("set_loading_stage", null, [A("name", core.String, 0)], null, "WorldError", Effect());
        Add("derive_seed", null, [A("base", core.UInt64, 0), A("channel", core.String, 1)], [OC("seed", core.UInt64)], null, (_, v) =>
        {
            var hash = v.GetArgument<ulong>("base");
            foreach (var ch in v.GetArgument<string>("channel"))
                hash = unchecked((hash ^ ch) * 1099511628211UL);
            return One("seed", _engine.CreateValue(core.UInt64, hash));
        });
        Add("find_entities", null, [A("classname", core.String, 0)], [OC("markers", _engine.Catalog.ArrayOf(TypeId("MapMarker")))], null, (_, v) =>
        {
            var name = v.GetArgument<string>("classname");
            return One("markers", _engine.CreateArray(TypeId("MapMarker"), _markers.GetValueOrDefault(name, []).Select(x => V("MapMarker", x))));
        });

        AddFluentWorld(c, Add, I, A, O, Fluent);
        Add("validate_player_spawns", [new("markers", "Markers.", _engine.Catalog.ArrayOf(TypeId("MapMarker")), true), I("navigation", "NavigationSystem")], null, [OC("markers", _engine.Catalog.ArrayOf(TypeId("MapMarker")))], "NavigationError",
            (_, v) => ValidateMarkerArray(v.GetInput("markers"), "info_spawn") is { } fault ? fault : One("markers", v.GetInput("markers")), true);
        Add("validate_monster_spawns", [new("markers", "Markers.", _engine.Catalog.ArrayOf(TypeId("MapMarker")), true), I("navigation", "NavigationSystem")], null,
            [OC("markers", _engine.Catalog.ArrayOf(TypeId("MapMarker")))], "NavigationError", (_, v) => ValidateMarkerArray(v.GetInput("markers"), "info_monster_spawn") is { } fault ? fault : One("markers", v.GetInput("markers")), true);
        Add("validate_navigation", [I("navigation", "NavigationSystem", true), I("map", "GameMap")], null,
            [OC("summary", core.String), OC("is_complete", core.Bool)], "NavigationError", (_, _) => new CommandOutcome.Success(new Dictionary<string, ShellValue>
            {
                ["summary"] = _engine.CreateValue(core.String, "Navigation ready."),
                ["is_complete"] = _engine.CreateValue(core.Bool, true)
            }));
        Add("require_true", [new("value", "Boolean value.", core.Bool, true)], [A("message", core.String, 0)], [OC("value", core.Bool)], "MapError",
            (_, v) => v.GetInput<bool>("value") ? One("value", v.GetInput("value")) : new CommandOutcome.Error(E("MapError", v.GetArgument<string>("message"))));
        Add("activate_navigation", [I("navigation", "NavigationSystem", true), I("map", "GameMap")], null, [O("value", "NavigationSystem")], "NavigationError", Fluent("navigation"));
        Add("choose_random_spawn", [new("markers", "Spawn markers.", _engine.Catalog.ArrayOf(TypeId("MapMarker")), true)], [A("seed", core.UInt64, 0)], [O("marker", "MapMarker")], "SpawnError", (_, v) =>
        {
            if (ValidateMarkerArray(v.GetInput("markers"), "info_spawn") is { } fault)
                return fault;
            var list = _engine.GetArrayItems(v.GetInput("markers"));
            if (list.Count == 0)
                return new CommandOutcome.Error(E("SpawnError", "No player spawn."));
            var index = (int)(v.GetArgument<ulong>("seed") % (ulong)list.Count);
            return One("marker", list.ElementAt(index));
        }, true);

        Add("spawn_monster", [I("marker", "MapMarker", true), I("director", "EncounterDirector")],
            [A("difficulty", TypeId("Difficulty"), 0), A("faction", TypeId("MonsterFaction"), 1), A("reason", TypeId("SpawnReason"), 2), A("seed", core.UInt64, 3), A("start_awake", core.Bool, 4)],
            [O("monster", "Monster")], "SpawnError", (_, v) =>
            {
                if (!Marker(v, "marker", "info_monster_spawn", out var fault))
                    return fault!;
                var marker = v.GetInput<MapMarker>("marker");
                SpawnedMonsters++;
                var rank = marker.SpawnOrder == 1 ? MonsterRank.Boss : marker.SpawnOrder == 2 ? MonsterRank.Elite : MonsterRank.Normal;
                var group = marker.SpawnOrder == 3 ? "ambush" : marker.SpawnOrder == 4 ? "guard" : "roam";
                return One("monster", V("Monster", new Monster(marker.StableId, rank, group)));
            }, true);
        foreach (var name in new[] { "attach_to_encounter", "initialize_monster_ai", "assign_patrol_routes", "set_health_multiplier", "set_damage_multiplier", "give_monster_armor", "set_monster_dormant", "set_guard_radius", "wake_monster" })
        {
            var ports = new List<InputPortDescriptor> { I("monster", "Monster", true) };
            var args = new List<ArgumentDescriptor>();
            if (name == "attach_to_encounter")
                ports.Add(I("director", "EncounterDirector"));
            if (name == "initialize_monster_ai")
            {
                ports.Add(I("navigation", "NavigationSystem"));
                args.Add(A("seed", core.UInt64, 0));
            }
            if (name == "assign_patrol_routes")
            {
                ports.Add(new("routes", "Routes.", _engine.Catalog.ArrayOf(TypeId("MapMarker"))));
                args.Add(A("seed", core.UInt64, 0));
            }
            if (name is "set_health_multiplier" or "set_damage_multiplier")
                args.Add(A("multiplier", core.Float32, 0));
            if (name == "give_monster_armor")
                args.Add(A("amount", core.Int32, 0));
            if (name == "set_monster_dormant")
                args.Add(A("dormant", core.Bool, 0));
            if (name == "set_guard_radius")
                args.Add(A("radius", core.Float32, 0));
            if (name == "wake_monster")
                args.Add(A("reason", TypeId("WakeReason"), 0));
            var invoker = name == "assign_patrol_routes"
                ? new CommandInvoker((_, v) => ValidateMarkerArray(v.GetInput("routes"), "info_patrol") is { } fault ? fault : One("value", v.GetInput("monster")))
                : Fluent("monster");
            Add(name, ports, args, [O("value", "Monster")], "EncounterError", invoker, name == "assign_patrol_routes");
        }

        Add("spawn_loot", [I("marker", "MapMarker", true)], [A("difficulty", TypeId("Difficulty"), 0), A("seed", core.UInt64, 1)], [O("loot", "Loot")], "SpawnError",
            (_, v) => Marker(v, "marker", "info_loot_spawn", out var fault) ? One("loot", V("Loot", new Loot(v.GetInput<MapMarker>("marker").Name))) : fault!, true);
        Add("register_checkpoint", [I("marker", "MapMarker", true), I("game_rules", "GameRules")], null, [O("value", "MapMarker")], "MapError",
            (_, v) => Marker(v, "marker", "info_checkpoint", out var fault) ? One("value", v.GetInput("marker")) : fault!, true);
        Add("set_initial_checkpoint", [I("rules", "GameRules", true), I("checkpoint", "MapMarker")], null, [O("value", "GameRules")], "MapError",
            (_, v) => Marker(v, "checkpoint", "info_checkpoint", out var fault) ? One("value", v.GetInput("rules")) : fault!, true);
        Add("spawn_player", [I("marker", "MapMarker", true), I("player", "Player"), I("world", "GameWorld")], [A("facing", TypeId("SpawnFacing"), 0), A("protection_seconds", core.Float32, 1)],
            [O("player", "Player", true), O("camera", "Camera")], "SpawnError", (_, v) =>
            {
                if (!Marker(v, "marker", "info_spawn", out var fault))
                    return fault!;
                var marker = v.GetInput<MapMarker>("marker");
                SpawnedPlayers++;
                return new CommandOutcome.Success(new Dictionary<string, ShellValue> { ["player"] = V("Player", v.GetInput<Player>("player") with { Position = marker.Position }), ["camera"] = V("Camera", new Camera("main")) });
            }, true);

        AddPlayerCommands(Add, I, A, O, Fluent, core);
        Add("grant_weapon_to", [I("weapon", "Weapon", true), I("player", "Player")], [A("condition", core.Float32, 0), A("source", TypeId("GrantSource"), 1)], [O("value", "Weapon")], "InventoryError",
            Fluent("weapon", effect: (_, v) => GrantedWeapons.Add(v.GetInput<Weapon>("weapon"))));
        Add("grant_item_to", [I("item", "Item", true), I("player", "Player")], [A("count", core.Int32, 0), A("source", TypeId("GrantSource"), 1)], [O("value", "Item")], "InventoryError",
            Fluent("item", effect: (_, v) => GrantedItems.Add(v.GetInput<Item>("item"))));

        Add("spawn_ambient_emitter", [I("marker", "MapMarker", true)], [A("seed", core.UInt64, 0)], [O("emitter", "AmbientEmitter")], "SpawnError",
            (_, v) => Marker(v, "marker", "info_ambient", out var fault) ? One("emitter", V("AmbientEmitter", new AmbientEmitter(v.GetInput<MapMarker>("marker").Name))) : fault!, true);
        Add("start_ambient_emitter", [I("emitter", "AmbientEmitter", true)], [A("fade_seconds", core.Float32, 0)], [O("value", "AmbientEmitter")], "AudioError", Fluent("emitter"));
        Add("choose_weather", [I("map", "GameMap", true)], [A("seed", core.UInt64, 0)], [O("profile", "WeatherProfile")], "WorldError", (_, _) => One("profile", V("WeatherProfile", new WeatherProfile("storm"))));
        Add("apply_weather", [I("world", "GameWorld", true), I("profile", "WeatherProfile")], null, [O("value", "GameWorld")], "WorldError", Fluent("world"));
        Add("play_music", null, [A("track", core.String, 0), A("fade_seconds", core.Float32, 1), A("loop", core.Bool, 2)], null, "AudioError", Effect());
        Add("set_player", [I("director", "EncounterDirector", true), I("player", "Player")], null, [O("value", "EncounterDirector")], "EncounterError", Fluent("director"));
        Add("set_difficulty", [I("director", "EncounterDirector", true)], [A("difficulty", TypeId("Difficulty"), 0)], [O("value", "EncounterDirector")], "EncounterError", Fluent("director"));
        Add("arm_encounter", [I("director", "EncounterDirector", true), new("monsters", "Monsters.", _engine.Catalog.ArrayOf(TypeId("Monster")))], null, [O("value", "EncounterDirector")], "EncounterError", Fluent("director"));
        Add("log_map_started", [I("player", "Player")], [A("map_name", core.String, 0), A("monster_count", core.Int32, 1), A("loot_count", core.Int32, 2)], null, "TelemetryError", Effect());
        return c;

        CommandOutcome.Fault? ValidateMarkerArray(ShellValue value, string classname)
        {
            var wrong = _engine.GetArrayItems(value).Select(x => x.Get<MapMarker>()).FirstOrDefault(x => x.Classname != classname);
            return wrong is null ? null : new CommandOutcome.Fault(new RuntimeFaultCode("GAME1001"), $"Expected {classname}, received {wrong.Classname}.");
        }
    }

    private string FormatInvocation(string name, IReadOnlyList<InputPortDescriptor> inputs,
        IReadOnlyList<ArgumentDescriptor> arguments, IReadOnlyList<OutputPortDescriptor> outputs,
        string? errorType, InvocationContext context, InvocationValues values, CommandOutcome outcome)
    {
        var call = FormatCall(name, inputs, arguments, context, values);
        return outcome switch
        {
            CommandOutcome.Fault fault => $"{call} -> Fault<{fault.Code.Value}>({Quote(fault.Message)})",
            CommandOutcome.Error error => $"{call} -> Err<{_engine.Catalog.GetTypeName(error.Value.Type)}>({DescribeValue(error.Value)})",
            CommandOutcome.Success => $"{call} -> {FormatSuccess(name, outputs, errorType)}",
            _ => $"{call} -> HostFault(InvalidOutcome)"
        };
    }

    private string FormatCall(string name, IReadOnlyList<InputPortDescriptor> inputs,
        IReadOnlyList<ArgumentDescriptor> arguments, InvocationContext context, InvocationValues values)
    {
        var primary = inputs.FirstOrDefault(x => x.IsDefault);
        var path = string.Concat(context.ArrayIndexPath.Select(index => $"[{index}]"));
        var entries = new List<string>();
        foreach (var input in inputs.Where(x => !x.IsDefault))
            entries.Add($"{input.Name} <- {DescribeValue(values.GetInput(input.Name))}");
        foreach (var argument in arguments.OrderBy(x => x.Position))
            entries.Add($"{argument.Name}: {DescribeValue(values.GetArgument(argument.Name))}");
        var invocation = entries.Count == 0 ? $"{name}{path}" : $"{name}{path}({string.Join(", ", entries)})";
        return primary is null ? invocation : $"{DescribeValue(values.GetInput(primary.Name))} -> {invocation}";
    }

    private string FormatSuccess(string commandName, IReadOnlyList<OutputPortDescriptor> outputs, string? errorType)
    {
        var successType = outputs.Count switch
        {
            0 => "Void",
            1 => _engine.Catalog.GetTypeName(outputs[0].Type),
            _ => $"{ToPascal(commandName)}.Output"
        };
        return errorType is null ? successType : $"Ok<{successType}>";
    }

    private string DescribeValue(ShellValue value)
    {
        if (value.Value is string text)
            return Quote(text);
        if (value.Value is bool boolean)
            return boolean ? "true" : "false";
        if (value.Value is float single)
            return single.ToString("0.###", CultureInfo.InvariantCulture);
        if (value.Value is double number)
            return number.ToString("0.###", CultureInfo.InvariantCulture);
        if (value.Value is MapMarker marker)
            return marker.Name;
        if (value.Value is Monster monster)
            return $"monster#{monster.StableId}";
        if (value.Value is Player player)
            return player.Name;
        if (value.Value is GameMap map)
            return map.Name;
        if (value.Value is GameWorld world)
            return world.Name;
        if (value.Value is EncounterDirector director)
            return director.Name;
        if (value.Value is NavigationSystem navigation)
            return navigation.Name;
        if (value.Value is Camera camera)
            return camera.Name;
        if (value.Value is Loot loot)
            return loot.Name;
        if (value.Value is AmbientEmitter emitter)
            return emitter.Name;
        if (value.Value is WeatherProfile weather)
            return weather.Name;
        if (_engine.Catalog.GetTypeName(value.Type).StartsWith("Array<", StringComparison.Ordinal))
            return $"{_engine.Catalog.GetTypeName(value.Type)}[{_engine.GetArrayItems(value).Count}]";
        if (value.Value is GameFailure failure)
            return Quote(failure.Message);
        return Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? value.ToString();
    }

    private static string Quote(string value) => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    private static string ToPascal(string value) => string.Concat(value.Split('_', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    private void AddFluentWorld(List<CommandDescriptor> _, Func<string, IEnumerable<InputPortDescriptor>?, IEnumerable<ArgumentDescriptor>?, IEnumerable<OutputPortDescriptor>?, string?, CommandInvoker, bool, CommandDescriptor> add,
        Func<string, string, bool, InputPortDescriptor> input, Func<string, ShellTypeId, int, ArgumentDescriptor> arg,
        Func<string, string, bool, OutputPortDescriptor> output, Func<string, string, Action<InvocationContext, InvocationValues>?, CommandInvoker> fluent)
    {
        var core = _engine.Core;
        void F(string name, string type, IEnumerable<ArgumentDescriptor>? args = null) => add(name, [input("target", type, true)], args, [output("value", type, false)], "WorldError", fluent("target", "value", null), false);
        F("pause_simulation", "GameWorld", [arg("reason", TypeId("WorldTransitionReason"), 0)]);
        F("disable_player_input", "GameWorld");
        F("enable_player_input", "GameWorld");
        F("resume_simulation", "GameWorld", [arg("reason", TypeId("WorldTransitionReason"), 0)]);
        F("clear_transient_entities", "GameWorld", [arg("keep_players", core.Bool, 0)]);
        F("set_time_scale", "GameWorld", [arg("scale", core.Float32, 0)]);
        F("set_gravity", "GameWorld", [arg("scale", core.Float32, 0)]);
        F("set_friendly_fire", "GameRules", [arg("enabled", core.Bool, 0)]);
        F("set_respawn_policy", "GameRules", [arg("policy", TypeId("RespawnPolicy"), 0)]);
        F("set_monster_scaling", "GameRules", [arg("difficulty", TypeId("Difficulty"), 0)]);
    }

    private void AddPlayerCommands(Func<string, IEnumerable<InputPortDescriptor>?, IEnumerable<ArgumentDescriptor>?, IEnumerable<OutputPortDescriptor>?, string?, CommandInvoker, bool, CommandDescriptor> add,
        Func<string, string, bool, InputPortDescriptor> input, Func<string, ShellTypeId, int, ArgumentDescriptor> arg,
        Func<string, string, bool, OutputPortDescriptor> output, Func<string, string, Action<InvocationContext, InvocationValues>?, CommandInvoker> fluent, CoreTypeCatalog core)
    {
        void F(string name, string type, string error, IEnumerable<ArgumentDescriptor>? args = null) => add(name, [input("target", type, true)], args, [output("value", type, false)], error, fluent("target", "value", null), false);
        F("set_camera_mode", "Camera", "PlayerError", [arg("mode", TypeId("CameraMode"), 0)]);
        F("fade_from_black", "Camera", "PlayerError", [arg("seconds", core.Float32, 0)]);
        foreach (var name in new[] { "set_max_health", "heal", "set_max_armor", "give_armor" })
            F(name, "Player", "PlayerError", [arg("amount", core.Int32, 0)]);
        F("set_inventory_capacity", "Player", "InventoryError", [arg("slots", core.Int32, 0)]);
        F("give_credits", "Player", "InventoryError", [arg("amount", core.Int32, 0)]);
        F("give_ammo", "Player", "InventoryError", [arg("ammo", TypeId("AmmoType"), 0), arg("amount", core.Int32, 1)]);
        F("equip_weapon", "Player", "InventoryError", [arg("weapon", TypeId("Weapon"), 0)]);
        F("give_item", "Player", "InventoryError", [arg("item", TypeId("Item"), 0), arg("count", core.Int32, 1)]);
        F("show_message", "Player", "UIError", [arg("text", core.String, 0), arg("duration_seconds", core.Float32, 1)]);
        F("clear_objectives", "GameRules", "ObjectiveError");
        F("add_objective", "GameRules", "ObjectiveError", [arg("id", core.String, 0), arg("title", core.String, 1), arg("priority", TypeId("ObjectivePriority"), 2)]);
        F("activate_objective", "GameRules", "ObjectiveError", [arg("id", core.String, 0)]);
    }

}
