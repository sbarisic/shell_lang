using ShellLang;
using ShellLangTest;
using System.Globalization;

public static class Program
{
    public static int Main(string[] args)
    {
        var mode = args.Length == 0 ? "--console" : args.Single();
        if (mode is not ("--console" or "--tests" or "--example" or "--all"))
        {
            Console.Error.WriteLine("Usage: shell_lang_test [--console|--tests|--example|--all]");
            return 2;
        }

        try
        {
            if (mode == "--console")
                InteractiveConsole.Run();
            if (mode is "--tests" or "--all")
                Conformance.Run();
            if (mode is "--example" or "--all")
                Example.Run(printTrace: true);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAILED: " + ex.Message);
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }
}

internal static class InteractiveConsole
{
    public static void Run()
    {
        var engine = new ShellEngine();
        var game = new MockGame(Console.WriteLine);
        var registration = game.Register(engine);
        if (!registration.Success)
            throw new InvalidOperationException("Console host registration failed: " + string.Join(Environment.NewLine, registration.Diagnostics));

        var session = new ShellSession();
        Console.WriteLine("ShellLang in-game console");
        Console.WriteLine("Enter an expression and press Enter. Type 'help' for commands or 'exit' to close.");

        while (true)
        {
            Console.Write("> ");
            var source = Console.ReadLine();
            if (source is null || source.Trim() is "exit" or "quit")
                break;
            if (string.IsNullOrWhiteSpace(source))
                continue;
            if (TryShowHelp(engine, source.Trim()))
                continue;

            var compilation = engine.Compile(source, session);
            if (!compilation.IsValid)
            {
                foreach (var diagnostic in compilation.Diagnostics)
                    Console.WriteLine($"{diagnostic.Code} ({diagnostic.Source.Line},{diagnostic.Source.Column}): {diagnostic.Message}");
                continue;
            }

            var result = engine.Execute(compilation, session);
            switch (result.Status)
            {
                case ExecutionStatus.Completed:
                    if (result.Value is not null && result.Value.Value is not ShellResultValue.VoidSuccess)
                        Console.WriteLine(FormatValue(engine, result.Value));
                    break;
                case ExecutionStatus.RuntimeFault:
                    Console.WriteLine($"{result.RuntimeFault!.Code.Value}: {result.RuntimeFault.Message}");
                    break;
                case ExecutionStatus.HostFault:
                    Console.WriteLine($"{result.HostFault!.Code}: {result.HostFault.Message}");
                    break;
            }
        }
    }

    private static bool TryShowHelp(ShellEngine engine, string source)
    {
        if (source == "help")
        {
            Console.WriteLine("Console commands:");
            Console.WriteLine("  help [name]  List commands or show detailed help.");
            Console.WriteLine("  exit         Close the console (quit also works).");
            Console.WriteLine();
            Console.WriteLine("ShellLang commands:");
            foreach (var command in engine.Catalog.Commands)
                Console.WriteLine($"  {command.Name,-28} {command.Description}");
            Console.WriteLine();
            Console.WriteLine("Collection and Result intrinsics:");
            foreach (var intrinsic in engine.Catalog.Intrinsics)
                Console.WriteLine($"  {intrinsic.Name,-28} {intrinsic.Description}");
            Console.WriteLine();
            Console.WriteLine("Use 'help <name>' for details.");
            return true;
        }

        const string prefix = "help ";
        if (!source.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var name = source[prefix.Length..].Trim();
        if (name.Length == 0)
        {
            Console.WriteLine("Usage: help [command]");
            return true;
        }

        var help = FindHelp(engine, name);
        if (help is null)
        {
            Console.WriteLine($"No command or symbol named '{name}'. Type 'help' to list commands.");
            return true;
        }

        PrintHelp(engine, help);
        return true;
    }

    private static HelpItem? FindHelp(ShellEngine engine, string name)
    {
        foreach (var command in engine.Catalog.Commands)
            if (command.Name == name)
                return engine.GetHelp(command.Id);
        foreach (var intrinsic in engine.Catalog.Intrinsics)
            if (intrinsic.Name == name)
                return engine.GetHelp(intrinsic.Id);
        foreach (var global in engine.Catalog.Globals)
            if (global.Name == name)
                return engine.GetHelp(global.Id);
        foreach (var type in engine.Catalog.Types)
            if (type.Name == name)
                return engine.GetHelp(type.SymbolId);
        foreach (var type in engine.Catalog.Enums)
            if (type.Name == name)
                return engine.GetHelp(type.SymbolId);
        foreach (var type in engine.Catalog.Errors)
            if (type.Name == name)
                return engine.GetHelp(type.SymbolId);
        return null;
    }

    private static void PrintHelp(ShellEngine engine, HelpItem help)
    {
        Console.WriteLine($"{help.Name} ({help.Kind})");
        Console.WriteLine(help.Description);
        PrintParameters("Inputs", help.Inputs);
        PrintParameters("Arguments", help.Arguments);
        PrintParameters("Outputs", help.Outputs);
        if (help.ErrorType is { } errorType)
            Console.WriteLine($"Error: {engine.Catalog.GetTypeName(errorType)}");
        if (help.RuntimeFaults.Count > 0)
            Console.WriteLine($"Runtime faults: {string.Join(", ", help.RuntimeFaults.Select(fault => fault.Code.Value))}");
        if (help.Members.Count > 0)
            Console.WriteLine($"Members: {string.Join(", ", help.Members)}");

        void PrintParameters(string heading, IReadOnlyList<HelpParameter> parameters)
        {
            if (parameters.Count == 0)
                return;
            Console.WriteLine(heading + ":");
            foreach (var parameter in parameters)
            {
                var flags = new List<string>();
                if (parameter.IsDefault)
                    flags.Add("default");
                if (!parameter.Required)
                    flags.Add("optional");
                var suffix = flags.Count == 0 ? string.Empty : $" ({string.Join(", ", flags)})";
                Console.WriteLine($"  {parameter.Name}: {engine.Catalog.GetTypeName(parameter.Type)}{suffix} - {parameter.Description}");
            }
        }
    }

    private static string FormatValue(ShellEngine engine, ShellValue value)
    {
        if (value.Value is ShellResultValue.Success success)
            return $"Ok({FormatValue(engine, success.Value)})";
        if (value.Value is ShellResultValue.VoidSuccess)
            return "Ok";
        if (value.Value is ShellResultValue.Error error)
            return $"Err({FormatValue(engine, error.Value)})";

        var typeName = engine.Catalog.GetTypeName(value.Type);
        if (typeName.StartsWith("Array<", StringComparison.Ordinal))
            return $"[{string.Join(", ", engine.GetArrayItems(value).Select(item => FormatValue(engine, item)))}]";

        return value.Value switch
        {
            string text => $"\"{text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"",
            bool boolean => boolean ? "true" : "false",
            float number => number.ToString("0.###", CultureInfo.InvariantCulture),
            double number => number.ToString("0.###", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }
}

internal static class Conformance
{
    private static int _passed;
    public static void Run()
    {
        Test("literals, assignments, and operators", LiteralsAndOperators);
        Test("multiline continuation and hard semicolon", Continuation);
        Test("arrays and reducers", ArraysAndReducers);
        Test("empty reducer and require fault", EmptyRequire);
        Test("registration is atomic", AtomicRegistration);
        Test("catalog and session requirements", Revisions);
        Test("help and completion", Metadata);
        Test("console print output", PrintOutput);
        Test("terminal lifting, fault containment, and evaluation counts", AdvancedConformance.Run);
        Test("full 280-line map bootstrap", ExampleAssertions);
        Console.WriteLine($"Conformance: {_passed} suites passed.");
    }

    private static void LiteralsAndOperators()
    {
        var (engine, _, session) = Fixture();
        var compilation = engine.Compile("x = 10\ny = 2\n(x + y * 5) == 20", session);
        Valid(compilation);
        var result = engine.Execute(compilation, session);
        Equal(ExecutionStatus.Completed, result.Status);
        Equal(true, result.Value!.Get<bool>());
        True(session.TryGetBinding("x", out var x));
        Equal(10, x.Get<int>());
        result = engine.Execute(engine.Compile("local_player.name_length()", session), session);
        Equal(6, result.Value!.Get<int>());
    }

    private static void Continuation()
    {
        var (engine, _, session) = Fixture();
        var multiline = engine.Compile("map\n  .name\n  -> print", session);
        Valid(multiline);
        Equal(ExecutionStatus.Completed, engine.Execute(multiline, session).Status);
        var hard = engine.Compile("map;\n -> print", session);
        True(!hard.IsValid, "A semicolon must prevent pipeline continuation.");
    }

    private static void ArraysAndReducers()
    {
        var (engine, _, session) = Fixture();
        var result = Run(engine, session, "[1, 2, 3, 4] -> sum");
        Equal(10, result.Value!.Get<int>());
        result = Run(engine, session, "[3, 1, 2] -> min -> require");
        Equal(1, result.Value!.Get<int>());
        result = Run(engine, session, "find_entities(classname: \"info_spawn\") -> where(.spawn_order > 1) -> count");
        Equal(2, result.Value!.Get<int>());
    }

    private static void EmptyRequire()
    {
        var (engine, _, session) = Fixture();
        var result = Run(engine, session, "find_entities(classname: \"missing\") -> first -> require");
        Equal(ExecutionStatus.RuntimeFault, result.Status);
        Equal("SL4001", result.RuntimeFault!.Code.Value);
    }

    private static void AtomicRegistration()
    {
        var (engine, _, _) = Fixture();
        var revision = engine.CatalogRevision;
        var duplicate = new CommandDescriptor("print", "Duplicate.", null, null, null, (_, _) => CommandOutcome.Success.Empty);
        var registration = engine.Register(new DescriptorSet(commands: [duplicate]));
        True(!registration.Success);
        Equal(revision, engine.CatalogRevision);
    }

    private static void Revisions()
    {
        var (engine, _, session) = Fixture();
        Run(engine, session, "required = 1");
        var compilation = engine.Compile("required + 1", session);
        Valid(compilation);
        session.SetBinding("required", engine.CreateValue(engine.Core.Int32, 9));
        Equal(ExecutionStatus.Completed, engine.Execute(compilation, session).Status);
        session.SetBinding("required", engine.CreateValue(engine.Core.String, "changed"));
        Equal(ExecutionStatus.HostFault, engine.Execute(compilation, session).Status);
        var fresh = engine.Compile("map.name", session);
        Valid(fresh);
        True(engine.Register(new DescriptorSet()).Success);
        Equal(ExecutionStatus.HostFault, engine.Execute(fresh, session).Status);
    }

    private static void Metadata()
    {
        var (engine, _, session) = Fixture();
        True(engine.GetCompletions("spa", 3, session).Items.Any(x => x.InsertionText == "spawn_player"));
        var command = engine.Catalog.Commands.Single(x => x.Name == "spawn_player");
        var help = engine.GetHelp(command.Id);
        Equal("spawn_player", help!.Name);
        Equal(3, help.Inputs.Count);
        True(engine.GetCompletions("map.na", 6, session).Items.Any(x => x.InsertionText == "name"));
    }

    private static void PrintOutput()
    {
        var output = new List<string>();
        var engine = new ShellEngine();
        var game = new MockGame(output.Add);
        var registration = game.Register(engine);
        True(registration.Success, string.Join(Environment.NewLine, registration.Diagnostics));
        var session = new ShellSession();

        var result = Run(engine, session, "\"Hello world\" -> print");
        Equal(ExecutionStatus.Completed, result.Status);
        True(result.Value is null, "A non-fallible print command must remain terminal Void.");
        Equal(1, output.Count);
        Equal("Hello world", output[0]);

        output.Clear();
        result = Run(engine, session, "[\"one\", \"two\"] -> print");
        Equal(ExecutionStatus.Completed, result.Status);
        True(result.Value is null, "Printing an array must remain terminal Void.");
        Equal(1, output.Count);
        Equal("[one, two]", output[0]);
    }

    private static void ExampleAssertions() => Example.Run(printTrace: false);

    private static (ShellEngine Engine, MockGame Game, ShellSession Session) Fixture()
    {
        var engine = new ShellEngine();
        var game = new MockGame();
        var registration = game.Register(engine);
        if (!registration.Success)
            throw new InvalidOperationException(string.Join(Environment.NewLine, registration.Diagnostics));
        return (engine, game, new ShellSession());
    }

    private static ExecutionResult Run(ShellEngine engine, ShellSession session, string source)
    {
        var compilation = engine.Compile(source, session);
        Valid(compilation);
        var result = engine.Execute(compilation, session);
        if (result.Status == ExecutionStatus.HostFault)
            throw new InvalidOperationException($"{result.HostFault!.Code}: {result.HostFault.Message}", result.HostFault.Exception);
        return result;
    }
    private static void Valid(ShellCompilation compilation)
    {
        if (!compilation.IsValid)
            throw new InvalidOperationException(string.Join(Environment.NewLine, compilation.Diagnostics));
    }
    private static void Test(string name, Action action)
    {
        action();
        _passed++;
        Console.WriteLine("PASS " + name);
    }
    private static void True(bool condition, string? message = null)
    {
        if (!condition)
            throw new InvalidOperationException(message ?? "Expected true.");
    }
    private static void Equal<T>(T expected, T actual) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, received {actual}.");
    }
}

internal static class AdvancedConformance
{
    public static void Run()
    {
        var engine = new ShellEngine();
        var game = new MockGame();
        Good(game.Register(engine));
        var core = engine.Core;
        ShellTypeId Type(string name) => engine.Catalog.Types.FirstOrDefault(x => x.Name == name)?.Id
            ?? engine.Catalog.Enums.FirstOrDefault(x => x.Name == name)?.Id
            ?? engine.Catalog.Errors.First(x => x.Name == name).Id;
        var marker = Type("MapMarker");
        var spawnError = Type("SpawnError");
        var spawnOutput = engine.Catalog.Commands.Single(x => x.Name == "spawn_player").OutputRecordType!.Value;
        var baseActor = new TypeDescriptor("BaseActor", "Base actor.", typeof(BaseActor), new ValueAdapter<BaseActor>());
        var derivedActor = new TypeDescriptor("DerivedActor", "Derived actor.", typeof(DerivedActor), new ValueAdapter<DerivedActor>(), [baseActor.Id]);
        Good(engine.Register(new DescriptorSet(types: [baseActor, derivedActor])));
        True(engine.Catalog.IsAssignable(derivedActor.Id, baseActor.Id));
        True(engine.Catalog.IsAssignable(engine.Catalog.ArrayOf(derivedActor.Id), engine.Catalog.ArrayOf(baseActor.Id)));
        True(engine.Catalog.IsAssignable(engine.Catalog.ResultOf(derivedActor.Id, spawnError), engine.Catalog.ResultOf(baseActor.Id, engine.Core.Error)));
        True(engine.Catalog.IsAssignable(derivedActor.Id, engine.Core.Any));
        True(!engine.Catalog.IsAssignable(engine.Core.Any, derivedActor.Id));
        var touches = 0;
        var fallibleTouches = 0;
        var faultTouches = 0;
        var samples = 0;
        var boolCalls = 0;
        InputPortDescriptor Primary() => new("marker", "Marker.", marker, true);
        CommandOutcome.Success MarkerValue(InvocationValues values) => CommandOutcome.Success.Single("value", values.GetInput("marker"));
        var commands = new List<CommandDescriptor>
        {
            new("touch_marker", "Touch marker.", [Primary()], null, null, (_, _) => { touches++; return CommandOutcome.Success.Empty; }),
            new("try_marker", "Fallible terminal marker.", [Primary()], null, null, (_, _) =>
            {
                fallibleTouches++;
                return fallibleTouches == 2
                    ? new CommandOutcome.Error(engine.CreateValue(spawnError, new GameFailure("second marker failed")))
                    : CommandOutcome.Success.Empty;
            }, spawnError),
            new("try_marker_ok", "Successful fallible terminal marker.", [Primary()], null, null, (_, _) => CommandOutcome.Success.Empty, spawnError),
            new("fault_marker", "Faulting marker value.", [Primary()], null, [new OutputPortDescriptor("value", "Marker.", marker)], (_, values) =>
            {
                faultTouches++; return faultTouches == 2 ? new CommandOutcome.Fault(new RuntimeFaultCode("GAME1001"), "deliberate marker fault") : MarkerValue(values);
            }, runtimeFaults: [new RuntimeFaultCode("GAME1001")]),
            new("explode_marker", "Throw from host.", [Primary()], null, null, (_, _) => throw new InvalidOperationException("boom")),
            new("undeclared_fault", "Return undeclared fault.", [Primary()], null, null, (_, _) => new CommandOutcome.Fault(new RuntimeFaultCode("OTHER1001"), "bad")),
            new("sample_once", "Count argument evaluation.", null, null, [new OutputPortDescriptor("value", "Count.", core.Int32)], (_, _) =>
                CommandOutcome.Success.Single("value", engine.CreateValue(core.Int32, ++samples))),
            new("touch_amount", "Touch with amount.", [Primary()], [new ArgumentDescriptor("amount", "Amount.", core.Int32, 0)], null, (_, _) => { touches++; return CommandOutcome.Success.Empty; }),
            new("load_empty_markers", "Fallible marker load.", null, null,
                [new OutputPortDescriptor("markers", "Markers.", engine.Catalog.ArrayOf(marker))], (_, _) =>
                    CommandOutcome.Success.Single("markers", engine.CreateArray(marker, [])), spawnError),
            new("wrong_output", "Wrong host output.", null, null, [new OutputPortDescriptor("value", "Integer.", core.Int32)], (_, _) =>
                CommandOutcome.Success.Single("value", engine.CreateValue(core.String, "wrong"))),
            new("fail_bool", "Return a typed Boolean error.", null, null, [new OutputPortDescriptor("value", "Boolean.", core.Bool)], (_, _) =>
            { boolCalls++; return new CommandOutcome.Error(engine.CreateValue(spawnError, new GameFailure("boolean failed"))); }, spawnError),
            new("inspect_spawn_output", "Consume a complete spawn output.", [new InputPortDescriptor("output", "Spawn output.", spawnOutput, true)], null,
                [new OutputPortDescriptor("value", "Accepted.", core.Bool)], (_, _) => CommandOutcome.Success.Single("value", engine.CreateValue(core.Bool, true))),
            new("accept_spawned_player", "Consume the default player output.", [new InputPortDescriptor("player", "Player.", Type("Player"), true)], null,
                [new OutputPortDescriptor("value", "Accepted.", core.Bool)], (_, _) => CommandOutcome.Success.Single("value", engine.CreateValue(core.Bool, true)))
        };
        Good(engine.Register(new DescriptorSet(commands: commands)));
        var session = new ShellSession();

        var result = Execute("find_entities(classname: \"info_spawn\") -> touch_marker", session);
        Completed(result);
        Equal(3, touches);
        result = Execute("find_entities(classname: \"missing\") -> touch_amount(amount: sample_once())", session);
        Completed(result);
        Equal(1, samples);
        Equal(3, touches);
        result = Execute("nested = [find_entities(classname: \"info_spawn\"), find_entities(classname: \"info_spawn\")]\nnested -> touch_marker", session);
        Completed(result);
        Equal(9, touches);
        result = Execute("ok = find_entities(classname: \"info_spawn\") -> try_marker_ok", session);
        Completed(result);
        True(session.TryGetBinding("ok", out var ok));
        True(ok.Value is ShellResultValue.VoidSuccess);
        result = Execute("failed = find_entities(classname: \"info_spawn\") -> try_marker", session);
        Completed(result);
        True(session.TryGetBinding("failed", out var failed));
        var error = failed.Value as ShellResultValue.Error;
        True(error is not null);
        Equal(2, fallibleTouches);
        True(error!.Frames.Any(x => x.ArrayIndex == 1));
        result = Execute("outer = load_empty_markers() -> touch_marker", session);
        Completed(result);
        True(session.TryGetBinding("outer", out var outer));
        True(outer.Value is ShellResultValue.VoidSuccess);
        result = Execute("x = \"old\"", session);
        Completed(result);
        faultTouches = 0;
        result = Execute("x = find_entities(classname: \"info_spawn\") -> fault_marker\nx = \"new\"", session);
        Equal(ExecutionStatus.RuntimeFault, result.Status);
        Equal(0, result.CompletedStatementCount);
        Equal("GAME1001", result.RuntimeFault!.Code.Value);
        Equal(1, result.RuntimeFault.Context.Count(x => x.ArrayIndex is not null));
        Equal(1, result.RuntimeFault.Context.Single(x => x.ArrayIndex is not null).ArrayIndex!.Value);
        True(session.TryGetBinding("x", out var old));
        Equal("old", old.Get<string>());
        result = Execute("find_entities(classname: \"info_spawn\") -> explode_marker\nafter = 1", session);
        Equal(ExecutionStatus.HostFault, result.Status);
        True(!session.TryGetBinding("after", out _));
        result = Execute("find_entities(classname: \"info_spawn\") -> undeclared_fault", session);
        Equal(ExecutionStatus.HostFault, result.Status);
        result = Execute("find_entities(classname: \"info_spawn\") -> spawn_monster(director <- encounter_director, difficulty: Hard, faction: Hostile, reason: MapStart, seed: 1, start_awake: false)", session);
        Equal(ExecutionStatus.RuntimeFault, result.Status);
        Equal("GAME1001", result.RuntimeFault!.Code.Value);
        Equal(0, game.SpawnedMonsters);
        result = Execute("wrong_output()", session);
        Equal(ExecutionStatus.HostFault, result.Status);
        result = Execute("([1, 2, 3] + 1) -> sum", session);
        Completed(result);
        Equal(9, result.Value!.Get<int>());
        result = Execute("((find_entities(classname: \"info_spawn\") -> first).spawn_order + 1) -> require", session);
        Completed(result);
        Equal(2, result.Value!.Get<int>());
        result = Execute("(1 + (find_entities(classname: \"missing\") -> first).spawn_order) -> is_ok", session);
        Completed(result);
        Equal(false, result.Value!.Get<bool>());
        result = Execute("(false && fail_bool()) -> require", session);
        Completed(result);
        Equal(false, result.Value!.Get<bool>());
        Equal(0, boolCalls);
        const string spawn = "find_entities(classname: \"info_spawn\") -> choose_random_spawn(seed: 1) -> require -> spawn_player(player <- local_player, world <- world, facing: MarkerAngles, protection_seconds: 1.0) -> require";
        result = Execute(spawn + " -> inspect_spawn_output", session);
        Completed(result);
        Equal(true, result.Value!.Get<bool>());
        result = Execute(spawn + " -> accept_spawned_player", session);
        Completed(result);
        Equal(true, result.Value!.Get<bool>());
        True(!engine.Compile("local_player.password", session).IsValid, "Unregistered CLR members must be hidden.");
        Throws<ArgumentException>(() => engine.Catalog.ArrayOf(core.Void));
        var require = engine.Catalog.Intrinsics.Single(x => x.Name == "require");
        Equal("intrinsic", engine.GetHelp(require.Id)!.Kind);

        ExecutionResult Execute(string source, ShellSession target)
        {
            var compilation = engine.Compile(source, target);
            if (!compilation.IsValid)
                throw new InvalidOperationException(string.Join(Environment.NewLine, compilation.Diagnostics));
            return engine.Execute(compilation, target);
        }
    }

    private static void Good(RegistrationResult result)
    {
        if (!result.Success)
            throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
    }
    private static void Completed(ExecutionResult result)
    {
        if (result.Status != ExecutionStatus.Completed)
            throw new InvalidOperationException(result.HostFault?.Message ?? result.RuntimeFault?.Message);
    }
    private static void True(bool value, string? message = null)
    {
        if (!value)
            throw new InvalidOperationException(message ?? "Expected true.");
    }
    private static void Equal<T>(T expected, T actual) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, received {actual}.");
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}

internal static class Example
{
    public static void Run(bool printTrace)
    {
        var engine = new ShellEngine();
        var game = new MockGame();
        var registration = game.Register(engine);
        if (!registration.Success)
            throw new InvalidOperationException("Example registration failed: " + string.Join(Environment.NewLine, registration.Diagnostics));
        var script = LoadScript();
        var lines = script.Split('\n').Length;
        if (lines != 280)
            throw new InvalidOperationException($"EXAMPLE.md script must contain 280 physical lines; found {lines}.");
        var compilation = engine.Compile(script, new ShellSession());
        if (!compilation.IsValid)
            throw new InvalidOperationException("Example did not compile:" + Environment.NewLine + string.Join(Environment.NewLine, compilation.Diagnostics.Take(30)));
        var session = new ShellSession();
        var result = engine.Execute(engine.Compile(script, session), session);
        if (result.Status != ExecutionStatus.Completed)
            throw new InvalidOperationException($"Example execution failed: {result.RuntimeFault?.Code.Value} {result.RuntimeFault?.Message}{result.HostFault?.Code} {result.HostFault?.Message}");
        if (game.SpawnedMonsters != 4)
            throw new InvalidOperationException($"Expected four spawned monsters, found {game.SpawnedMonsters}.");
        if (game.SpawnedPlayers != 1)
            throw new InvalidOperationException($"Expected one spawned player, found {game.SpawnedPlayers}.");
        if (game.GrantedWeapons.Count != 5)
            throw new InvalidOperationException("Not all starter weapons were granted.");
        if (game.GrantedItems.Count != 6)
            throw new InvalidOperationException("Not all starter items were granted.");
        if (!game.Trace.Any(x => x.StartsWith("set_loading_stage(name: \"ready\") -> Ok<Void>", StringComparison.Ordinal)) ||
            !game.Trace.Any(x => x.StartsWith("log_map_started(player <- Morgan,", StringComparison.Ordinal)))
            throw new InvalidOperationException("The bootstrap did not reach its final effects.");
        if (printTrace)
        {
            Console.WriteLine($"Example completed: {game.SpawnedMonsters} monsters, {game.SpawnedPlayers} player, {game.GrantedWeapons.Count} weapons, {game.GrantedItems.Count} starter items.");
            foreach (var item in game.Trace)
                Console.WriteLine("  " + item);
        }
    }

    private static string LoadScript()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "EXAMPLE.md");
        var text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        const string open = "```text\n";
        var start = text.IndexOf(open, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException("EXAMPLE.md has no text fence.");
        start += open.Length;
        var end = text.IndexOf("\n```", start, StringComparison.Ordinal);
        if (end < 0)
            throw new InvalidOperationException("EXAMPLE.md has no closing fence.");
        return text[start..end];
    }
}
